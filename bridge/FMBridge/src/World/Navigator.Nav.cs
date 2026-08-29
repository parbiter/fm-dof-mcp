using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using SI.Bindable;
using SI.Core;
using FM.UI;
using System.Text.Json.Nodes;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace FMBridge.World;

/// <summary>
/// Verbal navigation (part 2): resolve a panel by name/address, load it via
/// Addressables, then Show/Open through PanelManager. Results broadcast as
/// on_nav events; loading is polled from the main-thread tick.
/// </summary>
internal static partial class Navigator
{
    private static Dictionary<string, string> _addresses; // name -> address

    private static Dictionary<string, string> AddressTable()
    {
        if (_addresses != null) return _addresses;
        _addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LoadTable("panel-guids.json");
        LoadTable("panel-asset-ids.json");
        if (_addresses.Count == 0) LoadTable("panel-ids.json");
        return _addresses;
    }

    private static void LoadTable(string file)
    {
        try
        {
            var path = Path.Combine(Paths.PluginPath, "FMBridge", file);
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string val = null;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    val = prop.Value.GetString();
                else if (prop.Value.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String)
                    val = addr.GetString();
                if (val != null && !_addresses.ContainsKey(prop.Name))
                    _addresses[prop.Name] = val;
            }
        }
        catch { }
    }

    private static string ResolveAddress(string name)
    {
        var table = AddressTable();
        if (name.Contains('/')) return name; // passthrough full address
        if (table.TryGetValue(name, out var exact)) return exact;
        foreach (var kv in table)
        {
            if (kv.Key.EndsWith(name, StringComparison.OrdinalIgnoreCase) ||
                kv.Value.EndsWith("/" + name + ".uxml", StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    private static AsyncOperationHandle<PanelID> _pending;
    private static string _pendingName;
    private static string _pendingParamName;
    private static SI.Core.TypedValue _pendingParamValue;
    private static List<(string name, SI.Core.TypedValue value)> _pendingArgs;
    private static Panel.TransitionCompleteCallback _cb;
    private static Action<JsonObject> _broadcast;
    internal static BepInEx.Logging.ManualLogSource _log;
    private static bool _lastOk;
    private static string _lastVia = "";

    public static void Init(Action<JsonObject> broadcast) => _broadcast = broadcast;

    public static JsonObject NavOpen(string panel) => NavOpenEx(panel, null, null);

    public static JsonObject NavOpenEx(string panel, string paramName, SI.Core.TypedValue paramValue)
    {
        try
        {
            if (string.IsNullOrEmpty(panel)) return new JsonObject { ["ok"] = false, ["error"] = "no-panel" };
            if (_pendingName != null) return new JsonObject { ["ok"] = false, ["error"] = "load-in-progress:" + _pendingName };
            var pm = Pm();
            if (pm == null) return new JsonObject { ["ok"] = false, ["reason"] = "panelmanager-null" };
            var addr = ResolveAddress(panel);
            if (addr == null) return new JsonObject { ["ok"] = false, ["error"] = "unknown-panel:" + panel };
            _log?.LogInfo($"[Nav] opening '{panel}' -> key='{addr}'");
            _pending = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<PanelID>(addr);
            _pendingName = panel;
            _pendingParamName = paramName;
            _pendingParamValue = paramValue;
            EnsureCallback();
            return new JsonObject { ["ok"] = true, ["loading"] = true, ["address"] = addr };
        }
        catch (Exception e)
        {
            _pending = null;
            _pendingName = null;
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// Multi-arg panel open: Build(pid, default(Key), cb, anchor).Arg(n1,v1).Arg(n2,v2)....Open().
    /// This is the only way to express more than one context parameter
    /// (the decompiled pm.Open only carries a single name/value pair).
    /// </summary>
    public static JsonObject NavOpenArgs(string panel, List<(string name, SI.Core.TypedValue value)> args)
    {
        try
        {
            if (string.IsNullOrEmpty(panel)) return new JsonObject { ["ok"] = false, ["error"] = "no-panel" };
            if (args == null || args.Count == 0) return new JsonObject { ["ok"] = false, ["error"] = "no-args" };
            if (_pendingName != null) return new JsonObject { ["ok"] = false, ["error"] = "load-in-progress:" + _pendingName };
            var pm = Pm();
            if (pm == null) return new JsonObject { ["ok"] = false, ["reason"] = "panelmanager-null" };
            var addr = ResolveAddress(panel);
            if (addr == null) return new JsonObject { ["ok"] = false, ["error"] = "unknown-panel:" + panel };
            _log?.LogInfo($"[Nav] opening '{panel}' -> key='{addr}' args={args.Count}");
            _pending = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<PanelID>(addr);
            _pendingName = panel;
            _pendingArgs = args;
            EnsureCallback();
            return new JsonObject { ["ok"] = true, ["loading"] = true, ["address"] = addr };
        }
        catch (Exception e)
        {
            _pending = null;
            _pendingName = null;
            _pendingArgs = null;
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    // Scratch-node counter for uid-scoped opens. Own namespace __bridge.nav.N
    // so planted nodes never collide with ReadEntity's __bridge.re.N or any
    // other __bridge.* scratch namespace.
    private static int _navCounter;

    /// <summary>
    /// uid-scoped nav_open: fabricates the FM.UI
    /// reference for a raw uid (PersonReference by default; ref_kind override
    /// maps person|club|nation|competition -> PersonReference/ClubReference/
    /// NationReference/CompReference — all classes with a public .ctor(int),
    /// a fabrication route proven live), plants it as TypedValue on
    /// a fresh __bridge.nav.N scratch node via NativeBindings.CreatePath+Set,
    /// then feeds it into the exact nav_person-style plumbing (NavOpenEx /
    /// NavOpenArgs) — zero downstream panel-open changes.
    ///
    /// args entries accept BOTH shapes: legacy {name,nodePath} resolved
    /// through the tree exactly like nav_args, and new {name,value} where
    /// value is a raw uid planted with the same ref_kind.
    /// </summary>
    public static JsonObject NavOpenScoped(BindingSubsystem bindings, string panel, long entityUid, string refKind, string paramName, JsonNode argsNode)
    {
        var planted = new JsonArray();
        try
        {
            var args = new List<(string name, SI.Core.TypedValue value)>();

            if (entityUid >= 0)
            {
                var (path, tv, err) = PlantEntityRef(bindings, entityUid, refKind);
                if (path == null) return new JsonObject { ["ok"] = false, ["error"] = err };
                planted.Add(path);
                args.Add((string.IsNullOrEmpty(paramName) ? "player" : paramName, tv));
            }

            if (argsNode is JsonArray ja)
            {
                foreach (var item in ja)
                {
                    var name = (string)item?["name"];
                    if (string.IsNullOrEmpty(name))
                        return new JsonObject { ["ok"] = false, ["error"] = "args entry missing 'name'" };
                    var legacy = (string)item?["nodePath"];
                    if (!string.IsNullOrEmpty(legacy))
                    {
                        var tv = FMBridge.Eyes.TreeWalker.FindValueTyped(bindings, legacy);
                        if (tv == null)
                            return new JsonObject { ["ok"] = false, ["error"] = "node-not-found:" + legacy };
                        args.Add((name, tv));
                        continue;
                    }
                    if (item?["value"] is JsonValue vv && vv.TryGetValue<long>(out var uid))
                    {
                        var (path, tv, err) = PlantEntityRef(bindings, uid, refKind);
                        if (path == null) return new JsonObject { ["ok"] = false, ["error"] = err };
                        planted.Add(path);
                        args.Add((name, tv));
                        continue;
                    }
                    return new JsonObject { ["ok"] = false, ["error"] = "args entry '" + name + "' needs nodePath or numeric value" };
                }
            }

            JsonObject result;
            if (args.Count == 0) result = NavOpen(panel);                       // plain open, unchanged
            else if (args.Count == 1) result = NavOpenEx(panel, args[0].name, args[0].value);   // pm.Open single-param route (nav_person parity)
            else result = NavOpenArgs(panel, args);                             // Build+Arg+Open for 2+ params
            if (planted.Count > 0) result["planted"] = planted;
            return result;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>Fabricates FM.UI.{Person|Club|Nation|Comp}Reference(uid) and
    /// parks it as TypedValue on a fresh real __bridge.nav.N node
    /// (NativeBindings.CreatePath + Set with UpdateHandler|ForceUpdateValue —
    /// the same fabricate-and-plant mechanics ReadEntity uses). Returns the scratch path plus
    /// the wrapped TypedValue (the same object a tree walk would hand to
    /// nav_person), or an error.</summary>
    private static (string path, SI.Core.TypedValue value, string error) PlantEntityRef(BindingSubsystem bindings, long uid, string refKind)
    {
        if (uid < 0 || uid > int.MaxValue) return (null, null, "uid-out-of-range:" + uid);
        SI.Interop.InteropReference fabricated;
        try
        {
            fabricated = (refKind ?? "person") switch
            {
                "person" => new FM.UI.PersonReference((int)uid),
                "club" => new FM.UI.ClubReference((int)uid),
                "nation" => new FM.UI.NationReference((int)uid),
                "competition" => new FM.UI.CompReference((int)uid),
                _ => null,
            };
        }
        catch (Exception e) { return (null, null, "fabricate-ctor-failed: " + e.Message); }
        if (fabricated == null) return (null, null, "unknown-ref-kind:" + (refKind ?? ""));

        SI.Core.TypedValue wrapped;
        try
        {
            wrapped = TypedValue.Create<Il2CppSystem.Object>(fabricated.Cast<Il2CppSystem.Object>());
            if (wrapped == null) return (null, null, "typedvalue-create-returned-null");
        }
        catch (Exception e) { return (null, null, "typedvalue-create-failed: " + e.Message); }

        try
        {
            var path = "__bridge.nav." + ++_navCounter;
            var key = NativeBindings.CreatePath(bindings, path, Bindings.NodeFlags.Default);
            bindings.Set(ref key, wrapped,
                Bindings.SetFlags.UpdateHandler | Bindings.SetFlags.ForceUpdateValue);
            return (path, wrapped, null);
        }
        catch (Exception e) { return (null, null, "plant-failed: " + e.Message); }
    }

    private static void EnsureCallback()
    {
        if (_cb != null) return;
        _cb = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Panel.TransitionCompleteCallback>(
            (Action<Panel>)(p => { }));
    }

    public static void Tick()
    {
        if (_verifyPid != null) { VerifyTick(); return; }
        if (_pending == null || _pendingName == null) return;
        var name = _pendingName;
        try
        {
            var handle = _pending;
            var status = handle.Status.ToString();
            if (status == "None") return;
            if (status == "Succeeded")
            {
                var pid = handle.Result;
                var pm = Pm();
                if (pm == null) { Emit(false, name, "panelmanager-null"); }
                else
                {
                    int regBefore = 0;
                    try { foreach (var _x in pm.Panels) { regBefore++; break; } } catch { regBefore = -1; }
                    var via = "";
                    if (_pendingArgs != null && _pendingArgs.Count > 0)
                    {
                        try
                        {
                            var builder = pm.Build(pid, default(Bindings.Key), _cb, "");
                            foreach (var (argName, argValue) in _pendingArgs)
                                builder = builder.Arg(argName, argValue);
                            builder.Open();
                            via = "build-args:" + _pendingArgs.Count;
                        }
                        catch (Exception ea) { via = "build-args-err:" + ea.Message; }
                    }
                    else if (!string.IsNullOrEmpty(_pendingParamName))
                    {
                        try
                        {
                            pm.Open(pid, _pendingParamName, _pendingParamValue, _cb, "");
                            via = "open-param:" + _pendingParamName;
                        }
                        catch (Exception ep) { via = "open-param-err:" + ep.Message; }
                    }
                    else
                    {
                        try
                        {
                            pm.Show(pid, _cb);
                            via = "show";
                        }
                        catch (Exception e2) { via = "show-err:" + e2.Message; }
                    }
                    // transition is async: verify over several ticks instead of immediately
                    _verifyPid = pid;
                    _verifyName = name;
                    _verifyVia = via;
                    _verifyTicks = 0;
                    _verifyReg0 = regBefore;
                    _verifyParamName = _pendingParamName;
                    _verifyParamValue = _pendingParamValue;
                    _verifyArgs = _pendingArgs;
                    _pending = null;
                    _pendingName = null;
                    _pendingParamName = null;
                    _pendingParamValue = null;
                    _pendingArgs = null;
                    return;
                }
            }
            else
            {
                string opex = "";
                try
                {
                    var oe = handle.OperationException;
                    if (oe != null) opex = oe.GetType().Name + ":" + oe.Message;
                }
                catch { }
                Emit(false, name, "load-" + status + (opex.Length > 0 ? "|" + opex : ""));
            }
        }
        catch (Exception e)
        {
            Emit(false, name, "tick-err:" + e.Message);
        }
        finally
        {
        }
    }

    private static PanelID _verifyPid;
    private static string _verifyName;
    private static string _verifyVia;
    private static int _verifyTicks;
    private static int _verifyReg0;
    private static string _verifyParamName;
    private static SI.Core.TypedValue _verifyParamValue;
    private static List<(string name, SI.Core.TypedValue value)> _verifyArgs;
    private static bool _done;

    private static void VerifyTick()
    {
        if (_verifyPid == null) return;
        _verifyTicks++;
        var pm = Pm();
        bool open = false;
        try { open = pm.IsOpen(_verifyPid); } catch { }
        var hasParam = !string.IsNullOrEmpty(_verifyParamName);
        var hasArgs = _verifyArgs != null && _verifyArgs.Count > 0;
        if (!open && _verifyTicks == 6)
        {
            try
            {
                if (hasArgs)
                {
                    var builder = pm.Build(_verifyPid, default(Bindings.Key), _cb, "");
                    foreach (var (argName, argValue) in _verifyArgs)
                        builder = builder.Arg(argName, argValue);
                    builder.Open();
                    _verifyVia += "+rebuild-args";
                    _log?.LogInfo("[Nav] fallback Build+Arg+Open rebuild issued");
                }
                else if (hasParam)
                {
                    pm.Open(_verifyPid, _verifyParamName, _verifyParamValue, _cb, "");
                    _verifyVia += "+reopen-param";
                    _log?.LogInfo("[Nav] fallback Open(pid,param,cb,'') issued");
                }
                else
                {
                    pm.Open(_verifyPid, _cb, "");
                    _verifyVia += "+open('')";
                    _log?.LogInfo("[Nav] fallback Open(pid,cb,'') issued");
                }
            }
            catch (Exception e2) { _verifyVia += "+open-err:" + e2.Message; }
        }
        if (!open && _verifyTicks == 16 && !hasParam && !hasArgs)
        {
            try
            {
                pm.Open(_verifyPid, (string)null, (TypedValue)null, _cb, "");
                _verifyVia += "+open(null-param)";
                _log?.LogInfo("[Nav] fallback Open(pid,null,param,cb,'') issued");
            }
            catch (Exception e2) { _verifyVia += "+openp-err:" + e2.Message; }
        }
        try { open = pm.IsOpen(_verifyPid); } catch { }
        int regNow = 0;
        try { foreach (var _x in pm.Panels) { regNow++; break; } } catch { regNow = -1; }
        if (open || _verifyTicks > 40)
        {
            _log?.LogInfo($"[Nav] verify panel={_verifyName} ticks={_verifyTicks} open={open} reg {_verifyReg0}->{regNow} via={_verifyVia}");
            Emit(open, _verifyName, _verifyVia + $"|ticks={_verifyTicks}|reg={regNow}");
            _verifyPid = null;
            _verifyParamName = null;
            _verifyParamValue = null;
            _verifyArgs = null;
        }
    }

    public static JsonObject CloseTop()
    {
        var pm = Pm();
        if (pm == null) return new JsonObject { ["ok"] = false, ["reason"] = "panelmanager-null" };
        bool hadFullscreen = false;
        try { hadFullscreen = pm.HasFullscreenPanelOpen; } catch { hadFullscreen = false; }
        try
        {
            pm.CloseHighestPanel();
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        _log?.LogInfo($"[Nav] close_top hadFullscreen={hadFullscreen}");
        return new JsonObject { ["ok"] = true, ["hadFullscreen"] = hadFullscreen };
    }

    private static PanelHistoryManagerModule HistoryModule()
    {
        try
        {
            return SI.Core.ManualSingleton<PanelHistoryManagerModule>.Instance;
        }
        catch { return null; }
    }

    /// <summary>Dumps the game's panel navigation back/forward history via the
    /// engine's own debug stringifier. String call only — do not enumerate the
    /// history node list or touch Arguments via interop (past attempts crashed).</summary>
    public static JsonObject NavHistory()
    {
        PanelHistoryManagerModule mod;
        try
        {
            mod = HistoryModule();
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        if (mod == null) return new JsonObject { ["ok"] = false, ["reason"] = "history-module-null" };

        PanelHistoryManager mgr;
        try
        {
            mgr = mod.PanelHistoryManager;
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        if (mgr == null) return new JsonObject { ["ok"] = false, ["reason"] = "history-manager-null" };

        try
        {
            var s = mgr.GetPanelHistoryString();
            _log?.LogInfo($"[Nav] nav_history ok len={s?.Length ?? 0}");
            return new JsonObject { ["ok"] = true, ["history"] = s };
        }
        catch (Exception e)
        {
            _log?.LogInfo($"[Nav] nav_history err={e.Message}");
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private static void Emit(bool ok, string panel, string via)
    {
        _lastOk = ok;
        _lastVia = via;
        try
        {
            _log?.LogInfo($"[Nav] emit panel={panel} ok={ok} via={via}");
            _broadcast?.Invoke(new JsonObject { ["ok"] = ok, ["panel"] = panel, ["via"] = via });
        }
        catch { }
    }
}
