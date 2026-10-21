using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>Which shaders run on a standalone headset as they are, and what the others are replaced with.</summary>
    public static class ParelQuestShaders
    {
        public const string ToonLit = "ParelVR/Mobile/Toon Lit";
        public const string StandardLite = "ParelVR/Mobile/Standard Lite";
        public const string Unlit = "ParelVR/Mobile/Unlit";
        public const string Particles = "ParelVR/Mobile/Particles";
        public const string ErrorShader = "Hidden/InternalErrorShader";

        public enum Mode { Opaque, Cutout, Transparent }

        // Cheap on a mobile GPU and part of every Unity install or of this SDK: kept as they are, on avatars too.
        private static readonly string[] MobilePrefixes =
        {
            "ParelVR/Mobile/", "Mobile/", "Unlit/", "UI/", "Sprites/", "Skybox/", "TextMeshPro/", "GUI/",
            "Legacy Shaders/Particles/", "Legacy Shaders/Diffuse", "Legacy Shaders/VertexLit", "Legacy Shaders/Transparent/",
            "Nature/Terrain/Diffuse", "Nature/Tree", "Nature/SpeedTree", "Hidden/",
            "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Baked Lit", "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Particles/Simple Lit", "Universal Render Pipeline/Particles/Unlit"
        };

        // Fine on PC, too heavy for a headset's GPU: replaced in worlds as well.
        private static readonly string[] HeavyPrefixes =
        {
            ".poiyomi", "poiyomi", "hidden/locked/.poiyomi", "lil", "_lil", "unitychantoonshader", "uts", "arktoon", "sunao",
            "mochie", "xiexe", "xstoon", "silent", "filamented", "orels", "standard", "autodesk interactive",
            "universal render pipeline/lit", "universal render pipeline/complex lit", "hdrp/", "nature/terrain/standard",
            "nature/terrain/specular"
        };

        // Another built-in shader does the same job on a headset.
        private static readonly Dictionary<string, string> BuiltInSwaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Nature/Terrain/Standard", "Nature/Terrain/Diffuse" },
            { "Nature/Terrain/Specular", "Nature/Terrain/Diffuse" },
        };

        public static bool IsMobile(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName) || shaderName == ErrorShader) return false;
            return MobilePrefixes.Any(p => shaderName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsHeavy(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            string lower = shaderName.ToLowerInvariant();
            return HeavyPrefixes.Any(p => lower.StartsWith(p)) || lower.Contains("poiyomi") || lower.Contains("liltoon");
        }

        public static string BuiltInSwap(string shaderName) =>
            shaderName != null && BuiltInSwaps.TryGetValue(shaderName, out string swap) ? swap : null;

        public static bool LooksUnlit(string shaderName) =>
            !string.IsNullOrEmpty(shaderName) && shaderName.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0;

        public static bool LooksParticle(string shaderName) =>
            !string.IsNullOrEmpty(shaderName) && shaderName.IndexOf("particle", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Why a shader can't run on a standalone headset, or null when it can.</summary>
        public static string Problem(Shader shader)
        {
            if (shader == null || shader.name == ErrorShader) return "is missing or failed to import";
            if (ShaderUtil.ShaderHasError(shader)) return "has compile errors";
            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            string source;
            try { source = File.ReadAllText(path); }
            catch (IOException) { return null; }
            if (Regex.IsMatch(source, @"#pragma\s+geometry")) return "uses a geometry stage";
            if (Regex.IsMatch(source, @"#pragma\s+(hull|domain)")) return "uses tessellation";
            if (Regex.IsMatch(source, @"\bGrabPass\b")) return "uses GrabPass";
            return null;
        }

        /// <summary>
        /// The shader a material gets on a standalone headset, or null when it keeps its own. Avatars only keep
        /// mobile shaders; worlds keep any shader that runs there and isn't one of the heavy PC families.
        /// </summary>
        public static string Replacement(string shaderName, string problem, bool avatar, bool particle)
        {
            bool broken = string.IsNullOrEmpty(shaderName) || shaderName == ErrorShader;
            if (!broken && IsMobile(shaderName)) return null;
            string swap = BuiltInSwap(shaderName);
            if (swap != null) return swap;
            if (!avatar && !broken && problem == null && !IsHeavy(shaderName)) return null;
            if (particle || LooksParticle(shaderName)) return Particles;
            if (avatar) return ToonLit;
            return LooksUnlit(shaderName) ? Unlit : StandardLite;
        }

        /// <summary>How a material blends, from its render queue, keywords and the usual blend-mode properties.</summary>
        public static Mode ModeOf(int queue, IEnumerable<string> keywords, ParelMaterialSnapshot snapshot)
        {
            var keys = new HashSet<string>(keywords ?? Array.Empty<string>());
            if (queue > 2500 || keys.Contains("_ALPHABLEND_ON") || keys.Contains("_ALPHAPREMULTIPLY_ON") || keys.Contains("_SURFACE_TYPE_TRANSPARENT"))
                return Mode.Transparent;
            if (queue >= 2450 || keys.Contains("_ALPHATEST_ON")) return Mode.Cutout;
            if (snapshot != null)
            {
                if (snapshot.Float("_Surface") >= 1f) return Mode.Transparent;
                if (snapshot.Float("_AlphaClip") >= 1f) return Mode.Cutout;
            }
            return Mode.Opaque;
        }

        /// <summary>True when the material adds its color on top (fire, glows) instead of blending over.</summary>
        public static bool IsAdditive(string shaderName, ParelMaterialSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(shaderName) && Regex.IsMatch(shaderName, @"add(itive)?\b", RegexOptions.IgnoreCase)) return true;
            float? dst = snapshot?.Float("_DstBlend");
            return dst.HasValue && Mathf.Approximately(dst.Value, 1f); // BlendMode.One
        }
    }
}
