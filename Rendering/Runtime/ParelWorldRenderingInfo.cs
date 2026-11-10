using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Written into a world's build copy by the SDK when PVRP converted it at upload: where the world came from, and
    /// what still has to be done when it loads (baked data can't be changed at upload without touching the creator's
    /// lighting asset). Also carries the creator's optional per-device overrides. Content without it loads as before.
    /// </summary>
    [AddComponentMenu("ParelVR/Rendering/World Rendering Settings")]
    [DisallowMultipleComponent]
    public sealed class ParelWorldRenderingInfo : MonoBehaviour
    {
        public const int CurrentVersion = 1;

        [Serializable]
        public sealed class DeviceOverride
        {
            [Tooltip("Shown only on this kind of device.")]
            public TargetPlatform Platform = TargetPlatform.Quest;
            [Tooltip("Turned on for that device (e.g. a lighter version of a heavy object).")]
            public GameObject[] Enable = Array.Empty<GameObject>();
            [Tooltip("Turned off for that device.")]
            public GameObject[] Disable = Array.Empty<GameObject>();
        }

        [Header("Written by the ParelVR SDK at upload")]
        public int Version = CurrentVersion;
        public RenderPipelineKind SourcePipeline = RenderPipelineKind.BuiltIn;
        [Tooltip("The exposure (EV100) the creator's HDRP scene used; physical values were converted with it.")]
        public float SceneEv100 = LightingConversion.DefaultEv100;
        [Tooltip("Multiplier for the baked light probes (1 = unchanged). HDRP bakes them in physical units.")]
        public float LightProbeScale = 1f;
        [Tooltip("Recalculate ambient light from the converted sky when the world loads.")]
        public bool AmbientFromSky;
        public int MaterialsConverted;
        public int MaterialsKept;

        [Header("Creator settings")]
        [Tooltip("Optional objects to switch per device when automatic optimisation isn't enough.")]
        public DeviceOverride[] DeviceOverrides = Array.Empty<DeviceOverride>();

        private bool _applied;

        /// <summary>Applies every ParelWorldRenderingInfo in the scene once. Returns how many were applied.</summary>
        public static int ApplyAll(Scene scene, TargetPlatform target)
        {
            if (!scene.IsValid() || !scene.isLoaded) return 0;
            int n = 0;
            var roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);
            foreach (GameObject root in roots)
                foreach (ParelWorldRenderingInfo info in root.GetComponentsInChildren<ParelWorldRenderingInfo>(true))
                    if (info.Apply(target, scene == SceneManager.GetActiveScene())) n++;
            return n;
        }

        /// <summary>Applies the load-time steps once. <paramref name="sceneIsActive"/>: the world's lighting settings are the ones in use.</summary>
        public bool Apply(TargetPlatform target, bool sceneIsActive)
        {
            if (_applied) return false;
            _applied = true;

            foreach (DeviceOverride o in DeviceOverrides ?? Array.Empty<DeviceOverride>())
            {
                if (o == null) continue;
                bool here = o.Platform == target;
                foreach (GameObject g in o.Enable ?? Array.Empty<GameObject>()) if (g != null) g.SetActive(here);
                foreach (GameObject g in o.Disable ?? Array.Empty<GameObject>()) if (g != null) g.SetActive(!here);
            }

            if (sceneIsActive && Mathf.Abs(LightProbeScale - 1f) > 1e-4f && LightmapSettings.lightProbes != null)
            {
                LightProbes probes = LightmapSettings.lightProbes;
                SphericalHarmonicsL2[] sh = probes.bakedProbes;
                for (int i = 0; i < sh.Length; i++) sh[i] *= LightProbeScale;
                probes.bakedProbes = sh;
            }
            if (sceneIsActive && AmbientFromSky && RenderSettings.ambientMode == AmbientMode.Skybox) DynamicGI.UpdateEnvironment();

            RenderingLog.Notice("World made with the " + CompatibilityReport.PipelineLabel(SourcePipeline) + ": " + MaterialsConverted + " material(s) converted at upload"
                                + (Mathf.Abs(LightProbeScale - 1f) > 1e-4f ? ", light probes scaled ×" + LightProbeScale.ToString("0.#####") : "") + ".");
            return true;
        }
    }
}
