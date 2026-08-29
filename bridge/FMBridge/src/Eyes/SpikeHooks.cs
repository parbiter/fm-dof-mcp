using System;
using BepInEx.Logging;
using HarmonyLib;
using SI.Bindable;

namespace FMBridge.Eyes;

internal static class SpikeHooks
{
    private static ManualLogSource _log;

    public static BindingSubsystem CapturedBindings { get; private set; }
    public static FM.UI.ContinueManagerModule CapturedContinueManager { get; private set; }
    public static int Initialised { get; private set; }

    public static void Apply(ManualLogSource log)
    {
        _log = log;
        var harmony = new Harmony("dev.fmdofmcp.bridge.eyes");

        TryPatch(harmony, "BindingSubsystem.Initialise",
            () => harmony.Patch(
                AccessTools.Method(typeof(BindingSubsystem), "Initialise"),
                postfix: new HarmonyMethod(typeof(SpikeHooks), nameof(BindingInitialisePost))));

        TryPatch(harmony, "ContinueManagerModule.Activate",
            () => harmony.Patch(
                AccessTools.Method(typeof(FM.UI.ContinueManagerModule), "Activate"),
                postfix: new HarmonyMethod(typeof(SpikeHooks), nameof(ContinueActivatePost))));
    }

    private static void TryPatch(Harmony harmony, string name, Action patch)
    {
        try
        {
            patch();
            _log.LogInfo($"[Eyes] hook applied: {name}");
        }
        catch (Exception e)
        {
            _log.LogWarning($"[Eyes] hook FAILED: {name} — {e.GetType().Name}: {e.Message}");
        }
    }

    private static void BindingInitialisePost(BindingSubsystem __instance)
    {
        Initialised++;
        if (CapturedBindings == null) CapturedBindings = __instance;
        _log.LogInfo("[Eyes] SPIKE: BindingSubsystem.Initialise fired — instance captured");
    }

    public static event Action CareerActivated;

    private static void ContinueActivatePost(FM.UI.ContinueManagerModule __instance)
    {
        if (CapturedContinueManager != __instance)
        {
            CapturedContinueManager = __instance;
            _log.LogInfo("[Hands] ContinueManagerModule captured");
        }
        try { CareerActivated?.Invoke(); }
        catch (Exception e) { _log.LogWarning($"[Hands] CareerActivated handler error: {e.Message}"); }
    }
}
