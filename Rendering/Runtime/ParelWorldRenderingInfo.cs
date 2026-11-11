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
        [Tooltip("Volumetric fog (from HDRP's Fog and Local Volumetric Fog), drawn by the ParelVR shaders.")]
        public ParelFogSettings Fog = new ParelFogSettings();
        [Tooltip("Volumetric clouds (from HDRP's Volumetric Clouds), drawn into the ParelVR sky, with cloud shadows.")]
        public ParelCloudSettings Clouds = new ParelCloudSettings();
        [Tooltip("Screen-space reflections, GI, contact and micro shadows (from HDRP's volume overrides), drawn by the ParelVR lit shaders.")]
        public ParelScreenEffectsSettings ScreenEffects = new ParelScreenEffectsSettings();
        [Tooltip("Post-processing (from HDRP's volume overrides), drawn by the game's post-processing.")]
        public ParelPostSettings Post = new ParelPostSettings();

        [Header("Creator settings")]
        [Tooltip("Optional objects to switch per device when automatic optimisation isn't enough.")]
        public DeviceOverride[] DeviceOverrides = Array.Empty<DeviceOverride>();

        private bool _applied;
        /// <summary>The world whose fog is showing (one at a time: the last one applied).</summary>
        private static ParelWorldRenderingInfo _fogOwner;

        /// <summary>Raised after a world's info was applied. The game builds the post-processing from <see cref="Post"/> here.</summary>
        public static event Action<ParelWorldRenderingInfo> Applied;

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
            if (sceneIsActive && SourcePipeline == RenderPipelineKind.HighDefinition) RepairAmbient();
            else if (sceneIsActive && AmbientFromSky && RenderSettings.ambientMode == AmbientMode.Skybox) DynamicGI.UpdateEnvironment();

            if (Fog != null && Fog.Enabled)
            {
                if (sceneIsActive) PutSkyInFog(target);
                _fogOwner = this;
                ParelFogGlobals.Apply(Fog, FindSun(), ExposureScale);
                ParelVolumetricFog.Enable(Fog, FindSun);
            }

            if (Clouds != null && Clouds.Enabled) ParelSkyClouds.Enable(Clouds, FindSun);
            if (ScreenEffects != null && ScreenEffects.Any) ParelScreenEffects.Apply(ScreenEffects);

            RenderingLog.Notice("World made with the " + CompatibilityReport.PipelineLabel(SourcePipeline) + ": " + MaterialsConverted + " material(s) converted at upload"
                                + (Mathf.Abs(LightProbeScale - 1f) > 1e-4f ? ", light probes scaled ×" + LightProbeScale.ToString("0.#####") : "")
                                + (Fog != null && Fog.Enabled ? ", volumetric fog on" + (Fog.Volumes != null && Fog.Volumes.Length > 0 ? " with " + Fog.Volumes.Length + " local fog volume(s)" : "") : "")
                                + (Post != null && Post.Any ? ", post-processing from the world" : "") + ".");
            try { Applied?.Invoke(this); }
            catch (Exception e) { Debug.LogException(e); }
            return true;
        }

        /// <summary>The factor HDRP physical light values were multiplied by at upload (1 for other pipelines).</summary>
        public float ExposureScale => SourcePipeline != RenderPipelineKind.HighDefinition ? 1f
            : Mathf.Abs(LightProbeScale - 1f) > 1e-4f ? LightProbeScale : LightingConversion.ExposureScale(SceneEv100);

        // HDRP lights its scene from the sky and never writes Built-in's ambient values, so a converted world can carry
        // an ambient of almost nothing: black shadows and fog with no light in it. Light it from its sky instead.
        private static void RepairAmbient()
        {
            bool black;
            switch (RenderSettings.ambientMode)
            {
                case AmbientMode.Trilight:
                    black = Mathf.Max(RenderSettings.ambientSkyColor.maxColorComponent, RenderSettings.ambientEquatorColor.maxColorComponent,
                                      RenderSettings.ambientGroundColor.maxColorComponent) * RenderSettings.ambientIntensity < 2e-3f;
                    break;
                case AmbientMode.Flat:
                    black = RenderSettings.ambientLight.maxColorComponent * RenderSettings.ambientIntensity < 2e-3f;
                    break;
                default:
                    black = Mathf.Abs(RenderSettings.ambientProbe[0, 0]) + Mathf.Abs(RenderSettings.ambientProbe[1, 0]) + Mathf.Abs(RenderSettings.ambientProbe[2, 0]) < 2e-3f;
                    break;
            }
            if (RenderSettings.ambientMode == AmbientMode.Skybox || (black && RenderSettings.skybox != null))
            {
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = 1f;
                DynamicGI.UpdateEnvironment();
            }
        }

        private Cubemap _skyCube;
        private Material _skyMaterial;

        // Only the ParelVR sky shader draws the fog over the sky. Any other sky (HDRP's physically based and gradient
        // skies arrive as Skybox/Procedural) left the ground fogged and the sky clear, a hard line along the horizon.
        // Capture that sky into a small cubemap once and draw it with ParelVR/Skybox, which the fog covers.
        private void PutSkyInFog(TargetPlatform target)
        {
            Material sky = RenderSettings.skybox;
            if (sky == null || sky.shader == null || sky.shader.name == ParelShaderNames.Skybox || sky.shader.name == ParelShaderNames.Sky) return;
            Shader parelSky = ParelShaderLibrary.Resolve(ShaderRole.Skybox, target, out ShaderRole resolved);
            if (parelSky == null || resolved != ShaderRole.Skybox) return;

            var go = new GameObject("[ParelVR Sky Capture]") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.allowHDR = true;
                _skyCube = new Cubemap(target == TargetPlatform.Quest ? 128 : 256, TextureFormat.RGBAHalf, true) { name = "ParelVR Fog Sky" };
                if (!cam.RenderToCubemap(_skyCube))
                {
                    Destroy(_skyCube);
                    _skyCube = null;
                    return;
                }
            }
            finally { Destroy(go); }

            _skyMaterial = new Material(parelSky) { name = sky.name + " (in fog)" };
            _skyMaterial.SetTexture("_Tex", _skyCube);
            _skyMaterial.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            _skyMaterial.SetFloat("_Exposure", 1f);
            RenderSettings.skybox = _skyMaterial;
        }

        private void OnDestroy()
        {
            if (_skyMaterial != null && RenderSettings.skybox == _skyMaterial) RenderSettings.skybox = null;
            if (_skyMaterial != null) Destroy(_skyMaterial);
            if (_skyCube != null) Destroy(_skyCube);
        }

        private Light _sun;
        private float _nextSunSearch;

        private Light FindSun()
        {
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;
            if (Time.unscaledTime < _nextSunSearch) return _sun;
            _nextSunSearch = Time.unscaledTime + 2f;
            _sun = RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional ? RenderSettings.sun : null;
            if (_sun == null)
                foreach (GameObject root in gameObject.scene.GetRootGameObjects())
                    foreach (Light l in root.GetComponentsInChildren<Light>(false))
                        if (l.type == LightType.Directional && (_sun == null || l.intensity > _sun.intensity)) _sun = l;
            return _sun;
        }

        // The sun and local fog boxes can move: the fog follows them every frame (a few shader globals).
        private void LateUpdate()
        {
            if (_fogOwner == this && Fog != null && Fog.Enabled) ParelFogGlobals.Apply(Fog, FindSun(), ExposureScale);
        }

        private void OnEnable()
        {
            if (!_applied || Fog == null || !Fog.Enabled) return;
            _fogOwner = this;
            ParelFogGlobals.Apply(Fog, FindSun(), ExposureScale);
            ParelVolumetricFog.Enable(Fog, FindSun);
        }

        private void OnDisable()
        {
            if (Clouds != null && Clouds.Enabled && _applied) ParelSkyClouds.Disable();
            if (ScreenEffects != null && ScreenEffects.Any && _applied) ParelScreenEffects.Clear();
            if (_fogOwner != this) return;
            _fogOwner = null;
            ParelFogGlobals.Clear();
            ParelVolumetricFog.Disable();
        }
    }
}
