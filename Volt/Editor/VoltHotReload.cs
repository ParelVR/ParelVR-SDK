using System;
using ParelVR.Volt.Simulator;
using UnityEditor;
using UnityEngine;

namespace ParelVR.Volt.EditorTools
{
    /// <summary>
    /// Hot reload: while the simulator runs, saving a world script compiles it with voltc and swaps the new module
    /// into the running world; fields that still exist keep their values. Unity's own domain reload is held off
    /// until play mode ends, so the scene and the simulator survive the edit.
    /// </summary>
    [InitializeOnLoad]
    internal static class VoltHotReload
    {
        private static bool _locked, _pending;

        static VoltHotReload()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        internal static bool Active => _locked && EditorApplication.isPlaying && VoltSimulator.IsRunning;

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode && VoltSettings.instance.hotReload && !_locked)
            {
                EditorApplication.LockReloadAssemblies();
                _locked = true;
            }
            else if ((change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode) && _locked)
            {
                _locked = false;
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        internal static void Schedule()
        {
            if (_pending) return;
            _pending = true;
            EditorApplication.delayCall += () => { _pending = false; Reload(); };
        }

        [MenuItem("ParelVR SDK/Volt/Hot Reload %&r", priority = 22)]
        private static void Reload()
        {
            if (!EditorApplication.isPlaying || !VoltSimulator.IsRunning)
            {
                Debug.Log("[Volt] Hot reload works while the world runs in play mode.");
                return;
            }
            VoltCompileOutput output = VoltCompilerService.Compile();
            if (!output.success || output.bytes == null || output.bytes.Length == 0)
            {
                Debug.LogError("[Volt] Hot reload: the scripts have errors, so the world keeps running the previous build (ParelVR SDK > Volt > Console).");
                return;
            }
            try
            {
                int kept = VoltSimulator.Reload(output.bytes);
                Debug.Log("[Volt] Hot reloaded the scripts; " + kept + " field values kept. OnStart runs again. New scripts on objects need play mode to restart.");
            }
            catch (Exception e)
            {
                Debug.LogError("[Volt] Hot reload failed, stop and start play mode: " + e.Message);
            }
        }
    }

    internal sealed class VoltHotReloadWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!VoltHotReload.Active) return;
            foreach (string path in imported)
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                VoltHotReload.Schedule();
                return;
            }
        }
    }
}
