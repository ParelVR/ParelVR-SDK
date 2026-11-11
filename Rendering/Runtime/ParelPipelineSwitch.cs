using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Runs every world on the render pipeline it was made with. Built-in worlds run on Built-in; URP and HDRP worlds
    /// uploaded with SDK 1.17+ keep their own materials, shaders, lights and volumes, and the game switches to its URP
    /// or HDRP pipeline before their scene loads. Standalone headsets can't run HDRP; an HDRP world is converted for
    /// them at upload as before, so it loads on Built-in there.
    /// The SDK puts a world's pipeline into its scene path (<see cref="UrpFolder"/>, <see cref="HdrpFolder"/>), so the
    /// game knows it from the bundle before loading anything.
    /// </summary>
    public static class ParelPipelineSwitch
    {
        /// <summary>Upload folder for the scenes of a world that runs on URP.</summary>
        public const string UrpFolder = "Assets/ParelVR_URP_World";
        /// <summary>Upload folder for the scenes of a world that runs on HDRP.</summary>
        public const string HdrpFolder = "Assets/ParelVR_HDRP_World";

        /// <summary>The game's pipeline assets (game only; the SDK doesn't ship them).</summary>
        public const string UrpResource = "ParelPipelines/ParelURP";
        public const string HdrpResource = "ParelPipelines/ParelHDRP";

        private static RenderPipelineAsset _urp, _hdrp;
        private static RenderPipelineKind _wanted = RenderPipelineKind.BuiltIn;
        private static bool _hooked;

        /// <summary>The pipeline running now.</summary>
        public static RenderPipelineKind Current
        {
            get
            {
                RenderPipelineAsset a = GraphicsSettings.currentRenderPipeline;
                if (a == null) return RenderPipelineKind.BuiltIn;
                return a.GetType().Name.IndexOf("HDRenderPipeline", StringComparison.Ordinal) >= 0 ? RenderPipelineKind.HighDefinition : RenderPipelineKind.Universal;
            }
        }

        /// <summary>Raised after the running pipeline changed (old, new).</summary>
        public static event Action<RenderPipelineKind, RenderPipelineKind> Switched;

        /// <summary>The pipeline a world scene was uploaded for, read from its path in the bundle.</summary>
        public static RenderPipelineKind PipelineOfScene(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return RenderPipelineKind.BuiltIn;
            if (scenePath.IndexOf(UrpFolder + "/", StringComparison.OrdinalIgnoreCase) >= 0) return RenderPipelineKind.Universal;
            if (scenePath.IndexOf(HdrpFolder + "/", StringComparison.OrdinalIgnoreCase) >= 0) return RenderPipelineKind.HighDefinition;
            return RenderPipelineKind.BuiltIn;
        }

        /// <summary>Switches to the pipeline <paramref name="scenePath"/> was made for. Call right before loading it.</summary>
        public static RenderPipelineKind PrepareForScene(string scenePath) => Use(PipelineOfScene(scenePath));

        public static RenderPipelineAsset AssetFor(RenderPipelineKind p)
        {
            switch (p)
            {
                case RenderPipelineKind.Universal: return _urp != null ? _urp : (_urp = Resources.Load<RenderPipelineAsset>(UrpResource));
                case RenderPipelineKind.HighDefinition: return _hdrp != null ? _hdrp : (_hdrp = Resources.Load<RenderPipelineAsset>(HdrpResource));
                default: return null;
            }
        }

        /// <summary>Switches the running pipeline. Returns the pipeline in use afterwards.</summary>
        public static RenderPipelineKind Use(RenderPipelineKind made)
        {
            RenderPipelineKind p = made == RenderPipelineKind.Universal || made == RenderPipelineKind.HighDefinition ? made : RenderPipelineKind.BuiltIn;
            if (p == RenderPipelineKind.HighDefinition && (Application.isMobilePlatform || !SystemInfo.supportsComputeShaders))
            {
                Debug.LogWarning("[ParelVR Pipeline] This device can't run HDRP; the world stays on Built-in.");
                p = RenderPipelineKind.BuiltIn;
            }
            RenderPipelineAsset asset = AssetFor(p);
            if (p != RenderPipelineKind.BuiltIn && asset == null)
            {
                Debug.LogError("[ParelVR Pipeline] The game has no " + p + " pipeline asset (Resources/" + (p == RenderPipelineKind.Universal ? UrpResource : HdrpResource) + "); the world stays on Built-in.");
                p = RenderPipelineKind.BuiltIn;
            }
            _wanted = p;
            if (!_hooked)
            {
                _hooked = true;
                // Each quality level has its own pipeline setting: a graphics-settings change mustn't drop the world's pipeline.
                QualitySettings.activeQualityLevelChanged += (from, to) => { if (Current != _wanted) Use(_wanted); };
            }
            RenderPipelineKind before = Current;
            // HDRP's sky system writes its own temporary material over the active scene's skybox (and forces its ambient
            // and reflection settings), then destroys that material when HDRP shuts down: the scene that was active
            // around the switch (the loading screen, the game's own scenes) would come back with no sky.
            if (p == RenderPipelineKind.HighDefinition && before != RenderPipelineKind.HighDefinition) SceneLook.Save();
            if (GraphicsSettings.defaultRenderPipeline != asset) GraphicsSettings.defaultRenderPipeline = asset;
            if (QualitySettings.renderPipeline != asset) QualitySettings.renderPipeline = asset;
            RenderPipelineKind after = Current;
            if (before == RenderPipelineKind.HighDefinition && after != RenderPipelineKind.HighDefinition) SceneLook.Restore();
            if (after != before)
            {
                Debug.Log("[ParelVR Pipeline] " + before + " -> " + after + (after != made ? " (world made with " + made + ")" : ""));
                try { Switched?.Invoke(before, after); }
                catch (Exception e) { Debug.LogException(e); }
            }
            return after;
        }

        /// <summary>The active scene's sky and ambient settings, kept across an HDRP stretch.</summary>
        private static class SceneLook
        {
            private static bool _saved;
            private static UnityEngine.SceneManagement.Scene _scene;
            private static Material _skybox;
            private static AmbientMode _ambientMode;
            private static float _ambientIntensity, _reflectionIntensity;
            private static Texture _customReflection;

            public static void Save()
            {
                _scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                _skybox = RenderSettings.skybox;
                _ambientMode = RenderSettings.ambientMode;
                _ambientIntensity = RenderSettings.ambientIntensity;
                _reflectionIntensity = RenderSettings.reflectionIntensity;
                _customReflection = RenderSettings.customReflectionTexture;
                _saved = true;
            }

            public static void Restore()
            {
                if (!_saved) return;
                _saved = false;
                // Only the scene the settings came from, if it is still there and active; scenes loaded since have their own.
                if (!_scene.IsValid() || !_scene.isLoaded || UnityEngine.SceneManagement.SceneManager.GetActiveScene() != _scene) return;
                RenderSettings.skybox = _skybox;
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.reflectionIntensity = _reflectionIntensity;
                RenderSettings.customReflectionTexture = _customReflection;
                DynamicGI.UpdateEnvironment();
            }
        }
    }
}
