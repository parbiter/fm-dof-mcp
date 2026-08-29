using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Logging;

namespace FMBridge;

/// <summary>Starts the host-side DoF chat service (scripts/
/// dof_chat_service.mjs) alongside the game, so the in-game chat works
/// without the user starting anything by hand. The game's own process
/// environment has no useful PATH (no node, no claude CLI), so
/// deploy_bridge.sh records where they live in chat_service.env next to
/// the plugin dll; if that file is missing the feature simply stays off
/// and the service can still be run manually. The service itself refuses
/// to run twice (it exits if another instance holds its lock port), so a
/// manually started copy and this one never fight over the chat.</summary>
internal static class ChatServiceLauncher
{
    private static Process _proc;

    public static void Start(ManualLogSource log)
    {
        try
        {
            var dir = Path.Combine(BepInEx.Paths.PluginPath, "FMBridge");
            var envFile = Path.Combine(dir, "chat_service.env");
            if (!File.Exists(envFile))
            {
                log.LogInfo("[Bridge] chat service autostart: chat_service.env not found — run scripts/deploy_bridge.sh, or start scripts/dof_chat_service.mjs by hand");
                return;
            }

            string node = null, script = null, path = null;
            foreach (var line in File.ReadAllLines(envFile))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var key = line.Substring(0, i).Trim();
                var val = line.Substring(i + 1).Trim();
                if (key == "NODE") node = val;
                else if (key == "SCRIPT") script = val;
                else if (key == "PATH") path = val;
            }
            if (node == null || script == null || !File.Exists(node) || !File.Exists(script))
            {
                log.LogWarning($"[Bridge] chat service autostart: stale chat_service.env (node={node ?? "?"}, script={script ?? "?"}) — rerun scripts/deploy_bridge.sh");
                return;
            }

            // Route through /bin/sh purely for the output redirect; exec
            // replaces the shell so the tracked pid is node itself. Paths
            // travel as environment variables to sidestep quoting.
            var logFile = Path.Combine(dir, "chat_service.log");
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec \"$DOF_NODE\" \"$DOF_SCRIPT\" > \"$DOF_LOG\" 2>&1");
            psi.Environment["DOF_NODE"] = node;
            psi.Environment["DOF_SCRIPT"] = script;
            psi.Environment["DOF_LOG"] = logFile;
            // Tells the service its lifecycle is game-managed: exit when the
            // bridge socket closes instead of retrying forever.
            psi.Environment["DOF_CHAT_MANAGED"] = "1";
            if (!string.IsNullOrEmpty(path)) psi.Environment["PATH"] = path;

            _proc = Process.Start(psi);
            log.LogInfo($"[Bridge] chat service started (pid {_proc?.Id}), log: {logFile}");
        }
        catch (Exception e)
        {
            log.LogWarning($"[Bridge] chat service autostart failed: {e.Message}");
        }
    }

    public static void Stop()
    {
        try
        {
            if (_proc != null && !_proc.HasExited)
                _proc.Kill(entireProcessTree: true);
        }
        catch { }
        _proc = null;
    }
}
