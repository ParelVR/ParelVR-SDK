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
        private static int _assetChanges;

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
        public static RenderPipelineKind PrepareForScene(string scenePath)
        {
            // A new world: the last world's own pipeline asset goes with it (AdoptWorld picks the new one's).
            _worldAsset = null;
            return Use(PipelineOfScene(scenePath));
        }

        /// <summary>
        /// The pipeline <paramref name="made"/> actually runs on here: HDRP falls back to Built-in where HDRP can't run,
        /// and to Built-in when the game has no asset for it.
        /// </summary>
        public static RenderPipelineKind Runnable(RenderPipelineKind made)
        {
            if (made != RenderPipelineKind.Universal && made != RenderPipelineKind.HighDefinition) return RenderPipelineKind.BuiltIn;
            if (made == RenderPipelineKind.HighDefinition && (Application.isMobilePlatform || !SystemInfo.supportsComputeShaders)) return RenderPipelineKind.BuiltIn;
            return AssetFor(made) != null ? made : RenderPipelineKind.BuiltIn;
        }

        /// <summary>The pipeline a loaded world scene runs on here.</summary>
        public static RenderPipelineKind RunnableFor(UnityEngine.SceneManagement.Scene scene) => Runnable(PipelineOfScene(scene.path));

        /// <summary>
        /// Runs an already loaded world (one loaded beside the old world for a portal, walked into) on its pipeline, with
        /// its own pipeline asset when it carries one, in one switch.
        /// </summary>
        public static RenderPipelineKind EnterWorld(UnityEngine.SceneManagement.Scene scene)
        {
            RenderPipelineKind made = PipelineOfScene(scene.path);
            ParelWorldPipeline info = ParelWorldPipeline.Find(scene);
            _worldAsset = info != null && info.Asset != null && KindOf(info.Asset) == Runnable(made) ? info.Asset : null;
            if (_worldAsset != null) Debug.Log("[ParelVR Pipeline] Running the world on its own " + KindOf(_worldAsset) + " asset '" + _worldAsset.name + "'.");
            return Use(made);
        }

        public static RenderPipelineAsset AssetFor(RenderPipelineKind p)
        {
            switch (p)
            {
                case RenderPipelineKind.Universal: return _urp != null ? _urp : (_urp = Resources.Load<RenderPipelineAsset>(UrpResource));
                case RenderPipelineKind.HighDefinition: return _hdrp != null ? _hdrp : (_hdrp = Resources.Load<RenderPipelineAsset>(HdrpResource));
                default: return null;
            }
        }

        /// <summary>
        /// Call once the world scene has loaded. A world uploaded with SDK 1.18+ carries the creator's own pipeline asset
        /// (<see cref="ParelWorldPipeline"/>); the world then runs on it (their renderer features, volumetrics, quality
        /// settings) instead of the game's generic one. Older worlds keep the game's asset.
        /// </summary>
        public static RenderPipelineKind AdoptWorld(UnityEngine.SceneManagement.Scene scene)
        {
            ParelWorldPipeline info = ParelWorldPipeline.Find(scene);
            if (info == null || info.Asset == null || _wanted == RenderPipelineKind.BuiltIn)
            {
                // No asset of its own: the game's, never the one of a world that ran before.
                if (_worldAsset != null && _wanted != RenderPipelineKind.BuiltIn) { _worldAsset = null; return Use(_wanted); }
                return Current;
            }
            RenderPipelineKind kind = KindOf(info.Asset);
            if (kind != _wanted)
            {
                Debug.LogWarning("[ParelVR Pipeline] The world's own pipeline asset is " + kind + " but it runs on " + _wanted + "; keeping the game's asset.");
                return Current;
            }
            _worldAsset = info.Asset;
            Debug.Log("[ParelVR Pipeline] Running the world on its own " + kind + " asset '" + info.Asset.name + "'" +
                      (string.IsNullOrEmpty(info.PipelineVersion) ? "" : " (made with " + info.PipelineVersion + ")") + ".");
            return Use(_wanted);
        }

        private static RenderPipelineAsset _worldAsset;

        private static RenderPipelineKind KindOf(RenderPipelineAsset a) =>
            a == null ? RenderPipelineKind.BuiltIn :
            a.GetType().Name.IndexOf("HDRenderPipeline", StringComparison.Ordinal) >= 0 ? RenderPipelineKind.HighDefinition : RenderPipelineKind.Universal;

        /// <summary>Switches the running pipeline. Returns the pipeline in use afterwards.</summary>
        public static RenderPipelineKind Use(RenderPipelineKind made)
        {
            RenderPipelineKind p = made == RenderPipelineKind.Universal || made == RenderPipelineKind.HighDefinition ? made : RenderPipelineKind.BuiltIn;
            if (p == RenderPipelineKind.HighDefinition && (Application.isMobilePlatform || !SystemInfo.supportsComputeShaders))
            {
                Debug.LogWarning("[ParelVR Pipeline] This device can't run HDRP; the world stays on Built-in.");
                p = RenderPipelineKind.BuiltIn;
            }
            // The world's own asset while it is loaded (AdoptWorld); a new pipeline choice drops it.
            if (_worldAsset != null && KindOf(_worldAsset) != p) _worldAsset = null;
            RenderPipelineAsset asset = _worldAsset != null ? _worldAsset : AssetFor(p);
            if (p != RenderPipelineKind.BuiltIn && asset == null)
            {
                Debug.LogError("[ParelVR Pipeline] The game has no " + p + " pipeline asset (Resources/" + (p == RenderPipelineKind.Universal ? UrpResource : HdrpResource) + "); the world stays on Built-in.");
                p = RenderPipelineKind.BuiltIn;
            }
            _wanted = p;
            if (!_hooked)
            {
                _hooked = true;
                // Diagnostics: every time Unity actually tears down / rebuilds the running pipeline, with who caused it.
                // A rebuild in the editor also rebuilds every VFX graph and unloads unused assets; it should happen
                // once per world switch, never per frame.
                RenderPipelineManager.activeRenderPipelineAssetChanged += (a, b) =>
                {
                    _assetChanges++;
                    if (_assetChanges <= 12 || _assetChanges % 50 == 0)
                        Debug.LogWarning("[ParelVR Pipeline] Pipeline asset changed #" + _assetChanges + ": " + (a ? a.name : "Built-in") + " -> " + (b ? b.name : "Built-in") +
                                         " (frame " + Time.frameCount + ", default " + (GraphicsSettings.defaultRenderPipeline ? GraphicsSettings.defaultRenderPipeline.name : "none") +
                                         ", quality " + (QualitySettings.renderPipeline ? QualitySettings.renderPipeline.name : "none") + " lvl " + QualitySettings.GetQualityLevel() + ")\n" + Environment.StackTrace);
                };
                // Each quality level has its own pipeline setting: a graphics-settings change mustn't drop the world's pipeline.
                QualitySettings.activeQualityLevelChanged += (from, to) =>
                {
                    RenderPipelineAsset want = _worldAsset != null ? _worldAsset : AssetFor(_wanted);
                    if (GraphicsSettings.currentRenderPipeline != want) Use(_wanted);
                };
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
