using System;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// SDK 1.20+: the quality settings the creator's project used when the world was uploaded (the active quality
    /// level). They aren't part of a pipeline asset or a scene, yet they shape the look: how far away models switch to
    /// their simpler LODs, how sharp textures stay at an angle, whether realtime reflection probes update, how baked and
    /// realtime shadows mix (shadowmask mode), terrain detail and tree distances, texture streaming... While the world is
    /// the player's, the game runs on these instead of its own (<see cref="Hold"/>), and puts its own back after.
    /// </summary>
    [Serializable]
    public sealed class ParelWorldQuality
    {
        public string LevelName = string.Empty;

        public float LodBias = 1f;
        public int MaximumLodLevel;
        public AnisotropicFiltering Anisotropic = AnisotropicFiltering.Enable;
        public int TextureMipLimit;
        public bool RealtimeReflectionProbes = true;
        public bool BillboardsFaceCamera = true;
        public SkinWeights SkinWeights = SkinWeights.FourBones;
        public bool SoftParticles;
        public int ParticleRaycastBudget = 256;

        public ShadowmaskMode ShadowmaskMode = ShadowmaskMode.DistanceShadowmask;
        // Built-in only (URP / HDRP take these from their pipeline asset); kept so a Built-in world can carry them too.
        public ShadowQuality Shadows = ShadowQuality.All;
        public ShadowResolution ShadowResolution = ShadowResolution.Medium;
        public ShadowProjection ShadowProjection = ShadowProjection.StableFit;
        public float ShadowDistance = 40f;
        public int ShadowCascades = 2;
        public float ShadowCascade2Split = 1f / 3f;
        public Vector3 ShadowCascade4Split = new Vector3(0.067f, 0.2f, 0.467f);
        public float ShadowNearPlaneOffset = 3f;
        public int PixelLights = 4;
        /// <summary>Unity 6.2+ mesh LOD (0: not captured).</summary>
        public float MeshLodThreshold;

        public bool StreamingMipmaps;
        public float StreamingMipmapsMemoryBudget = 512f;
        public int StreamingMipmapsMaxLevelReduction = 2;
        public bool StreamingMipmapsAddAllCameras = true;

        public TerrainQualityOverrides TerrainOverrides;
        public float TerrainPixelError = 1f, TerrainBasemapDistance = 1000f, TerrainDetailDensityScale = 1f, TerrainDetailDistance = 80f;
        public float TerrainTreeDistance = 5000f, TerrainBillboardStart = 50f, TerrainFadeLength = 5f;
        public float TerrainMaxTrees = 50f;

        /// <summary>The settings Unity is running on now.</summary>
        public static ParelWorldQuality Capture() => new ParelWorldQuality
        {
            LevelName = QualitySettings.names.Length > 0 ? QualitySettings.names[Mathf.Clamp(QualitySettings.GetQualityLevel(), 0, QualitySettings.names.Length - 1)] : string.Empty,
            LodBias = QualitySettings.lodBias,
            MaximumLodLevel = QualitySettings.maximumLODLevel,
            Anisotropic = QualitySettings.anisotropicFiltering,
            TextureMipLimit = QualitySettings.globalTextureMipmapLimit,
            RealtimeReflectionProbes = QualitySettings.realtimeReflectionProbes,
            BillboardsFaceCamera = QualitySettings.billboardsFaceCameraPosition,
            SkinWeights = QualitySettings.skinWeights,
            SoftParticles = QualitySettings.softParticles,
            ParticleRaycastBudget = QualitySettings.particleRaycastBudget,
            ShadowmaskMode = QualitySettings.shadowmaskMode,
            Shadows = QualitySettings.shadows,
            ShadowResolution = QualitySettings.shadowResolution,
            ShadowProjection = QualitySettings.shadowProjection,
            ShadowDistance = QualitySettings.shadowDistance,
            ShadowCascades = QualitySettings.shadowCascades,
            ShadowCascade2Split = QualitySettings.shadowCascade2Split,
            ShadowCascade4Split = QualitySettings.shadowCascade4Split,
            ShadowNearPlaneOffset = QualitySettings.shadowNearPlaneOffset,
            PixelLights = QualitySettings.pixelLightCount,
#if UNITY_6000_2_OR_NEWER
            MeshLodThreshold = QualitySettings.meshLodThreshold,
#endif
            StreamingMipmaps = QualitySettings.streamingMipmapsActive,
            StreamingMipmapsMemoryBudget = QualitySettings.streamingMipmapsMemoryBudget,
            StreamingMipmapsMaxLevelReduction = QualitySettings.streamingMipmapsMaxLevelReduction,
            StreamingMipmapsAddAllCameras = QualitySettings.streamingMipmapsAddAllCameras,
            TerrainOverrides = QualitySettings.terrainQualityOverrides,
            TerrainPixelError = QualitySettings.terrainPixelError,
            TerrainBasemapDistance = QualitySettings.terrainBasemapDistance,
            TerrainDetailDensityScale = QualitySettings.terrainDetailDensityScale,
            TerrainDetailDistance = QualitySettings.terrainDetailDistance,
            TerrainTreeDistance = QualitySettings.terrainTreeDistance,
            TerrainBillboardStart = QualitySettings.terrainBillboardStart,
            TerrainFadeLength = QualitySettings.terrainFadeLength,
            TerrainMaxTrees = QualitySettings.terrainMaxTrees,
        };

        /// <summary>Puts these settings on Unity.</summary>
        public void Apply()
        {
            if (!Mathf.Approximately(QualitySettings.lodBias, LodBias)) QualitySettings.lodBias = LodBias;
            if (QualitySettings.maximumLODLevel != MaximumLodLevel) QualitySettings.maximumLODLevel = MaximumLodLevel;
            if (QualitySettings.anisotropicFiltering != Anisotropic) QualitySettings.anisotropicFiltering = Anisotropic;
            // Changing it re-uploads every texture: only when it really differs.
            if (QualitySettings.globalTextureMipmapLimit != TextureMipLimit) QualitySettings.globalTextureMipmapLimit = TextureMipLimit;
            if (QualitySettings.realtimeReflectionProbes != RealtimeReflectionProbes) QualitySettings.realtimeReflectionProbes = RealtimeReflectionProbes;
            if (QualitySettings.billboardsFaceCameraPosition != BillboardsFaceCamera) QualitySettings.billboardsFaceCameraPosition = BillboardsFaceCamera;
            if (QualitySettings.skinWeights != SkinWeights) QualitySettings.skinWeights = SkinWeights;
            if (QualitySettings.softParticles != SoftParticles) QualitySettings.softParticles = SoftParticles;
            if (QualitySettings.particleRaycastBudget != ParticleRaycastBudget) QualitySettings.particleRaycastBudget = ParticleRaycastBudget;
            if (QualitySettings.shadowmaskMode != ShadowmaskMode) QualitySettings.shadowmaskMode = ShadowmaskMode;
            if (QualitySettings.shadows != Shadows) QualitySettings.shadows = Shadows;
            if (QualitySettings.shadowResolution != ShadowResolution) QualitySettings.shadowResolution = ShadowResolution;
            if (QualitySettings.shadowProjection != ShadowProjection) QualitySettings.shadowProjection = ShadowProjection;
            if (!Mathf.Approximately(QualitySettings.shadowDistance, ShadowDistance)) QualitySettings.shadowDistance = ShadowDistance;
            if (QualitySettings.shadowCascades != ShadowCascades) QualitySettings.shadowCascades = ShadowCascades;
            if (!Mathf.Approximately(QualitySettings.shadowCascade2Split, ShadowCascade2Split)) QualitySettings.shadowCascade2Split = ShadowCascade2Split;
            if (QualitySettings.shadowCascade4Split != ShadowCascade4Split) QualitySettings.shadowCascade4Split = ShadowCascade4Split;
            if (!Mathf.Approximately(QualitySettings.shadowNearPlaneOffset, ShadowNearPlaneOffset)) QualitySettings.shadowNearPlaneOffset = ShadowNearPlaneOffset;
            if (QualitySettings.pixelLightCount != PixelLights) QualitySettings.pixelLightCount = PixelLights;
#if UNITY_6000_2_OR_NEWER
            if (MeshLodThreshold > 0f && !Mathf.Approximately(QualitySettings.meshLodThreshold, MeshLodThreshold)) QualitySettings.meshLodThreshold = MeshLodThreshold;
#endif
            if (QualitySettings.streamingMipmapsActive != StreamingMipmaps) QualitySettings.streamingMipmapsActive = StreamingMipmaps;
            if (!Mathf.Approximately(QualitySettings.streamingMipmapsMemoryBudget, StreamingMipmapsMemoryBudget)) QualitySettings.streamingMipmapsMemoryBudget = StreamingMipmapsMemoryBudget;
            if (QualitySettings.streamingMipmapsMaxLevelReduction != StreamingMipmapsMaxLevelReduction) QualitySettings.streamingMipmapsMaxLevelReduction = StreamingMipmapsMaxLevelReduction;
            if (QualitySettings.streamingMipmapsAddAllCameras != StreamingMipmapsAddAllCameras) QualitySettings.streamingMipmapsAddAllCameras = StreamingMipmapsAddAllCameras;
            if (QualitySettings.terrainQualityOverrides != TerrainOverrides) QualitySettings.terrainQualityOverrides = TerrainOverrides;
            if (!Mathf.Approximately(QualitySettings.terrainPixelError, TerrainPixelError)) QualitySettings.terrainPixelError = TerrainPixelError;
            if (!Mathf.Approximately(QualitySettings.terrainBasemapDistance, TerrainBasemapDistance)) QualitySettings.terrainBasemapDistance = TerrainBasemapDistance;
            if (!Mathf.Approximately(QualitySettings.terrainDetailDensityScale, TerrainDetailDensityScale)) QualitySettings.terrainDetailDensityScale = TerrainDetailDensityScale;
            if (!Mathf.Approximately(QualitySettings.terrainDetailDistance, TerrainDetailDistance)) QualitySettings.terrainDetailDistance = TerrainDetailDistance;
            if (!Mathf.Approximately(QualitySettings.terrainTreeDistance, TerrainTreeDistance)) QualitySettings.terrainTreeDistance = TerrainTreeDistance;
            if (!Mathf.Approximately(QualitySettings.terrainBillboardStart, TerrainBillboardStart)) QualitySettings.terrainBillboardStart = TerrainBillboardStart;
            if (!Mathf.Approximately(QualitySettings.terrainFadeLength, TerrainFadeLength)) QualitySettings.terrainFadeLength = TerrainFadeLength;
            if (!Mathf.Approximately(QualitySettings.terrainMaxTrees, TerrainMaxTrees)) QualitySettings.terrainMaxTrees = TerrainMaxTrees;
        }

        /// <summary>
        /// While held: puts back whatever something else changed since (the game's settings modules re-apply the player's
        /// texture quality, mesh LOD and so on after every scene load). Only differing values are written.
        /// </summary>
        public static void KeepHeld() => _held?.Apply();

        public override string ToString() =>
            $"'{LevelName}': LOD bias {LodBias:0.##} (max level {MaximumLodLevel}), aniso {Anisotropic}, texture limit {TextureMipLimit}, realtime probes {RealtimeReflectionProbes}, " +
            $"shadowmask {ShadowmaskMode}, skin {SkinWeights}, streaming {StreamingMipmaps}, terrain overrides {TerrainOverrides}";

        // ---- the hold -------------------------------------------------------------------------------------------

        private static ParelWorldQuality _held, _before;
        private static bool _hooked;

        /// <summary>The world's settings the game runs on now, or null. While set, the game's own quality code
        /// (graphics settings, adaptive rendering) leaves Unity's quality settings alone.</summary>
        public static ParelWorldQuality Held => _held;

        /// <summary>Raised when the world's settings were let go: the game's own quality settings should go back on.</summary>
        public static event Action Released;

        /// <summary>Runs on <paramref name="world"/>'s settings until <see cref="Let"/>.</summary>
        public static void Hold(ParelWorldQuality world)
        {
            if (world == null) { Let(); return; }
            if (_held == world) return;
            if (_held == null) _before = Capture();
            _held = world;
            if (!_hooked)
            {
                _hooked = true;
                // A quality level change loads that level's values over the world's: put the world's back.
                QualitySettings.activeQualityLevelChanged += (from, to) => _held?.Apply();
            }
            world.Apply();
            Debug.Log("[ParelVR Quality] Running on the world's own quality settings " + world + ".");
        }

        /// <summary>The game's own settings go back.</summary>
        public static void Let()
        {
            if (_held == null) return;
            _held = null;
            _before?.Apply();
            _before = null;
            Debug.Log("[ParelVR Quality] Back on the game's quality settings.");
            try { Released?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
