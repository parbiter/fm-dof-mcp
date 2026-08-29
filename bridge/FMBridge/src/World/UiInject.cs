using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace FMBridge.World;

/// <summary>
/// In-game chat surface for the Director of Football: a "Chat with DoF"
/// entry injected into FM26's Recruitment nav dropdown, and the chat overlay
/// panel itself (header with New chat/close, scrollable message bubbles, a
/// working input row) attached to the PanelManager root. UI-addition only —
/// nothing here reads or drives the game's own screens, saves, or continues.
/// The overlay is the front-end half of the in-game chat: a host-side
/// service (scripts/dof_chat_service.mjs) drives it over the ui_inject verb,
/// posting replies with overlay_post and draining player-typed text with
/// overlay_poll.
/// </summary>
internal static class UiInject
{
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // === Menu injection: "Chat with DoF" under the Recruitment nav dropdown ===
    //
    // Recon (live): the Recruitment dropdown's chevron only opens
    // on a genuine OS-level pointer click. Its item list is NOT a child
    // of MainNavigationBar/NavTabDropdown — it renders into a root-level
    // overlay layer shared by every dropdown/tooltip in the game:
    //   PanelManager-container > floating-element-root (depth1, full-panel
    //   sized, sibling of Background/Menu/PortalScreen/LoadingScreen) >
    //   floating-element-mouse-over-padding > ... > "View" (the actual item
    //   list container, flex column) > one wrapper per row at a ~30px pitch:
    //     LeftIconIndicator, BindingExpect > BindingRemapper > SIText (+
    //     CardIconVisible), RightIconIndicator, ChildDropdown > DropdownArrow,
    //     Tooltip > TooltipLabel.
    // Because floating-element-root is reused for every dropdown/tooltip in
    // the game, "a populated View exists under it" is NOT enough to identify
    // *this* dropdown's list — FindRecruitmentMenuList additionally checks the
    // row texts against RecruitmentMenuFingerprint before accepting a match.
    //
    // Persistence check — SUPERSEDED (live-verification correction
    // after deploy): the recon note that used to be here ("built
    // once and kept alive") was WRONG. Live verification after an actual
    // deploy found the floating popup IS destroyed on dropdown close and
    // rebuilt from scratch on every open (a post-close tree probe for
    // "floating" found 0 rows). So Tick() below is the PRIMARY attach
    // mechanism on every single open, not a defensive fallback — every open
    // gets a brand-new empty "View" that races the native code to populate
    // it, and this class's insert-at-index-0 + overflow-driven height growth
    // are both designed around that race (see TryInjectMenuItem/Tick docs).
    internal const string MenuItemName = "fm-dof-menuitem";
    private const string MenuItemLabel = "Chat with DoF";
    private const string FloatingRootName = "floating-element-root";
    private const string FloatingPaddingName = "floating-element-mouse-over-padding";
    private const string MenuListNodeName = "View";
    private const float DefaultMenuPitch = 30f;

    // Row texts unique to the Recruitment dropdown (captured live), used only
    // to disambiguate its "View" from any other dropdown/tooltip's "View"
    // that might also be parked (hidden) under the shared floating-element-root.
    private static readonly string[] RecruitmentMenuFingerprint =
        { "Player Recommendations", "Recruitment Focuses", "Recruitment Objectives" };

    // Native-look fallback, pixel-sampled from a live screenshot
    // (popup bg #20232F; native item glyph-core average RGB (204,193,243)) —
    // used only until a real native sibling row exists to copy from (see
    // MaybeCopyNativeStyle, which always prefers the live sibling's
    // resolvedStyle over this hardcoded guess).
    private static readonly Color FallbackMenuTextColor = new Color(0xCC / 255f, 0xC1 / 255f, 0xF3 / 255f, 1f);
    private const float FallbackMenuFontSize = 13f;
    // rgba(0,0,0,0) — transparent at rest, matches the native rows (no visible
    // background box until hovered).
    private static readonly Color MenuBaseColor = new Color(0f, 0f, 0f, 0f);
    // rgba(135,25,178,0.25) — restrained hover fill, kept deliberately
    // faint rather than risk looking foreign next to the native rows (the
    // native hover fill isn't observable through this bridge).
    private static readonly Color MenuHoverColor = new Color(135f / 255f, 25f / 255f, 178f / 255f, 0.25f);

    private static int _menuClickCount;
    private static long? _menuLastClickTick;
    private static bool _menuArmed;      // true once menu_add has been called; menu_remove clears it
    private static VisualElement _menuElement; // live reference to our attached row, or null
    private static VisualElement _menuPaddingContainer; // cached floating-element-mouse-over-padding ancestor, for the current popup instance only
    private static Label _menuLabel;     // live reference to our row's Label, for the deferred native-style copy
    private static bool _menuStyleCopied; // true once MaybeCopyNativeStyle has successfully read a native sibling this popup instance
    private static long _menuLastAttemptMs = -1;
    private const long MenuRetryIntervalMs = 1000;
    private static int _menuGrowAttempts;  // per-popup-instance growth attempts, reset on detach
    private const int MaxGrowAttemptsPerPopup = 4;

    // === Overlay injection: the "Chat with DoF" panel ===
    //
    // The chat overlay itself: header (title/New chat/close), scrollable
    // message area, working input row. Attached as the LAST child of the
    // PanelManager RootVisualElement rather than under floating-element-root: that layer is torn down
    // and rebuilt on essentially every dropdown/tooltip open/close (see the
    // menu-injection recon above), which would make the overlay flicker or
    // vanish out from under a user mid-conversation. Last-child-of-root still
    // paints on top of every screen because RootVisualElement's children are
    // drawn in tree order and nothing else re-parents itself after this panel
    // is appended.
    internal const string OverlayName = "fm-dof-overlay";
    private const float OverlayRight = 24f;
    private const float OverlayTop = 120f;
    private const float OverlayWidth = 420f;
    private const float OverlayHeight = 620f;
    private const float OverlayRadius = 8f;
    private const float OverlayHeaderHeight = 40f;
    private const float OverlayCloseButtonSize = 28f;
    private const float OverlayBubbleMaxWidth = 320f;
    private const float OverlayInputHeight = 44f;

    // rgba(32,35,47,0.97) — #20232F, matches the native dropdown popup bg
    // sampled for BuildMenuRow above, at near-opaque alpha since this floats
    // over arbitrary screens rather than another popup.
    // Fully opaque: 0.97 alpha rendered visibly translucent in-game (FM's
    // compositing amplifies it — the fixture sidebar bled through the body),
    // and a chat panel wants a solid ground anyway.
    private static readonly Color OverlayBackgroundColor = new Color(0x20 / 255f, 0x23 / 255f, 0x2F / 255f, 1f);
    // rgba(135,25,178,0.60) — same accent family as the menu hover, at a
    // stronger alpha since this is the panel's only border cue.
    private static readonly Color OverlayBorderColor = new Color(135f / 255f, 25f / 255f, 178f / 255f, 0.60f);
    // rgba(135,25,178,0.35) — header/input hairline, fainter than the panel border.
    private static readonly Color OverlayHairlineColor = new Color(135f / 255f, 25f / 255f, 178f / 255f, 0.35f);
    // rgba(135,25,178,0.35) — close-button hover fill, same alpha as the hairline.
    private static readonly Color OverlayCloseHoverColor = new Color(135f / 255f, 25f / 255f, 178f / 255f, 0.35f);
    // rgba(135,25,178,0.25) — DoF message bubble fill.
    private static readonly Color OverlayDofBubbleColor = new Color(135f / 255f, 25f / 255f, 178f / 255f, 0.25f);
    // rgba(60,65,85,0.6) — user message bubble fill (neutral, no accent).
    private static readonly Color OverlayUserBubbleColor = new Color(60f / 255f, 65f / 255f, 85f / 255f, 0.6f);
    // rgba(15,17,24,0.9) — input placeholder row background.
    private static readonly Color OverlayInputBgColor = new Color(15f / 255f, 17f / 255f, 24f / 255f, 0.9f);
    // rgba(160,150,190,0.55) — dim placeholder text.
    private static readonly Color OverlayPlaceholderColor = new Color(160f / 255f, 150f / 255f, 190f / 255f, 0.55f);
    // rgba(160,150,190,0.30) — scrollbar thumb at rest, same dim family as the placeholder text.
    private static readonly Color OverlayScrollThumbColor = new Color(160f / 255f, 150f / 255f, 190f / 255f, 0.30f);
    // rgba(160,150,190,0.55) — scrollbar thumb under the cursor.
    private static readonly Color OverlayScrollThumbHoverColor = new Color(160f / 255f, 150f / 255f, 190f / 255f, 0.55f);
    // rgba(255,255,255,0.04) — near-invisible scrollbar track behind the thumb.
    private static readonly Color OverlayScrollTrackColor = new Color(1f, 1f, 1f, 0.04f);

    private static VisualElement _overlayElement; // live reference to the attached overlay root, or null
    private static int _overlayCloseCount;

    // === Real chat wiring: message area, outbox, bubble cap ===
    //
    // The overlay shell above is still built once by BuildOverlay; these
    // fields/consts back the live chat surface layered onto it: the message
    // area is found by name (MessageAreaName) rather than only trusted as a
    // cached reference, because the overlay can be torn down and rebuilt
    // (overlay_remove then overlay_add/overlay_show) — same staleness concern
    // as _overlayElement itself, so every accessor revalidates .parent != null
    // before trusting a cached VisualElement.
    private const string MessageAreaName = "fm-dof-messages";
    private const string ScrollName = "fm-dof-scroll";
    private const string SystemLineText = "Your Director of Football is listening.";
    private const string InputFieldName = "fm-dof-input";
    private const float SendButtonSize = 36f;
    private const int MaxBubbles = 60;
    private const int MaxMessageChars = 4000;

    private static VisualElement _overlayMessageArea; // live reference to the message area container, or null
    private static ScrollView _overlayScroll; // live reference to the message ScrollView wrapper, or null
    private static Label _overlaySystemLine; // live reference to the dimmed "listening" line, or null once removed
    private static Label _overlayThinkingLine; // dimmed "checking with the scouts" line while the host computes a reply, or null
    private static Label _overlayStreamLabel; // label of the open streaming dof bubble (overlay_update), or null when no stream is open

    // Raised by the header's "New chat" button, consumed (and reset) by the
    // next overlay_poll so the host service knows to drop its headless
    // session. Guarded by _outboxLock like the outbox itself — the button
    // click also discards any not-yet-polled outbox text, since those
    // messages belong to the conversation being abandoned.
    private static bool _newChatPending;

    // User-typed messages awaiting delivery to the host chat service via
    // overlay_poll. Written by the submit handler (main-thread UI event);
    // drained by overlay_poll (also main-thread, via Enqueue below — but
    // locked regardless per house rule: never assume a threading model that
    // isn't enforced by the type itself).
    private static readonly object _outboxLock = new object();
    private static readonly List<(string Text, long Ts)> _outbox = new List<(string, long)>();

    /// <summary>Dispatch entry point for the ui_inject verb (flat "action"
    /// field: menu_add|menu_remove|menu_status|overlay_add|overlay_remove|
    /// overlay_show|overlay_hide|overlay_status|overlay_post|overlay_update|
    /// overlay_poll|overlay_clear|overlay_thinking). Must run on the main
    /// thread — VoiceServer routes it through Enqueue below. "msg" is the
    /// full request JsonObject as parsed there: overlay_post reads
    /// "from"/"text", overlay_update reads "text"/"done" and
    /// overlay_thinking reads "on"/"label"/"text" out of it, and a future
    /// action can read its own fields without a signature change.</summary>
    public static JsonObject Handle(string action, JsonNode msg)
    {
        switch (action)
        {
            case "menu_add": return MenuAdd();
            case "menu_remove": return MenuRemove();
            case "menu_status": return MenuStatus();
            case "overlay_add": return OverlayAdd();
            case "overlay_remove": return OverlayRemove();
            case "overlay_show": return OverlaySetVisible(true);
            case "overlay_hide": return OverlaySetVisible(false);
            case "overlay_status": return OverlayStatus();
            case "overlay_post": return OverlayPost(msg);
            case "overlay_update": return OverlayUpdate(msg);
            case "overlay_poll": return OverlayPoll();
            case "overlay_clear": return OverlayClear();
            case "overlay_thinking": return OverlayThinking(msg);
            default: return new JsonObject { ["ok"] = false, ["error"] = "unknown-action:" + (action ?? "") };
        }
    }

    /// <summary>Off-thread entry point used by VoiceServer: parses the raw
    /// request JSON, hops onto the main thread (UI Toolkit is main-thread
    /// only), and resolves with Handle's result.</summary>
    public static Task<JsonObject> Enqueue(FMBridge.Voice.MainThreadQueue queue, string requestJson)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try
            {
                JsonNode root = null;
                try { root = JsonNode.Parse(requestJson ?? ""); } catch { }
                tcs.SetResult(Handle((string)root?["action"], root));
            }
            catch (Exception e)
            {
                try { tcs.SetResult(new JsonObject { ["ok"] = false, ["error"] = e.Message }); } catch { }
            }
        });
        return tcs.Task;
    }

    private static JsonObject MenuAdd()
    {
        try
        {
            _menuArmed = true;
            var injected = TryInjectMenuItem();
            var result = new JsonObject { ["ok"] = true, ["armed"] = true, ["injected"] = injected };
            if (!injected)
                result["note"] = "Recruitment popup item list not found in the live tree yet (open the Recruitment " +
                    "nav dropdown once). Tick() (wired from BridgePlugin.OnTick) retries automatically at most once " +
                    "per second while armed — no further menu_add call is needed; poll menu_status to see when it attaches.";
            if (injected && _menuElement != null) result["geometry"] = Geometry(_menuElement);
            return result;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private static JsonObject MenuRemove()
    {
        try
        {
            _menuArmed = false;
            var pm = Navigator.Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            var existing = root != null ? FindByName(root, MenuItemName, 0) : null;
            bool existed = existing != null;
            if (existed) existing.RemoveFromHierarchy();
            _menuElement = null;
            _menuPaddingContainer = null;
            _menuLabel = null;
            _menuStyleCopied = false;
            _menuGrowAttempts = 0;
            Navigator._log?.LogInfo($"[Bridge] ui_inject menu_remove: {MenuItemName} disarmed (existed={existed})");
            return new JsonObject { ["ok"] = true, ["existed"] = existed };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private static JsonObject MenuStatus()
    {
        try
        {
            var pm = Navigator.Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            var existing = root != null ? FindByName(root, MenuItemName, 0) : null;
            var result = new JsonObject
            {
                ["ok"] = true,
                ["armed"] = _menuArmed,
                ["present"] = existing != null,
                ["click_count"] = _menuClickCount,
                ["last_click_tick"] = _menuLastClickTick.HasValue ? JsonValue.Create(_menuLastClickTick.Value) : null,
                ["row_index"] = existing != null ? GetRowIndex(existing) : -1,
            };
            if (existing != null) result["geometry"] = Geometry(existing);
            result["grow_attempts"] = _menuGrowAttempts;
            result["style_copied"] = _menuStyleCopied;
            if (_menuLabel != null)
            {
                try { result["our_font_size"] = (int)_menuLabel.resolvedStyle.fontSize; } catch { }
            }
            if (existing != null && existing.parent != null)
            {
                try
                {
                    var nat = FindFirstNativeTextElement(existing.parent);
                    if (nat != null)
                    {
                        result["native_font_size"] = (int)nat.resolvedStyle.fontSize;
                        // Row-chrome match diagnostics: our row box vs the
                        // native row box, and each text's world offset inside
                        // its row — the numbers MaybeCopyNativeStyle aligns.
                        try
                        {
                            var natRow = FindRowOf(nat, existing.parent);
                            var match = new JsonObject();
                            try { match["our_row_h"] = (int)existing.resolvedStyle.height; } catch { }
                            if (natRow != null)
                            {
                                try { match["native_row_h"] = (int)natRow.resolvedStyle.height; } catch { }
                                try { match["native_indent_x"] = (int)(nat.worldBound.x - natRow.worldBound.x); } catch { }
                                try { match["native_indent_y"] = (int)(nat.worldBound.y - natRow.worldBound.y); } catch { }
                            }
                            if (_menuLabel != null)
                            {
                                try { match["our_indent_x"] = (int)(_menuLabel.worldBound.x - existing.worldBound.x); } catch { }
                                try { match["our_indent_y"] = (int)(_menuLabel.worldBound.y - existing.worldBound.y); } catch { }
                            }
                            result["row_match"] = match;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            if (_menuPaddingContainer != null)
            {
                try { result["padding_height"] = (int)_menuPaddingContainer.resolvedStyle.height; } catch { }
            }
            if (existing != null)
            {
                // Ancestor-chain dump (name, resolved height, world yMax) up to
                // the panel root — ground-truth diagnostics for the clipping
                // chrome, since no generic tree-dump verb exists.
                var chain = new JsonArray();
                var cur = existing.parent;
                int guard = 0;
                while (cur != null && guard++ < 20)
                {
                    var entry = new JsonObject();
                    try { entry["name"] = cur.name ?? ""; } catch { }
                    try { entry["h"] = (int)cur.resolvedStyle.height; } catch { }
                    try { entry["yMax"] = (int)cur.worldBound.yMax; } catch { }
                    chain.Add(entry);
                    VisualElement p;
                    try { p = cur.parent; } catch { p = null; }
                    cur = p;
                }
                result["ancestors"] = chain;
            }
            return result;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Shared create-or-show logic: if the overlay is already
    /// attached, just makes sure it's visible (display Flex) rather than
    /// rebuilding it; otherwise builds and attaches a fresh one. Backs
    /// OverlayAdd directly and is reused by OverlayPost/EnsureMessageArea so
    /// posting a message (from either side of the bridge) never requires a
    /// separate overlay_add call first. Returns null only when the
    /// PanelManager root itself isn't available yet; never throws — callers
    /// wrap in their own try/catch.</summary>
    private static VisualElement EnsureOverlayVisible(out bool created)
    {
        created = false;
        var pm = Navigator.Pm();
        var root = pm != null ? pm.RootVisualElement : null;
        if (root == null) return null;

        var existing = _overlayElement;
        try { if (existing != null && existing.parent == null) existing = null; } catch { existing = null; }

        if (existing != null)
        {
            existing.style.display = DisplayStyle.Flex;
            return existing;
        }

        var overlay = BuildOverlay();
        root.Add(overlay); // Add(), not Insert — last child renders on top
        _overlayElement = overlay;
        created = true;
        return overlay;
    }

    /// <summary>Idempotent create-or-show: if the overlay is already attached,
    /// just makes sure it's visible (display Flex) rather than rebuilding it —
    /// a second overlay_add must never duplicate the panel or reset its state
    /// (there isn't any yet, but the shell is designed to hold some later).</summary>
    private static JsonObject OverlayAdd()
    {
        try
        {
            var overlay = EnsureOverlayVisible(out var created);
            if (overlay == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            if (created)
                Navigator._log?.LogInfo($"[Bridge] ui_inject overlay_add: {OverlayName} attached to PanelManager root");
            return new JsonObject { ["ok"] = true, ["created"] = created, ["visible"] = true };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private static JsonObject OverlayRemove()
    {
        try
        {
            var pm = Navigator.Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            var existing = root != null ? FindByName(root, OverlayName, 0) : _overlayElement;
            bool existed = existing != null;
            if (existed) existing.RemoveFromHierarchy();
            _overlayElement = null;
            _overlayMessageArea = null;
            _overlaySystemLine = null;
            Navigator._log?.LogInfo($"[Bridge] ui_inject overlay_remove: {OverlayName} detached (existed={existed})");
            return new JsonObject { ["ok"] = true, ["existed"] = existed };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Resolves the live message area, ensuring the overlay itself
    /// exists/visible first (see EnsureOverlayVisible). Re-locates the area by
    /// name if the cached reference has gone stale (e.g. the overlay was
    /// rebuilt by a remove+add cycle) rather than trusting the cache blindly —
    /// same staleness pattern as _overlayElement elsewhere in this file.</summary>
    private static VisualElement EnsureMessageArea()
    {
        var overlay = EnsureOverlayVisible(out _);
        if (overlay == null) return null;

        var area = _overlayMessageArea;
        try { if (area != null && area.parent == null) area = null; } catch { area = null; }
        if (area == null)
        {
            area = FindByName(overlay, MessageAreaName, 0);
            _overlayMessageArea = area;
        }
        return area;
    }

    /// <summary>Appends one chat bubble to the message area (creating/showing
    /// the overlay first if needed — see EnsureMessageArea), removing the
    /// dimmed "listening" system line on the first real bubble, then trims
    /// the oldest bubbles beyond MaxBubbles to bound memory. Used both by
    /// overlay_post (either side of the bridge) and locally by the input
    /// row's submit handler (from="user", for immediate echo without a
    /// round-trip). Never throws — swallows failures same as the rest of the
    /// overlay's click/hover handlers.</summary>
    private static void AppendBubble(string from, string text)
    {
        try
        {
            var area = EnsureMessageArea();
            if (area == null) return;

            if (_overlaySystemLine != null)
            {
                try { _overlaySystemLine.RemoveFromHierarchy(); } catch { }
                _overlaySystemLine = null;
            }

            var bubble = BuildOverlayBubble(text, isDof: from == "dof");
            area.Add(bubble);

            // Keep the transient "checking with the scouts" line coherent: a
            // dof reply supersedes it (the wait is over); a user bubble landing
            // while it shows (queued second question) just pushes it back to
            // the bottom so it keeps reading as "answer still coming".
            var thinking = _overlayThinkingLine;
            try { if (thinking != null && thinking.parent == null) { thinking = null; _overlayThinkingLine = null; } } catch { thinking = null; }
            if (thinking != null)
            {
                if (from == "dof")
                {
                    try { thinking.RemoveFromHierarchy(); } catch { }
                    _overlayThinkingLine = null;
                }
                else
                {
                    try { area.Add(thinking); } catch { }
                }
            }

            // Snap to bottom once THIS bubble has been laid out — layout
            // runs async, so scrolling immediately would use stale heights.
            try
            {
                bubble.RegisterCallback<GeometryChangedEvent>(
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<GeometryChangedEvent>>(
                        (Action<GeometryChangedEvent>)(_ => ScrollToBottom())));
            }
            catch { }

            while (area.childCount > MaxBubbles)
            {
                VisualElement oldest;
                try { oldest = area[0]; } catch { break; }
                try { oldest.RemoveFromHierarchy(); } catch { break; }
            }
        }
        catch { }
    }

    /// <summary>Backs both overlay_show (visible=true) and overlay_hide
    /// (visible=false). overlay_show creates the overlay first if it isn't
    /// present yet (mirrors OverlayAdd's create-or-show semantics); overlay_hide
    /// on a missing overlay is a no-op that reports visible=false.</summary>
    private static JsonObject OverlaySetVisible(bool visible)
    {
        try
        {
            var pm = Navigator.Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-root-visualelement" };

            var existing = _overlayElement;
            try { if (existing != null && existing.parent == null) existing = null; } catch { existing = null; }

            if (existing == null)
            {
                if (!visible) return new JsonObject { ["ok"] = true, ["visible"] = false };
                var overlay = BuildOverlay();
                root.Add(overlay);
                _overlayElement = overlay;
                Navigator._log?.LogInfo($"[Bridge] ui_inject overlay_show: {OverlayName} attached to PanelManager root");
                return new JsonObject { ["ok"] = true, ["visible"] = true };
            }

            existing.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            Navigator._log?.LogInfo($"[Bridge] ui_inject {(visible ? "overlay_show" : "overlay_hide")}: {OverlayName} display set to {(visible ? "Flex" : "None")}");
            return new JsonObject { ["ok"] = true, ["visible"] = visible };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private static JsonObject OverlayStatus()
    {
        try
        {
            var pm = Navigator.Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            var existing = root != null ? FindByName(root, OverlayName, 0) : null;
            bool visible = false;
            if (existing != null)
            {
                try { visible = existing.style.display.value != DisplayStyle.None; } catch { visible = true; }
            }
            var result = new JsonObject
            {
                ["ok"] = true,
                ["present"] = existing != null,
                ["visible"] = visible,
                ["close_click_count"] = _overlayCloseCount,
            };
            if (existing != null) result["geometry"] = Geometry(existing);
            return result;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Appends a chat bubble from either side of the bridge:
    /// msg["from"] must be "dof" or "user" (anything else is an error, no
    /// bubble added), msg["text"] must be a non-empty/non-whitespace string
    /// (capped at MaxMessageChars). Creates/shows the overlay first if
    /// needed (see EnsureMessageArea/EnsureOverlayVisible) so a dof-side
    /// message can open the panel on its own. Returns the total bubble count
    /// after the append (post-trim, so it never exceeds MaxBubbles).</summary>
    private static JsonObject OverlayPost(JsonNode msg)
    {
        try
        {
            string from;
            try { from = (string)msg?["from"]; } catch { from = null; }
            if (from != "dof" && from != "user")
                return new JsonObject { ["ok"] = false, ["error"] = "invalid-from:" + (from ?? "") };

            string text;
            try { text = (string)msg?["text"]; } catch { text = null; }
            if (string.IsNullOrWhiteSpace(text))
                return new JsonObject { ["ok"] = false, ["error"] = "empty-text" };
            if (text.Length > MaxMessageChars) text = text.Substring(0, MaxMessageChars);

            AppendBubble(from, text);

            var area = _overlayMessageArea;
            int count = 0;
            try { if (area != null) count = area.childCount; } catch { }

            Navigator._log?.LogInfo($"[Bridge] ui_inject overlay_post: from={from} len={text.Length} count={count}");
            return new JsonObject { ["ok"] = true, ["count"] = count };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Streams a growing dof reply into a single bubble. The first
    /// update opens a "streaming" bubble via the same path as an
    /// overlay_post from="dof" (system line removed, thinking line hidden,
    /// auto-scroll registered, bubble cap enforced) and caches its Label;
    /// each later update replaces that Label's text in place — msg["text"]
    /// is the full text so far (capped at MaxMessageChars), not a delta.
    /// msg["done"] = true closes the stream so the next update opens a
    /// fresh bubble; the cached reference is also dropped by OverlayClear
    /// (New chat) and whenever it goes stale (overlay rebuilt). done with
    /// no open stream and no text is an accepted no-op, not an error —
    /// the host may close defensively after a failure.</summary>
    private static JsonObject OverlayUpdate(JsonNode msg)
    {
        try
        {
            string text;
            try { text = (string)msg?["text"]; } catch { text = null; }
            bool done = false;
            try { done = (bool?)msg?["done"] ?? false; } catch { }

            if (string.IsNullOrWhiteSpace(text) && !done)
                return new JsonObject { ["ok"] = false, ["error"] = "empty-text" };
            if (text != null && text.Length > MaxMessageChars) text = text.Substring(0, MaxMessageChars);

            var label = _overlayStreamLabel;
            try { if (label != null && label.parent == null) { label = null; _overlayStreamLabel = null; } } catch { label = null; _overlayStreamLabel = null; }

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (label == null)
                {
                    AppendBubble("dof", text);
                    // AppendBubble leaves the new dof bubble as the last
                    // child of the message area (a dof bubble removes the
                    // thinking line instead of re-appending it), so the
                    // Label to mutate on later updates is that bubble's
                    // single child.
                    try
                    {
                        var msgArea = _overlayMessageArea;
                        if (msgArea != null && msgArea.childCount > 0)
                        {
                            var bubble = msgArea[msgArea.childCount - 1];
                            if (bubble != null && bubble.childCount > 0)
                                label = bubble[0].TryCast<Label>();
                        }
                    }
                    catch { label = null; }
                }
                else
                {
                    try { label.text = text; } catch { }
                    // The bubble's GeometryChangedEvent only fires when the
                    // new text changes its height; snap explicitly so every
                    // update keeps the tail in view.
                    ScrollToBottom();
                }
            }

            _overlayStreamLabel = done ? null : label;

            var area = _overlayMessageArea;
            int count = 0;
            try { if (area != null) count = area.childCount; } catch { }

            Navigator._log?.LogInfo($"[Bridge] ui_inject overlay_update: len={text?.Length ?? 0} done={done} streaming={_overlayStreamLabel != null} count={count}");
            return new JsonObject { ["ok"] = true, ["count"] = count, ["streaming"] = _overlayStreamLabel != null };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Drains the outbox of user-typed messages queued by the input
    /// row's submit handler (Enter or the send button) since the last poll.
    /// Empty array when nothing is pending. ts is milliseconds off the same
    /// _clock stopwatch used elsewhere in this file (click ticks etc), not a
    /// wall-clock timestamp. Also reports (and resets) the header button's
    /// new-chat request as "new_chat": true so the host service can drop its
    /// headless session — the flag rides the same reply as the drained
    /// messages, keeping ordering trivially consistent under _outboxLock.</summary>
    private static JsonObject OverlayPoll()
    {
        try
        {
            var arr = new JsonArray();
            bool newChat;
            lock (_outboxLock)
            {
                foreach (var item in _outbox)
                    arr.Add(new JsonObject { ["text"] = item.Text, ["ts"] = item.Ts });
                _outbox.Clear();
                newChat = _newChatPending;
                _newChatPending = false;
            }
            var res = new JsonObject { ["ok"] = true, ["messages"] = arr };
            if (newChat) res["new_chat"] = true;
            return res;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Removes every bubble from the message area and restores the
    /// dimmed "listening" placeholder line, so a cleared panel (New chat)
    /// looks like a freshly opened one rather than a blank void. Missing
    /// overlay/message area is a no-op that reports cleared=0, not an error.</summary>
    private static JsonObject OverlayClear()
    {
        try
        {
            var area = _overlayMessageArea;
            try { if (area != null && area.parent == null) area = null; } catch { area = null; }
            if (area == null)
            {
                var overlay = _overlayElement;
                try { if (overlay != null && overlay.parent == null) overlay = null; } catch { overlay = null; }
                if (overlay != null) area = FindByName(overlay, MessageAreaName, 0);
            }
            if (area == null) return new JsonObject { ["ok"] = true, ["cleared"] = 0 };

            int cleared = area.childCount;
            for (int i = area.childCount - 1; i >= 0; i--)
            {
                try { area[i].RemoveFromHierarchy(); } catch { }
            }
            _overlaySystemLine = null;
            _overlayThinkingLine = null;
            _overlayStreamLabel = null; // the bubble it pointed into is gone; next overlay_update starts fresh
            try
            {
                var sys = BuildSystemLine();
                area.Add(sys);
                _overlaySystemLine = sys;
            }
            catch { }

            Navigator._log?.LogInfo($"[Bridge] ui_inject overlay_clear: removed {cleared} bubble(s)");
            return new JsonObject { ["ok"] = true, ["cleared"] = cleared };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Shows/hides the transient "checking with the scouts" line at
    /// the bottom of the message area while the host service computes a
    /// reply: msg["on"] = true/false, optional msg["label"] (or legacy
    /// msg["text"]) overrides the default wording, capped at 120 chars.
    /// Idempotent both ways (on while shown just updates the
    /// text; off while hidden is a no-op) — the host also relies on
    /// AppendBubble removing the line automatically when the dof reply
    /// lands, so an explicit off is only a failure-path cleanup.</summary>
    private static JsonObject OverlayThinking(JsonNode msg)
    {
        try
        {
            bool on = false;
            try { on = (bool?)msg?["on"] ?? false; } catch { }

            var existing = _overlayThinkingLine;
            try { if (existing != null && existing.parent == null) { existing = null; _overlayThinkingLine = null; } } catch { existing = null; _overlayThinkingLine = null; }

            if (!on)
            {
                if (existing != null)
                {
                    try { existing.RemoveFromHierarchy(); } catch { }
                    _overlayThinkingLine = null;
                }
                return new JsonObject { ["ok"] = true, ["thinking"] = false };
            }

            var area = EnsureMessageArea();
            if (area == null) return new JsonObject { ["ok"] = false, ["reason"] = "no-message-area" };

            string text = null;
            try { text = (string)msg?["label"]; } catch { }
            if (string.IsNullOrWhiteSpace(text))
            {
                try { text = (string)msg?["text"]; } catch { }
            }
            if (string.IsNullOrWhiteSpace(text)) text = "Checking with the scouts…";
            if (text.Length > 120) text = text.Substring(0, 120);

            if (existing != null)
            {
                try { existing.text = text; } catch { }
                try { area.Add(existing); } catch { } // move back to bottom if bubbles landed after it
            }
            else
            {
                var line = new Label(text);
                line.pickingMode = PickingMode.Ignore;
                line.style.whiteSpace = WhiteSpace.Normal;
                line.style.color = OverlayPlaceholderColor;
                line.style.unityFontStyleAndWeight = FontStyle.Italic;
                line.style.fontSize = 12;
                line.style.marginTop = 6;
                line.style.marginBottom = 4;
                line.style.alignSelf = Align.FlexStart;
                area.Add(line);
                _overlayThinkingLine = line;
                try
                {
                    line.RegisterCallback<GeometryChangedEvent>(
                        Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<GeometryChangedEvent>>(
                            (Action<GeometryChangedEvent>)(_ => ScrollToBottom())));
                }
                catch { }
            }
            return new JsonObject { ["ok"] = true, ["thinking"] = true };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Creates the overlay if missing, otherwise flips its display
    /// between Flex/None. Called from the "Chat with DoF" menu row's click
    /// handler. Never throws out to the caller — the call site wraps this in
    /// its own try/catch so a failure here can never break click
    /// counting/logging.</summary>
    private static void ToggleOverlay()
    {
        var pm = Navigator.Pm();
        var root = pm != null ? pm.RootVisualElement : null;
        if (root == null) return;

        var existing = _overlayElement;
        try { if (existing != null && existing.parent == null) existing = null; } catch { existing = null; }

        if (existing == null)
        {
            var overlay = BuildOverlay();
            root.Add(overlay);
            _overlayElement = overlay;
            Navigator._log?.LogInfo($"[Bridge] ToggleOverlay: {OverlayName} created (shown)");
            return;
        }

        bool currentlyVisible;
        try { currentlyVisible = existing.style.display.value != DisplayStyle.None; } catch { currentlyVisible = true; }
        existing.style.display = currentlyVisible ? DisplayStyle.None : DisplayStyle.Flex;
        Navigator._log?.LogInfo($"[Bridge] ToggleOverlay: {OverlayName} display set to {(currentlyVisible ? "None" : "Flex")}");
    }

    /// <summary>Called once per main-thread tick from BridgePlugin.OnTick.
    ///
    /// Live verification (post-deploy) overturned the original
    /// recon: the floating popup IS destroyed on close and rebuilt fresh on
    /// every open (a post-close tree probe for "floating" found 0 rows).
    /// This watcher is therefore the PRIMARY attach mechanism, not a
    /// defensive fallback — every open gets a brand-new "View" with no
    /// children yet, and this Tick() races the native code to populate it.
    ///
    /// Idle-cost shape, by state:
    ///   - never armed: one static bool read, return. Effectively free.
    ///   - armed AND attached (dropdown currently open with our row in it):
    ///     one parent-null check, then MaybeGrowForOverflow (a worldBound
    ///     comparison against a cached ancestor reference — no tree walk) and
    ///     MaybeCopyNativeStyle (short-circuits immediately once
    ///     _menuStyleCopied is true). All cheap field reads, no DFS.
    ///   - armed AND not attached (the common case — dropdown closed, or not
    ///     yet opened this instance): the real tree walk in
    ///     TryInjectMenuItem/FindRecruitmentMenuList runs, throttled to at
    ///     most once per MenuRetryIntervalMs (1s), not every tick.</summary>
    public static void Tick()
    {
        if (!_menuArmed) return;

        if (_menuElement != null)
        {
            bool attached;
            try { attached = _menuElement.parent != null; } catch { attached = false; }
            if (attached)
            {
                MaybeGrowForOverflow();
                MaybeCopyNativeStyle();
                return;
            }
            _menuElement = null;
            _menuPaddingContainer = null;
            _menuLabel = null;
            _menuStyleCopied = false;
            _menuGrowAttempts = 0;
        }

        var now = _clock.ElapsedMilliseconds;
        if (_menuLastAttemptMs >= 0 && now - _menuLastAttemptMs < MenuRetryIntervalMs) return;
        _menuLastAttemptMs = now;
        TryInjectMenuItem();
    }

    private static bool TryInjectMenuItem()
    {
        try
        {
            var pm = Navigator.Pm();
            var root = pm != null ? pm.RootVisualElement : null;
            if (root == null) return false;

            var existing = FindByName(root, MenuItemName, 0);
            if (existing != null)
            {
                _menuElement = existing;
                return true;
            }

            var floatingRoot = FindByName(root, FloatingRootName, 0);
            if (floatingRoot == null) return false; // dropdown is closed right now — no floating popup exists

            var list = FindRecruitmentMenuList(floatingRoot);
            if (list == null) return false; // Recruitment dropdown isn't the (or isn't yet a) populated floating popup

            // Insert at index 0 (never Add/append): live verification found
            // append racing the native populate — landing after all 19 rows
            // on one open (clipped off the bottom, invisible) and, by luck,
            // at index 0 on another open (native rows appended after ours,
            // because the list was still empty when this Tick() fired).
            // Inserting at a fixed index 0 makes the outcome deterministic
            // regardless of that race: our row is always first, whether the
            // list is currently empty or already fully populated.
            var row = BuildMenuRow();
            list.Insert(0, row);
            _menuElement = row;
            _menuPaddingContainer = null; // set by MaybeGrowForOverflow to the topmost grown chrome node
            _menuStyleCopied = false;
            _menuGrowAttempts = 0;
            Navigator._log?.LogInfo($"[Bridge] ui_inject menu_add: {MenuItemName} inserted at index 0 of Recruitment dropdown list");

            // Best-effort immediately — covers the common case where the list
            // is already fully populated at insert time. MaybeGrowForOverflow
            // and MaybeCopyNativeStyle also re-run every subsequent tick
            // while attached (see Tick()) to catch the "list was still empty,
            // native populates afterward" race.
            MaybeGrowForOverflow();
            MaybeCopyNativeStyle();
            return true;
        }
        catch (Exception e)
        {
            Navigator._log?.LogWarning($"[Bridge] ui_inject menu inject attempt failed: {e.Message}");
            return false;
        }
    }

    private static VisualElement FindAncestorByName(VisualElement node, string name, int maxDepth)
    {
        var cur = node;
        int guard = 0;
        while (cur != null && guard++ < maxDepth)
        {
            try { if (cur.name == name) return cur; } catch { }
            try { cur = cur.parent; } catch { cur = null; }
        }
        return null;
    }

    /// <summary>Measures the live row pitch from the first two NATIVE
    /// siblings in the list (skipping our own row by name), falling back to
    /// DefaultMenuPitch (30px, matching the recon-measured native pitch) when
    /// fewer than two native rows currently exist — e.g. right after a fresh
    /// popup open, before the native code has populated any rows yet.</summary>
    private static float MeasurePitch(VisualElement list)
    {
        VisualElement first = null, second = null;
        for (int i = 0; i < list.childCount; i++)
        {
            VisualElement c;
            try { c = list[i]; } catch { continue; }
            try { if (c.name == MenuItemName) continue; } catch { }
            if (first == null) first = c;
            else { second = c; break; }
        }
        if (first != null && second != null)
        {
            try
            {
                var d = System.Math.Abs(second.layout.y - first.layout.y);
                if (d > 1f && !float.IsNaN(d)) return d;
            }
            catch { }
        }
        return DefaultMenuPitch;
    }

    /// <summary>Clip-driven, self-correcting height fix (requirement B/C from
    /// live verification: the popup's chrome doesn't grow for a 20th row, so
    /// the last native row — or the whole list, via an intervening
    /// ScrollView — gets clipped/scrollbarred once our row is inserted).
    /// Rather than tracking a recorded "expected" height (which would go
    /// stale, or fight a legitimate native resize), this reads CURRENT
    /// geometry every call: if the last row in the list currently extends
    /// below the padding container's own bottom edge, it isn't visible — grow
    /// the ancestor chain by exactly one pitch, right now, based on current
    /// values. The next check then sees no more overflow and does nothing
    /// further (idempotent by construction — no runaway growth), and if
    /// native later changes row count and overflow reappears, this grows
    /// again on the next tick that observes it.</summary>
    private static void MaybeGrowForOverflow()
    {
        try
        {
            if (_menuElement == null) return;
            var list = _menuElement.parent;
            if (list == null || list.childCount == 0) return;

            VisualElement lastChild;
            try { lastChild = list[list.childCount - 1]; } catch { return; }
            if (lastChild == _menuElement) return; // only our row exists so far — nothing to overflow yet

            float lastBottom = lastChild.worldBound.yMax;
            if (float.IsNaN(lastBottom)) return;

            // Live verification disproved the recon assumption baked into the
            // first version of this method: floating-element-mouse-over-padding
            // is NOT an ancestor of the item list (FindAncestorByName never
            // found it, so growth silently never ran). Instead of naming the
            // clipping node, detect it: walk up from the list toward
            // floating-element-root and flag overflow if ANY ancestor's painted
            // bottom edge sits above the last row's bottom. Grow the whole
            // fixed-height chain below the root when it does.
            bool overflow = false;
            VisualElement top = null;
            var node = list;
            int guard = 0;
            while (node != null && guard++ < 20)
            {
                bool isRoot = false;
                try { isRoot = node.name == FloatingRootName; } catch { }
                if (isRoot) break;
                top = node;
                try
                {
                    var bottom = node.worldBound.yMax;
                    if (!float.IsNaN(bottom) && lastBottom > bottom + 1f) overflow = true;
                }
                catch { }
                try { node = node.parent; } catch { node = null; }
            }
            if (!overflow || top == null) return;

            // Bounded so a node whose height native code re-clamps every frame
            // can't drive unbounded growth tick after tick.
            if (_menuGrowAttempts >= MaxGrowAttemptsPerPopup) return;
            _menuGrowAttempts++;

            var pitch = MeasurePitch(list);
            GrowAncestorChainBy(list, top, pitch);
            _menuPaddingContainer = top; // repurposed: topmost grown chrome node, for menu_status diagnostics
        }
        catch { }
    }

    /// <summary>Grows every fixed-height node from fromInclusive up through
    /// (and including) toInclusive by pitch. A ScrollView along that chain
    /// gets both height and maxHeight bumped — covers the "View sits inside a
    /// ScrollView" case live verification flagged (a scrollbar appeared once
    /// our row pushed the list to 20 entries). Nodes without a positive
    /// resolved height (flex/auto-sized wrappers) are left untouched.</summary>
    private static void GrowAncestorChainBy(VisualElement fromInclusive, VisualElement toInclusive, float pitch)
    {
        var node = fromInclusive;
        int guard = 0;
        while (node != null && guard++ < 20)
        {
            try
            {
                var scroll = node.TryCast<ScrollView>();
                if (scroll != null)
                {
                    GrowHeightIfFixed(scroll, false, pitch);
                    GrowHeightIfFixed(scroll, true, pitch);
                }
                else
                {
                    GrowHeightIfFixed(node, false, pitch);
                }
            }
            catch { }

            if (node == toInclusive) break;
            VisualElement parent;
            try { parent = node.parent; } catch { parent = null; }
            node = parent;
        }
    }

    private static void GrowHeightIfFixed(VisualElement node, bool isMaxHeight, float pitch)
    {
        try
        {
            float current;
            // resolvedStyle.maxHeight is StyleFloat in this interop assembly
            // (can carry StyleKeyword.None/Auto instead of a value), unlike
            // resolvedStyle.height which is a plain float — pull .value out
            // explicitly rather than relying on an implicit conversion.
            if (isMaxHeight) current = node.resolvedStyle.maxHeight.value; else current = node.resolvedStyle.height;
            if (current <= 0 || float.IsNaN(current)) return;
            var next = current + pitch;
            if (isMaxHeight) node.style.maxHeight = next; else node.style.height = next;
        }
        catch { }
    }

    private static int GetRowIndex(VisualElement row)
    {
        try
        {
            var parent = row?.parent;
            if (parent == null) return -1;
            for (int i = 0; i < parent.childCount; i++)
            {
                VisualElement c;
                try { c = parent[i]; } catch { continue; }
                try { if (c.name == MenuItemName) return i; } catch { }
            }
        }
        catch { }
        return -1;
    }

    /// <summary>Depth-capped search for the first native (non-ours) row's
    /// rendered TextElement, used by MaybeCopyNativeStyle to read a real
    /// sibling's resolved font size/color instead of guessing.</summary>
    private static TextElement FindFirstNativeTextElement(VisualElement list)
    {
        for (int i = 0; i < list.childCount; i++)
        {
            VisualElement c;
            try { c = list[i]; } catch { continue; }
            try { if (c.name == MenuItemName) continue; } catch { }
            var te = FindTextElementShallow(c, 0);
            if (te != null) return te;
        }
        return null;
    }

    private static TextElement FindTextElementShallow(VisualElement node, int depth)
    {
        if (node == null || depth > 6) return null;
        try
        {
            var te = node.TryCast<TextElement>();
            if (te != null && !string.IsNullOrWhiteSpace(te.text)) return te;
        }
        catch { }
        for (int i = 0; i < node.childCount; i++)
        {
            VisualElement child;
            try { child = node[i]; } catch { continue; }
            TextElement hit;
            try { hit = FindTextElementShallow(child, depth + 1); } catch { hit = null; }
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>One-shot: as soon as a native sibling row exists (it may not,
    /// right after a fresh popup open — see Tick()'s doc comment on the
    /// populate race), reads its resolvedStyle font size/color and applies
    /// them to our Label, then sets _menuStyleCopied so this never runs again
    /// for this popup instance. Prefers the live value over the hardcoded
    /// pixel-sampled fallback baked into BuildMenuRow.</summary>
    private static void MaybeCopyNativeStyle()
    {
        if (_menuStyleCopied || _menuLabel == null || _menuElement == null) return;
        try
        {
            var list = _menuElement.parent;
            if (list == null) return;
            var nativeText = FindFirstNativeTextElement(list);
            if (nativeText == null) return;

            var rs = nativeText.resolvedStyle;
            if (rs.fontSize > 0) _menuLabel.style.fontSize = rs.fontSize;
            if (rs.color.a > 0) _menuLabel.style.color = rs.color;
            // Font FAMILY is the dominant visual difference (live glyph
            // measurement: our fallback rendered ~43% taller than native rows
            // in the same dropdown) — FM ships its own typeface, so fontSize
            // and color alone still read as foreign. Copy the face itself,
            // plus weight and tracking. Each copy is individually guarded:
            // if one resolvedStyle member throws through the interop, the
            // rest still apply.
            try
            {
                _menuLabel.style.unityFontDefinition = new StyleFontDefinition(rs.unityFontDefinition);
            }
            catch { }
            try
            {
                var font = rs.unityFont;
                if (font != null) _menuLabel.style.unityFont = new StyleFont(font);
            }
            catch { }
            try { _menuLabel.style.unityFontStyleAndWeight = rs.unityFontStyleAndWeight; } catch { }
            try { _menuLabel.style.letterSpacing = rs.letterSpacing; } catch { }

            // Row CHROME (addendum #2, user feedback: "slightly
            // more centered and a bigger button than the rest"). Typography
            // alone left our hand-picked box (height 32, paddingLeft 24,
            // vertical centering) visibly off next to native rows. FM26
            // strips USS class lists at runtime, so "use the game's own
            // style" means copying the native row's RESOLVED box instead:
            //   - the text element's own box padding/margins (glyphs sit
            //     inside the element box; a default-theme Label carries its
            //     own few px, so mirror the native text's exactly),
            //   - the row container's height + vertical margins,
            //   - the text's x/y offset measured in WORLD space from row to
            //     text (native rows nest wrappers with their own padding;
            //     summing resolvedStyle paddings of one level under-reports
            //     the real indent — world geometry is nesting-proof).
            // With identical font+size+box, offset-matching is pixel-exact.
            try { _menuLabel.style.marginLeft = rs.marginLeft; } catch { }
            try { _menuLabel.style.marginRight = rs.marginRight; } catch { }
            try { _menuLabel.style.marginTop = rs.marginTop; } catch { }
            try { _menuLabel.style.marginBottom = rs.marginBottom; } catch { }
            try { _menuLabel.style.paddingLeft = rs.paddingLeft; } catch { }
            try { _menuLabel.style.paddingRight = rs.paddingRight; } catch { }
            try { _menuLabel.style.paddingTop = rs.paddingTop; } catch { }
            try { _menuLabel.style.paddingBottom = rs.paddingBottom; } catch { }
            try
            {
                var nativeRow = FindRowOf(nativeText, list);
                if (nativeRow != null)
                {
                    var rowRs = nativeRow.resolvedStyle;
                    float h = rowRs.height;
                    if (h > 8f && h < 100f) _menuElement.style.height = h;
                    try { _menuElement.style.marginTop = rowRs.marginTop; } catch { }
                    try { _menuElement.style.marginBottom = rowRs.marginBottom; } catch { }
                    float indentX = nativeText.worldBound.x - nativeRow.worldBound.x;
                    float indentY = nativeText.worldBound.y - nativeRow.worldBound.y;
                    if (indentX >= 0f && indentX < 200f) _menuElement.style.paddingLeft = indentX;
                    if (indentY >= 0f && indentY < 50f)
                    {
                        // switch from center-alignment to the native row's
                        // exact top offset — centering in a not-quite-native
                        // height is what read as "more centered" on screen
                        _menuElement.style.alignItems = Align.FlexStart;
                        _menuElement.style.paddingTop = indentY;
                    }
                }
            }
            catch { }
            _menuStyleCopied = true;
        }
        catch { }
    }

    /// <summary>Walks up from a text element to the direct child of
    /// <paramref name="list"/> that contains it (the native ROW container).
    /// Identity via .Pointer — interop wrappers are re-created per property
    /// access, so managed reference equality on parents is meaningless.</summary>
    private static VisualElement FindRowOf(VisualElement text, VisualElement list)
    {
        var node = text;
        int guard = 0;
        while (node != null && guard++ < 10)
        {
            VisualElement p;
            try { p = node.parent; } catch { return null; }
            if (p == null) return null;
            try { if (p.Pointer == list.Pointer) return node; } catch { return null; }
            node = p;
        }
        return null;
    }

    /// <summary>Finds the "View" node under floating-element-root whose row
    /// texts match at least two of RecruitmentMenuFingerprint. Depth/hit caps
    /// keep this bounded even if the shared overlay layer is currently
    /// holding more than one dropdown's (mostly hidden, still-attached —
    /// see the persistence recon note above) item list at once.</summary>
    private static VisualElement FindRecruitmentMenuList(VisualElement floatingRoot)
    {
        var candidates = new List<VisualElement>();
        CollectByName(floatingRoot, MenuListNodeName, 0, candidates, 12);
        foreach (var view in candidates)
        {
            var hits = new List<string>();
            CollectTextsShallow(view, 0, hits);
            int matches = 0;
            foreach (var f in RecruitmentMenuFingerprint)
                if (hits.Contains(f)) matches++;
            if (matches >= 2) return view;
        }
        return null;
    }

    private static void CollectByName(VisualElement node, string name, int depth, List<VisualElement> hits, int maxHits)
    {
        if (node == null || depth > 20 || hits.Count >= maxHits) return;
        try { if (node.name == name) hits.Add(node); } catch { }
        for (int i = 0; i < node.childCount && hits.Count < maxHits; i++)
            CollectByName(node[i], name, depth + 1, hits, maxHits);
    }

    /// <summary>Depth/hit-capped TextElement text collector, local to menu
    /// injection (deliberately not reusing Navigator's WalkDeepText, which is
    /// private to that file, for a one-off need).</summary>
    private static void CollectTextsShallow(VisualElement node, int depth, List<string> hits)
    {
        if (node == null || depth > 6 || hits.Count >= 40) return;
        for (int i = 0; i < node.childCount && hits.Count < 40; i++)
        {
            VisualElement child;
            try { child = node[i]; } catch { continue; }
            try
            {
                var te = child.TryCast<TextElement>();
                if (te != null && !string.IsNullOrWhiteSpace(te.text)) hits.Add(te.text);
            }
            catch { }
            try { CollectTextsShallow(child, depth + 1, hits); } catch { }
        }
    }

    /// <summary>Builds the injected row. Sized/padded to line up with the
    /// native rows' text column (paddingLeft 24 ~= native BindingExpect x369
    /// minus View x345) but built from a plain VisualElement+Label, same
    /// reasoning as the other builders here (avoids Il2CppSystem.Action plumbing).
    /// Inserted via list.Insert(0, row) in TryInjectMenuItem (see requirement
    /// A) so it's always first regardless of populate-order race.
    ///
    /// Styling (addendum, from a pixel-sampled live screenshot):
    /// a glass-purple accent look "looks off" next to native rows, so this
    /// deliberately looks like a plain native row instead — transparent
    /// background, no border/radius, text color/size taken from
    /// FallbackMenuTextColor/FallbackMenuFontSize (#CCC1F3, ~13px, matching
    /// the sampled native glyph-core average) until MaybeCopyNativeStyle can
    /// read and apply a real sibling's resolvedStyle. Hover stays restrained:
    /// only the row background tints (MenuHoverColor, rgba(135,25,178,0.25)),
    /// text color never changes on hover.</summary>
    private static VisualElement BuildMenuRow()
    {
        var row = new VisualElement { name = MenuItemName };
        row.pickingMode = PickingMode.Position;
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.height = 32;
        row.style.paddingLeft = 24;
        row.style.paddingRight = 24;
        row.style.backgroundColor = MenuBaseColor;

        var label = new Label(MenuItemLabel);
        label.pickingMode = PickingMode.Ignore;
        label.style.fontSize = FallbackMenuFontSize;
        label.style.color = FallbackMenuTextColor;
        label.style.unityFontStyleAndWeight = FontStyle.Normal;
        // Zero the default-theme Label box so the row's paddingLeft is the
        // ONLY indent; MaybeCopyNativeStyle overwrites these with the native
        // text element's real box once a sibling row exists.
        label.style.marginLeft = 0; label.style.marginRight = 0;
        label.style.marginTop = 0; label.style.marginBottom = 0;
        label.style.paddingLeft = 0; label.style.paddingRight = 0;
        label.style.paddingTop = 0; label.style.paddingBottom = 0;
        row.Add(label);
        _menuLabel = label;

        row.RegisterCallback<ClickEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                (Action<ClickEvent>)(_ => OnMenuClicked())));
        row.RegisterCallback<MouseEnterEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseEnterEvent>>(
                (Action<MouseEnterEvent>)(_ =>
                {
                    row.style.backgroundColor = MenuHoverColor;
                })));
        row.RegisterCallback<MouseLeaveEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseLeaveEvent>>(
                (Action<MouseLeaveEvent>)(_ =>
                {
                    row.style.backgroundColor = MenuBaseColor;
                })));

        return row;
    }

    private static void OnMenuClicked()
    {
        _menuClickCount++;
        _menuLastClickTick = _clock.ElapsedMilliseconds;
        Navigator._log?.LogInfo($"[Bridge] {MenuItemName} clicked (n={_menuClickCount})");
        // Guarded separately from the counter/logging above: a failure inside
        // the overlay toggle (e.g. root not found) must never look like the
        // menu row itself failed to register the click.
        try { ToggleOverlay(); } catch (Exception e) { Navigator._log?.LogWarning($"[Bridge] ToggleOverlay from menu click failed: {e.Message}"); }
    }

    /// <summary>Builds the chat overlay panel — see the class-level
    /// "Overlay injection" region doc above for placement rationale (last
    /// child of PanelManager root, not floating-element-root). Header
    /// (title + New chat + close), scrollable message area, working input
    /// row (outbox drained by overlay_poll). All child Labels are
    /// pickingMode Ignore except the clickable buttons.</summary>
    private static VisualElement BuildOverlay()
    {
        var overlay = new VisualElement { name = OverlayName };
        overlay.pickingMode = PickingMode.Position;
        overlay.style.position = Position.Absolute;
        overlay.style.right = OverlayRight;
        overlay.style.top = OverlayTop;
        overlay.style.width = OverlayWidth;
        overlay.style.height = OverlayHeight;
        overlay.style.flexDirection = FlexDirection.Column;
        overlay.style.backgroundColor = OverlayBackgroundColor;
        overlay.style.borderTopWidth = 1;
        overlay.style.borderRightWidth = 1;
        overlay.style.borderBottomWidth = 1;
        overlay.style.borderLeftWidth = 1;
        overlay.style.borderTopColor = OverlayBorderColor;
        overlay.style.borderRightColor = OverlayBorderColor;
        overlay.style.borderBottomColor = OverlayBorderColor;
        overlay.style.borderLeftColor = OverlayBorderColor;
        overlay.style.borderTopLeftRadius = OverlayRadius;
        overlay.style.borderTopRightRadius = OverlayRadius;
        overlay.style.borderBottomLeftRadius = OverlayRadius;
        overlay.style.borderBottomRightRadius = OverlayRadius;

        overlay.Add(BuildOverlayHeader());
        overlay.Add(BuildOverlayMessageArea());
        overlay.Add(BuildOverlayInputRow());

        return overlay;
    }

    private static VisualElement BuildOverlayHeader()
    {
        var header = new VisualElement();
        header.pickingMode = PickingMode.Ignore;
        header.style.height = OverlayHeaderHeight;
        header.style.flexShrink = 0; // only the message scroll area may flex; a long reply must never squash the header
        header.style.flexDirection = FlexDirection.Row;
        header.style.alignItems = Align.Center;
        header.style.paddingLeft = 16;
        header.style.paddingRight = 8;
        header.style.borderBottomWidth = 1;
        header.style.borderBottomColor = OverlayHairlineColor;

        var title = new Label("Chat with DoF");
        title.pickingMode = PickingMode.Ignore;
        title.style.fontSize = 14;
        title.style.color = FallbackMenuTextColor;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.Add(title);

        var spacer = new VisualElement();
        spacer.pickingMode = PickingMode.Ignore;
        spacer.style.flexGrow = 1;
        header.Add(spacer);

        var newChat = new VisualElement();
        newChat.pickingMode = PickingMode.Position;
        newChat.style.height = OverlayCloseButtonSize;
        newChat.style.paddingLeft = 8;
        newChat.style.paddingRight = 8;
        newChat.style.marginRight = 4;
        newChat.style.alignItems = Align.Center;
        newChat.style.justifyContent = Justify.Center;
        newChat.style.borderTopLeftRadius = 6;
        newChat.style.borderTopRightRadius = 6;
        newChat.style.borderBottomLeftRadius = 6;
        newChat.style.borderBottomRightRadius = 6;
        newChat.style.backgroundColor = new Color(0f, 0f, 0f, 0f);

        var newChatLabel = new Label("New chat");
        newChatLabel.pickingMode = PickingMode.Ignore;
        newChatLabel.style.fontSize = 12;
        newChatLabel.style.color = FallbackMenuTextColor;
        newChat.Add(newChatLabel);

        newChat.RegisterCallback<ClickEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                (Action<ClickEvent>)(_ => OnNewChatClicked())));
        newChat.RegisterCallback<MouseEnterEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseEnterEvent>>(
                (Action<MouseEnterEvent>)(_ => { newChat.style.backgroundColor = OverlayCloseHoverColor; })));
        newChat.RegisterCallback<MouseLeaveEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseLeaveEvent>>(
                (Action<MouseLeaveEvent>)(_ => { newChat.style.backgroundColor = new Color(0f, 0f, 0f, 0f); })));

        header.Add(newChat);

        var close = new VisualElement();
        close.pickingMode = PickingMode.Position;
        close.style.width = OverlayCloseButtonSize;
        close.style.height = OverlayCloseButtonSize;
        close.style.alignItems = Align.Center;
        close.style.justifyContent = Justify.Center;
        close.style.backgroundColor = new Color(0f, 0f, 0f, 0f);

        var closeLabel = new Label("✕");
        closeLabel.pickingMode = PickingMode.Ignore;
        closeLabel.style.color = FallbackMenuTextColor;
        close.Add(closeLabel);

        close.RegisterCallback<ClickEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                (Action<ClickEvent>)(_ => OnOverlayCloseClicked())));
        close.RegisterCallback<MouseEnterEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseEnterEvent>>(
                (Action<MouseEnterEvent>)(_ => { close.style.backgroundColor = OverlayCloseHoverColor; })));
        close.RegisterCallback<MouseLeaveEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseLeaveEvent>>(
                (Action<MouseLeaveEvent>)(_ => { close.style.backgroundColor = new Color(0f, 0f, 0f, 0f); })));

        header.Add(close);
        return header;
    }

    /// <summary>Builds the scrollable message area plus the dimmed system
    /// line, and caches the live references AppendBubble/OverlayPost/
    /// OverlayClear read (_overlayScroll/_overlayMessageArea/
    /// _overlaySystemLine). The v1 shell used overflow:Hidden +
    /// justifyContent:FlexEnd with no scrollbar; once a conversation
    /// outgrew the panel the bubbles overlapped each other (live-observed),
    /// so the named message area is now a plain column INSIDE
    /// a ScrollView — bubble code keeps finding it by name (FindByName's
    /// childCount/indexer walk proxies through the ScrollView's
    /// contentContainer) and never needs to know about the wrapper.</summary>
    private static VisualElement BuildOverlayMessageArea()
    {
        var scroll = new ScrollView { name = ScrollName };
        scroll.style.flexGrow = 1;
        // The scroll area is the ONLY element in the overlay column allowed
        // to flex: it absorbs all height changes (minHeight 0 lets it shrink
        // past its content) while the header/input row pin themselves with
        // flexShrink 0 — otherwise a tall conversation squashes the input row.
        scroll.style.flexShrink = 1;
        scroll.style.minHeight = 0;
        StyleOverlayScrollbar(scroll);

        var area = new VisualElement { name = MessageAreaName };
        area.style.paddingLeft = 12;
        area.style.paddingRight = 12;
        area.style.paddingTop = 12;
        area.style.flexDirection = FlexDirection.Column;

        var sys = BuildSystemLine();
        area.Add(sys);
        scroll.Add(area);

        _overlayScroll = scroll;
        _overlayMessageArea = area;
        _overlaySystemLine = sys;
        return scroll;
    }

    /// <summary>Restyles the message ScrollView's vertical scrollbar to sit
    /// flat against the panel: slim near-invisible track, rounded
    /// low-contrast thumb that brightens on hover, no arrow buttons. The
    /// game themes its own scrollbars through USS on its own controls —
    /// none of that reaches a programmatically-built panel, so without this
    /// the default UI Toolkit scroller chrome (grey track, boxy arrows)
    /// shows through. Every property is guarded per element/sub-element: if
    /// an interop build lacks one, the scrollbar keeps default looks there
    /// but stays functional.</summary>
    private static void StyleOverlayScrollbar(ScrollView scroll)
    {
        try
        {
            try
            {
                var hs = scroll.horizontalScroller;
                if (hs != null) hs.style.display = DisplayStyle.None;
            }
            catch { }

            var scroller = scroll.verticalScroller;
            if (scroller == null) return;
            try { scroller.style.width = 8; } catch { }
            try { scroller.style.backgroundColor = new Color(0f, 0f, 0f, 0f); } catch { }
            try { scroller.style.borderLeftWidth = 0; } catch { }

            try { if (scroller.lowButton != null) scroller.lowButton.style.display = DisplayStyle.None; } catch { }
            try { if (scroller.highButton != null) scroller.highButton.style.display = DisplayStyle.None; } catch { }

            try
            {
                var slider = scroller.slider;
                if (slider != null)
                {
                    slider.style.width = 8;
                    try { slider.style.marginTop = 2; } catch { }
                    try { slider.style.marginBottom = 2; } catch { }
                    try { slider.style.backgroundColor = new Color(0f, 0f, 0f, 0f); } catch { }
                }
            }
            catch { }

            // "unity-tracker"/"unity-dragger" are the element names UI
            // Toolkit's Slider assigns its track and thumb children.
            var tracker = FindByName(scroller, "unity-tracker", 0);
            if (tracker != null)
            {
                try { tracker.style.backgroundColor = OverlayScrollTrackColor; } catch { }
                try { tracker.style.borderTopWidth = 0; } catch { }
                try { tracker.style.borderRightWidth = 0; } catch { }
                try { tracker.style.borderBottomWidth = 0; } catch { }
                try { tracker.style.borderLeftWidth = 0; } catch { }
                try { tracker.style.borderTopLeftRadius = 4; } catch { }
                try { tracker.style.borderTopRightRadius = 4; } catch { }
                try { tracker.style.borderBottomLeftRadius = 4; } catch { }
                try { tracker.style.borderBottomRightRadius = 4; } catch { }
            }

            var dragger = FindByName(scroller, "unity-dragger", 0);
            if (dragger != null)
            {
                try { dragger.style.backgroundColor = OverlayScrollThumbColor; } catch { }
                try { dragger.style.width = 8; } catch { }
                try { dragger.style.left = 0; } catch { }
                try { dragger.style.borderTopWidth = 0; } catch { }
                try { dragger.style.borderRightWidth = 0; } catch { }
                try { dragger.style.borderBottomWidth = 0; } catch { }
                try { dragger.style.borderLeftWidth = 0; } catch { }
                try { dragger.style.borderTopLeftRadius = 4; } catch { }
                try { dragger.style.borderTopRightRadius = 4; } catch { }
                try { dragger.style.borderBottomLeftRadius = 4; } catch { }
                try { dragger.style.borderBottomRightRadius = 4; } catch { }
                try
                {
                    dragger.RegisterCallback<MouseEnterEvent>(
                        Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseEnterEvent>>(
                            (Action<MouseEnterEvent>)(_ => { dragger.style.backgroundColor = OverlayScrollThumbHoverColor; })));
                    dragger.RegisterCallback<MouseLeaveEvent>(
                        Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseLeaveEvent>>(
                            (Action<MouseLeaveEvent>)(_ => { dragger.style.backgroundColor = OverlayScrollThumbColor; })));
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>The dimmed "listening" placeholder line — built on first
    /// overlay creation and re-added by OverlayClear so a wiped panel (new
    /// chat) doesn't sit fully empty.</summary>
    private static Label BuildSystemLine()
    {
        var sys = new Label(SystemLineText);
        sys.pickingMode = PickingMode.Ignore;
        sys.style.whiteSpace = WhiteSpace.Normal;
        sys.style.color = OverlayPlaceholderColor;
        sys.style.fontSize = 12;
        sys.style.marginTop = 8;
        sys.style.alignSelf = Align.Center;
        return sys;
    }

    /// <summary>Scrolls the message ScrollView to the bottom. Called from a
    /// GeometryChangedEvent registered on each new bubble, i.e. after the
    /// layout pass that sized it — computing the offset from content vs
    /// viewport height directly avoids depending on when the scroller
    /// updates its own range. Re-resolves a stale ScrollView reference by
    /// name (same staleness pattern as _overlayMessageArea).</summary>
    private static void ScrollToBottom()
    {
        try
        {
            var scroll = _overlayScroll;
            try { if (scroll != null && scroll.parent == null) scroll = null; } catch { scroll = null; }
            if (scroll == null)
            {
                var overlay = _overlayElement;
                try { if (overlay != null && overlay.parent == null) overlay = null; } catch { overlay = null; }
                var el = overlay != null ? FindByName(overlay, ScrollName, 0) : null;
                scroll = el != null ? el.TryCast<ScrollView>() : null;
                _overlayScroll = scroll;
            }
            if (scroll == null) return;
            var area = _overlayMessageArea;
            if (area == null) return;

            float content = area.layout.height;
            float viewport = 0f;
            try { viewport = scroll.contentViewport.layout.height; } catch { }
            float target = content - viewport;
            if (target < 0f) target = 0f;
            scroll.scrollOffset = new Vector2(0f, target);
        }
        catch { }
    }

    private static VisualElement BuildOverlayBubble(string text, bool isDof)
    {
        var bubble = new VisualElement();
        bubble.pickingMode = PickingMode.Ignore;
        bubble.style.alignSelf = isDof ? Align.FlexStart : Align.FlexEnd;
        bubble.style.backgroundColor = isDof ? OverlayDofBubbleColor : OverlayUserBubbleColor;
        bubble.style.maxWidth = OverlayBubbleMaxWidth;
        bubble.style.borderTopLeftRadius = 6;
        bubble.style.borderTopRightRadius = 6;
        bubble.style.borderBottomLeftRadius = 6;
        bubble.style.borderBottomRightRadius = 6;
        bubble.style.paddingLeft = 8;
        bubble.style.paddingRight = 8;
        bubble.style.paddingTop = 8;
        bubble.style.paddingBottom = 8;
        bubble.style.marginBottom = 8;

        var label = new Label(text);
        label.pickingMode = PickingMode.Ignore;
        label.style.whiteSpace = WhiteSpace.Normal;
        label.style.color = FallbackMenuTextColor;
        label.style.fontSize = 13;
        bubble.Add(label);

        return bubble;
    }

    private static bool _exclusiveInput;

    /// <summary>Toggles the game's exclusive-input mode around chat-field
    /// focus. FM26's keyboard shortcuts are Unity InputActions polled
    /// independently of UI Toolkit focus, so while the chat input has
    /// focus we ask the game's own input manager to suspend them — the
    /// same mechanism the game's native text boxes use (its override
    /// disables the shortcut action maps on begin, restores on end).
    /// Reentrancy-guarded so a double focus-in can't unbalance End.</summary>
    private static void SetExclusiveInput(bool on)
    {
        try
        {
            var mgr = SI.Input.InputManager.Instance;
            if (mgr == null || on == _exclusiveInput) return;
            _exclusiveInput = on;
            if (on) mgr.OnExclusiveInputBegin();
            else mgr.OnExclusiveInputEnd();
            Navigator._log?.LogInfo($"[Bridge] chat input exclusive-input {(on ? "begin" : "end")} (game flag={mgr.IsExclusiveInputActive})");
        }
        catch (Exception e)
        {
            Navigator._log?.LogWarning($"[Bridge] exclusive-input toggle failed: {e.Message}");
        }
    }

    /// <summary>Builds the real input row: a single-line TextField plus a
    /// send button (same VisualElement+Label+hover pattern as the header
    /// close button — see BuildOverlayHeader). Submit (Enter or the button
    /// click) is handled by OnOverlaySubmit for both. The row keeps its
    /// original dark bg/border look; the TextField's own chrome
    /// (background/border) is stripped inline, best-effort, to blend into it
    /// — function first, per the interop note on this method's call site;
    /// no native placeholder text is attempted (that property isn't a plain
    /// field on this interop build and the field works fine without it).</summary>
    private static VisualElement BuildOverlayInputRow()
    {
        var row = new VisualElement();
        row.pickingMode = PickingMode.Ignore;
        row.style.height = OverlayInputHeight;
        row.style.minHeight = OverlayInputHeight;
        row.style.flexShrink = 0; // pinned: only the message scroll area flexes (see BuildOverlayMessageArea)
        row.style.marginLeft = 12;
        row.style.marginRight = 12;
        row.style.marginTop = 12;
        row.style.marginBottom = 12;
        row.style.backgroundColor = OverlayInputBgColor;
        row.style.borderTopLeftRadius = 6;
        row.style.borderTopRightRadius = 6;
        row.style.borderBottomLeftRadius = 6;
        row.style.borderBottomRightRadius = 6;
        row.style.borderTopWidth = 1;
        row.style.borderRightWidth = 1;
        row.style.borderBottomWidth = 1;
        row.style.borderLeftWidth = 1;
        row.style.borderTopColor = OverlayHairlineColor;
        row.style.borderRightColor = OverlayHairlineColor;
        row.style.borderBottomColor = OverlayHairlineColor;
        row.style.borderLeftColor = OverlayHairlineColor;
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.paddingLeft = 12;
        row.style.paddingRight = 6;

        // Interop note: construct with no-arg `new TextField()` (the
        // label-taking ctor isn't used — this field carries no separate
        // label), then set .name after. .value/.SetValueWithoutNotify exist
        // directly on the managed wrapper, no TryCast needed for those.
        TextField field = null;
        try
        {
            field = new TextField();
            field.name = InputFieldName;
            field.multiline = false;
            field.style.flexGrow = 1;
            field.style.marginRight = 8;
            field.style.marginLeft = 0;
            field.style.marginTop = 0;
            field.style.marginBottom = 0;
            field.style.paddingLeft = 0;
            field.style.paddingTop = 0;
            field.style.paddingBottom = 0;
            field.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
            field.style.borderTopWidth = 0;
            field.style.borderRightWidth = 0;
            field.style.borderBottomWidth = 0;
            field.style.borderLeftWidth = 0;
            field.style.color = FallbackMenuTextColor;
            field.style.fontSize = 13;

            // Best-effort reach into the internal text-input child to strip
            // its own background/border too — the outer TextField's style
            // above doesn't reliably repaint it on every UI Toolkit build.
            // Guarded individually; if the internal shape differs, the field
            // still functions, just with whatever native chrome shows through.
            try
            {
                if (field.childCount > 0)
                {
                    var inner = field[0];
                    if (inner != null)
                    {
                        try { inner.style.backgroundColor = new Color(0f, 0f, 0f, 0f); } catch { }
                        try { inner.style.borderTopWidth = 0; } catch { }
                        try { inner.style.borderRightWidth = 0; } catch { }
                        try { inner.style.borderBottomWidth = 0; } catch { }
                        try { inner.style.borderLeftWidth = 0; } catch { }
                        try { inner.style.paddingLeft = 0; } catch { }
                        try { inner.style.color = FallbackMenuTextColor; } catch { }
                    }
                }
            }
            catch { }

            field.RegisterCallback<KeyDownEvent>(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(
                    (Action<KeyDownEvent>)(evt =>
                    {
                        try
                        {
                            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                            {
                                OnOverlaySubmit(field);
                                evt.StopPropagation();
                            }
                        }
                        catch { }
                    })),
                TrickleDown.TrickleDown);

            // FM26 routes keyboard shortcuts through Unity InputActions
            // (the game's input manager), not UI Toolkit key events, so a
            // focused TextField alone doesn't stop the game reacting to
            // every letter typed. The game's own edit boxes suppress this
            // via the input manager's exclusive-input mode; mirror that:
            // begin on focus-in, end on focus-out. Belt-and-braces, also
            // swallow key events at the bubble phase so nothing above the
            // field sees them (bubble runs after the field's internal text
            // input has consumed the keystroke, so typing still works).
            field.RegisterCallback<FocusInEvent>(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<FocusInEvent>>(
                    (Action<FocusInEvent>)(_ => SetExclusiveInput(true))));
            field.RegisterCallback<FocusOutEvent>(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<FocusOutEvent>>(
                    (Action<FocusOutEvent>)(_ => SetExclusiveInput(false))));
            field.RegisterCallback<KeyDownEvent>(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(
                    (Action<KeyDownEvent>)(evt => { try { evt.StopPropagation(); } catch { } })));
            field.RegisterCallback<KeyUpEvent>(
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<KeyUpEvent>>(
                    (Action<KeyUpEvent>)(evt => { try { evt.StopPropagation(); } catch { } })));
        }
        catch (Exception e)
        {
            Navigator._log?.LogWarning($"[Bridge] overlay input field build failed: {e.Message}");
        }

        if (field != null) row.Add(field);

        var send = new VisualElement();
        send.pickingMode = PickingMode.Position;
        send.style.width = SendButtonSize;
        send.style.height = OverlayInputHeight - 12;
        send.style.alignItems = Align.Center;
        send.style.justifyContent = Justify.Center;
        send.style.borderTopLeftRadius = 6;
        send.style.borderTopRightRadius = 6;
        send.style.borderBottomLeftRadius = 6;
        send.style.borderBottomRightRadius = 6;
        send.style.backgroundColor = new Color(0f, 0f, 0f, 0f);

        var sendLabel = new Label("➤");
        sendLabel.pickingMode = PickingMode.Ignore;
        sendLabel.style.color = FallbackMenuTextColor;
        send.Add(sendLabel);

        send.RegisterCallback<ClickEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                (Action<ClickEvent>)(_ => OnOverlaySubmit(field))));
        send.RegisterCallback<MouseEnterEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseEnterEvent>>(
                (Action<MouseEnterEvent>)(_ => { send.style.backgroundColor = OverlayCloseHoverColor; })));
        send.RegisterCallback<MouseLeaveEvent>(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<EventCallback<MouseLeaveEvent>>(
                (Action<MouseLeaveEvent>)(_ => { send.style.backgroundColor = new Color(0f, 0f, 0f, 0f); })));

        row.Add(send);
        return row;
    }

    /// <summary>Submit handler shared by Enter-in-field and the send button
    /// click. Whitespace-only input is silently ignored (no bubble, no
    /// outbox entry, field left as-is). On real input: enqueue {text, ts}
    /// into the outbox for overlay_poll, echo it locally as a "user" bubble
    /// immediately (no round-trip needed for the local UI to reflect it),
    /// then clear the field via SetValueWithoutNotify (plain .value = ""
    /// would fire a ChangeEvent we don't want here).</summary>
    private static void OnOverlaySubmit(TextField field)
    {
        if (field == null) return;
        try
        {
            string text;
            try { text = field.value; } catch { text = null; }
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > MaxMessageChars) text = text.Substring(0, MaxMessageChars);

            var ts = _clock.ElapsedMilliseconds;
            lock (_outboxLock)
            {
                _outbox.Add((text, ts));
            }

            AppendBubble("user", text);
            try { field.SetValueWithoutNotify(""); } catch { }
        }
        catch (Exception e)
        {
            Navigator._log?.LogWarning($"[Bridge] overlay input submit failed: {e.Message}");
        }
    }

    /// <summary>Header "New chat" button: wipes the visible conversation and
    /// asks the host service (via the next overlay_poll's new_chat flag) to
    /// drop its headless session. Any not-yet-polled outbox text is discarded
    /// too — those messages belong to the conversation being abandoned.</summary>
    private static void OnNewChatClicked()
    {
        lock (_outboxLock)
        {
            _outbox.Clear();
            _newChatPending = true;
        }
        try { OverlayClear(); } catch { }
        Navigator._log?.LogInfo($"[Bridge] {OverlayName} new-chat clicked: bubbles cleared, session reset queued for next poll");
    }

    private static void OnOverlayCloseClicked()
    {
        _overlayCloseCount++;
        Navigator._log?.LogInfo($"[Bridge] {OverlayName} close clicked (n={_overlayCloseCount})");
        try
        {
            if (_overlayElement != null) _overlayElement.style.display = DisplayStyle.None;
        }
        catch { }
    }

    private static JsonObject Geometry(VisualElement el)
    {
        var world = el.worldBound;
        var layout = el.layout;
        return new JsonObject
        {
            ["world"] = new JsonObject { ["x"] = (int)world.x, ["y"] = (int)world.y, ["w"] = (int)world.width, ["h"] = (int)world.height },
            ["layout"] = new JsonObject { ["x"] = (int)layout.x, ["y"] = (int)layout.y, ["w"] = (int)layout.width, ["h"] = (int)layout.height },
        };
    }

    /// <summary>Exact-name walk over the live UI Toolkit visual tree, same
    /// shape as Navigator.Collect but returns the first
    /// hit instead of accumulating a list.</summary>
    private static VisualElement FindByName(VisualElement node, string name, int depth)
    {
        if (node == null || depth > 64) return null;
        try { if (node.name == name) return node; } catch { }
        for (int i = 0; i < node.childCount; i++)
        {
            var hit = FindByName(node[i], name, depth + 1);
            if (hit != null) return hit;
        }
        return null;
    }
}
