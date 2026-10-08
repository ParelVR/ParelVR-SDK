using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ParelVR.SDK.Core.ControlPanel;

namespace ParelVR.SDK.Core.Settings
{
    /// <summary>
    /// Single source of truth for the active ParelProjectType. On domain load and on every
    /// SetMode call, runs every discovered IParelModeAware through OnModeDeactivated/OnModeActivated.
    /// </summary>
    [InitializeOnLoad]
    public static class ParelModeManager
    {
        public static ParelProjectType Current { get; private set; }

        public static event Action OnModeChanged;

        static ParelModeManager()
        {
            Current = ParelProjectSettings.ProjectType;
            ActivateAll(Current);
        }

        public static void SetMode(ParelProjectType newMode)
        {
            if (newMode == Current) return;

            ParelProjectType oldMode = Current;
            DeactivateAll(oldMode);

            ParelProjectSettings.Save(newMode);
            Current = newMode;

            ActivateAll(newMode);

            OnModeChanged?.Invoke();
        }

        private static void ActivateAll(ParelProjectType mode)
        {
            foreach (var handler in DiscoverHandlers())
            {
                try
                {
                    handler.OnModeActivated(mode);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ParelVR SDK] {handler.GetType().FullName}.OnModeActivated({mode}) threw: {ex.Message}");
                }
            }
        }

        private static void DeactivateAll(ParelProjectType mode)
        {
            foreach (var handler in DiscoverHandlers())
            {
                try
                {
                    handler.OnModeDeactivated(mode);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ParelVR SDK] {handler.GetType().FullName}.OnModeDeactivated({mode}) threw: {ex.Message}");
                }
            }
        }

        private static List<IParelModeAware> DiscoverHandlers()
        {
            var handlers = new List<IParelModeAware>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IParelModeAware>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                try
                {
                    handlers.Add((IParelModeAware)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ParelVR SDK] Failed to instantiate mode handler '{type.FullName}': {ex.Message}");
                }
            }
            return handlers;
        }
    }
}
