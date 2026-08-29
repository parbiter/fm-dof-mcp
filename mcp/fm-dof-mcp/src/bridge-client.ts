import { EventEmitter } from "node:events";
import WebSocket from "ws";

export const DEFAULT_BRIDGE_URL = "ws://127.0.0.1:7777/";
export const GAME_NOT_RUNNING_MESSAGE =
  "game_not_running — launch FM26 with the bridge (scripts/build_bridge.sh && FM26_LAUNCH=1 scripts/deploy_bridge.sh), or wait for boot to finish";

const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;
const CONNECT_TIMEOUT_MS = 5_000;
const MAX_EVENT_BUFFER = 200;
const MIN_BACKOFF_MS = 500;
const MAX_BACKOFF_MS = 15_000;

export interface BridgeEvent {
  event: string;
  data: unknown;
  receivedAt: string;
}

/** Thrown when the bridge cannot be reached at all (connect failure). */
export class BridgeUnavailableError extends Error {
  constructor(cause?: unknown) {
    super(GAME_NOT_RUNNING_MESSAGE);
    this.name = "BridgeUnavailableError";
    if (cause instanceof Error) this.cause = cause;
  }
}

/** Thrown when the bridge answers a request with {ok: false}. */
export class BridgeRpcError extends Error {
  constructor(method: string, bridgeError: string) {
    super(bridgeError);
    this.name = "BridgeRpcError";
    this.method = method;
  }
  method: string;
}

type PendingEntry = {
  resolve: (value: unknown) => void;
  reject: (err: unknown) => void;
  timer: NodeJS.Timeout;
  method: string;
};

type ConnState = "idle" | "connecting" | "open" | "closed";

/**
 * Thin WebSocket client for the FM26 in-game bridge (fm-bridge).
 *
 * - Lazily connects on first use; a single connection is reused for all calls.
 * - Auto-reconnects in the background with capped exponential backoff after a drop.
 * - Correlates requests/replies by numeric id; unmatched/unsolicited messages
 *   (hello, on_idle, on_date_change, on_nav, on_wiretap, ...) are buffered as
 *   events and also emitted live on this object (EventEmitter) so callers can
 *   await the *next* occurrence of a given event.
 */
export class BridgeClient extends EventEmitter {
  private readonly url: string;
  private ws: WebSocket | null = null;
  private state: ConnState = "idle";
  private connectPromise: Promise<void> | null = null;
  private nextId = 1;
  private readonly pending = new Map<number, PendingEntry>();
  private readonly events: BridgeEvent[] = [];
  private reconnectAttempts = 0;
  private reconnectTimer: NodeJS.Timeout | null = null;

  constructor(url: string = process.env.FM_BRIDGE_URL || DEFAULT_BRIDGE_URL) {
    super();
    this.url = url;
    this.setMaxListeners(0);
  }

  /** Returns buffered pushed events (newest last). Optionally drains the buffer. */
  getEvents(clear: boolean): BridgeEvent[] {
    const snapshot = this.events.slice();
    if (clear) this.events.length = 0;
    return snapshot;
  }

  /** Resolves with the payload of the next occurrence of `eventName`, or null on timeout. */
  waitForEvent(eventName: string, timeoutMs: number): Promise<unknown | null> {
    return new Promise((resolve) => {
      let done = false;
      const handler = (data: unknown) => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        this.off(eventName, handler);
        resolve(data);
      };
      const timer = setTimeout(() => {
        if (done) return;
        done = true;
        this.off(eventName, handler);
        resolve(null);
      }, timeoutMs);
      this.on(eventName, handler);
    });
  }

  /** Calls a bridge RPC method and returns its `result` on success, throws on failure. */
  async call(
    method: string,
    args: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_REQUEST_TIMEOUT_MS,
  ): Promise<unknown> {
    await this.ensureConnected();
    const ws = this.ws;
    if (!ws || this.state !== "open") {
      throw new BridgeUnavailableError();
    }
    const id = this.nextId++;
    const payload = JSON.stringify({ id, method, ...args });

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`bridge request '${method}' timed out after ${timeoutMs}ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, method });
      try {
        ws.send(payload);
      } catch (err) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(err);
      }
    });
  }

  /** Lazily (re)connects if needed; resolves once the socket is open. */
  private ensureConnected(): Promise<void> {
    if (this.state === "open") return Promise.resolve();
    if (this.connectPromise) return this.connectPromise;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.connectPromise = this.doConnect().finally(() => {
      this.connectPromise = null;
    });
    return this.connectPromise;
  }

  private doConnect(): Promise<void> {
    this.state = "connecting";
    return new Promise((resolve, reject) => {
      let settled = false;
      let ws: WebSocket;
      try {
        ws = new WebSocket(this.url);
      } catch (err) {
        this.state = "closed";
        settled = true;
        reject(new BridgeUnavailableError(err));
        return;
      }

      const connectTimer = setTimeout(() => {
        if (settled) return;
        settled = true;
        try {
          ws.terminate();
        } catch {
          /* ignore */
        }
        this.state = "closed";
        reject(new BridgeUnavailableError(new Error("connect timeout")));
      }, CONNECT_TIMEOUT_MS);

      ws.on("open", () => {
        if (settled) return;
        settled = true;
        clearTimeout(connectTimer);
        this.ws = ws;
        this.state = "open";
        this.reconnectAttempts = 0;
        resolve();
      });

      ws.on("message", (data) => {
        this.handleMessage(data.toString());
      });

      ws.on("error", (err) => {
        process.stderr.write(`[fm-mcp] bridge socket error: ${(err as Error).message}\n`);
        if (!settled) {
          settled = true;
          clearTimeout(connectTimer);
          this.state = "closed";
          reject(new BridgeUnavailableError(err));
        }
      });

      ws.on("close", () => {
        clearTimeout(connectTimer);
        const wasOpen = this.state === "open";
        this.state = "closed";
        if (this.ws === ws) this.ws = null;
        if (wasOpen) {
          this.failAllPending(new Error("bridge connection closed"));
          this.scheduleReconnect();
        }
        if (!settled) {
          settled = true;
          reject(new BridgeUnavailableError(new Error("closed before open")));
        }
      });
    });
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;
    const attempt = this.reconnectAttempts++;
    const backoff = Math.min(MIN_BACKOFF_MS * 2 ** attempt, MAX_BACKOFF_MS);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      // Best-effort background reconnect; failures just reschedule via close/error.
      this.ensureConnected().catch(() => {
        this.scheduleReconnect();
      });
    }, backoff);
    this.reconnectTimer.unref?.();
  }

  private failAllPending(err: unknown): void {
    for (const [id, entry] of this.pending) {
      clearTimeout(entry.timer);
      entry.reject(err);
      this.pending.delete(id);
    }
  }

  private handleMessage(text: string): void {
    let msg: any;
    try {
      msg = JSON.parse(text);
    } catch {
      process.stderr.write(`[fm-mcp] dropped unparseable message: ${text.slice(0, 200)}\n`);
      return;
    }

    const id = typeof msg?.id === "number" ? msg.id : null;
    if (id !== null && this.pending.has(id)) {
      const entry = this.pending.get(id)!;
      this.pending.delete(id);
      clearTimeout(entry.timer);
      if (msg.ok === false) {
        entry.reject(new BridgeRpcError(entry.method, String(msg.error ?? "unknown bridge error")));
      } else {
        entry.resolve(msg.result ?? {});
      }
      return;
    }

    // Unsolicited/pushed message: hello, on_idle, on_date_change, on_nav, on_wiretap, ...
    const eventName = typeof msg?.event === "string" ? msg.event : "message";
    const eventData = msg?.data !== undefined ? msg.data : msg;
    const record: BridgeEvent = {
      event: eventName,
      data: eventData,
      receivedAt: new Date().toISOString(),
    };
    this.events.push(record);
    while (this.events.length > MAX_EVENT_BUFFER) this.events.shift();
    this.emit(eventName, eventData);
  }
}
