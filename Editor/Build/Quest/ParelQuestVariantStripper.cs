using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>
    /// Shader variants for what a standalone headset actually draws, in the background Android build only
    /// (<see cref="Active"/>, set by <see cref="ParelQuestLocalWorker"/>; it does nothing in the creator's own project).
    ///
    /// A world's URP shaders were compiled for every URP feature: each Shader Graph / Lit forward pass is about 25 keyword
    /// groups, and every feature left in (soft-shadow qualities, light layers, cookies, probe blending, box projection,
    /// LOD cross-fade...) doubled the count again. A creator's water Shader Graph and SpeedTree world sat at 70% CPU for
    /// over 20 minutes "compiling shaders" twice (11-21). The variants of features a headset build doesn't use are left
    /// out here. ParelVR doesn't use strict variant matching, so where the game turns one of these on, the headset
    /// draws the nearest variant: hard instead of soft shadows, no light cookies or light layers, one reflection probe
    /// without box projection, LODs that switch instead of fading.
    ///
    /// Built-in lighting passes (ForwardBase, ForwardAdd, Deferred, legacy Vertex) are left out as well: ParelVR runs on
    /// URP everywhere and URP never draws them (the game's own builds strip them the same way).
    /// </summary>
    public sealed class ParelQuestVariantStripper : IPreprocessShaders
    {
        public static bool Active;
        public static long Kept, Removed, RemovedBuiltInPasses;

        public static void Reset() => Kept = Removed = RemovedBuiltInPasses = 0;

        private static readonly HashSet<string> HeadsetOff = new HashSet<string>(StringComparer.Ordinal)
        {
            "_ADDITIONAL_LIGHT_SHADOWS",
            "_SHADOWS_SOFT", "_SHADOWS_SOFT_LOW", "_SHADOWS_SOFT_MEDIUM", "_SHADOWS_SOFT_HIGH",
            "_LIGHT_COOKIES",
            "_LIGHT_LAYERS", "_WRITE_RENDERING_LAYERS",
            "_REFLECTION_PROBE_BLENDING", "_REFLECTION_PROBE_BOX_PROJECTION", "_REFLECTION_PROBE_ATLAS", "REFLECTION_PROBE_ROTATION",
            "LOD_FADE_CROSSFADE",
            "LIGHTMAP_BICUBIC_SAMPLING",
            "_SCREEN_SPACE_OCCLUSION", "_SCREEN_SPACE_IRRADIANCE",
            "_DBUFFER_MRT1", "_DBUFFER_MRT2", "_DBUFFER_MRT3",
            "_FORWARD_PLUS", "_CLUSTER_LIGHT_LOOP",
            "_MAIN_LIGHT_SHADOWS_SCREEN",
            "_ADDITIONAL_LIGHTS_VERTEX",
            "PROBE_VOLUMES_L1", "PROBE_VOLUMES_L2",
            "EVALUATE_SH_MIXED", "EVALUATE_SH_VERTEX",
            "DEBUG_DISPLAY",
            "DOTS_INSTANCING_ON",
        };

        /// <summary>After URP's own stripping, so the counts are what is really compiled.</summary>
        public int callbackOrder => 1000;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!Active || data == null || data.Count == 0) return;
            switch (snippet.passType)
            {
                case PassType.Vertex:
                case PassType.VertexLM:
                case PassType.ForwardBase:
                case PassType.ForwardAdd:
                case PassType.LightPrePassBase:
                case PassType.LightPrePassFinal:
                case PassType.Deferred:
                    RemovedBuiltInPasses += data.Count;
                    Removed += data.Count;
                    data.Clear();
                    return;
            }
            for (int i = data.Count - 1; i >= 0; i--)
            {
                if (UsesHeadsetOffFeature(data[i].shaderKeywordSet))
                {
                    data.RemoveAt(i);
                    Removed++;
                }
                else Kept++;
            }
        }

        private static bool UsesHeadsetOffFeature(ShaderKeywordSet set)
        {
            foreach (ShaderKeyword keyword in set.GetShaderKeywords())
                if (HeadsetOff.Contains(keyword.name)) return true;
            return false;
        }
    }
}
