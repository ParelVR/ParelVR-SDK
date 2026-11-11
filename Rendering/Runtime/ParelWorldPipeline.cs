using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering
{
    /// <summary>
    /// SDK 1.18+: written into a URP / HDRP world's build copy at upload. It carries the creator's own pipeline asset
    /// (for URP with its renderer and renderer features: volumetric light / god rays, SSAO, decals, screen-space shadows,
    /// full-screen passes...) into the world's bundle, and a global volume with the project's default volume profile
    /// (HDRP keeps fog, volumetrics, exposure and sky there). The game runs the world on that asset instead of its own
    /// generic one (<see cref="ParelPipelineSwitch.AdoptWorld"/>), so it looks the way it did in the creator's editor.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class ParelWorldPipeline : MonoBehaviour
    {
        public const string ObjectName = "ParelVR World Pipeline";

        [Tooltip("The pipeline the world was made with.")]
        public RenderPipelineKind Pipeline = RenderPipelineKind.BuiltIn;
        [Tooltip("The creator's pipeline asset (URP: with its renderer and renderer features).")]
        public RenderPipelineAsset Asset;
        [Tooltip("The creator's URP / HDRP version, for diagnostics.")]
        public string PipelineVersion = string.Empty;

        /// <summary>The world's pipeline info, or null for worlds uploaded before SDK 1.18.</summary>
        public static ParelWorldPipeline Find(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                ParelWorldPipeline p = root.GetComponentInChildren<ParelWorldPipeline>(true);
                if (p != null) return p;
            }
            return null;
        }
    }
}
