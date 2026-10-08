using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Finds lip-sync and blink blend shapes by name. Used by the SDK's "Auto Detect" and by the
    /// game when an avatar's lip sync is left on Default. Understands the common naming schemes:
    /// VRChat (vrc.v_aa), Oculus / Ready Player Me (viseme_aa), VRoid (Fcl_MTH_A), plain A/I/U/E/O,
    /// and MMD (あ い う え お).
    /// </summary>
    public static class ParelVisemeAutoMapper
    {
        /// <summary>The 15 Oculus visemes, in the order VRChat's descriptor and Basis both use.</summary>
        public static readonly string[] VisemeNames =
        {
            "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou",
        };

        public const int VisemeCount = 15;

        private static readonly string[] StripPrefixes =
        {
            "vrc.v_", "vrc_v_", "v_", "viseme_", "viseme", "mouth_", "mth_", "fcl_mth_", "vrc.",
        };

        // Vowel-only rigs (VRoid, MMD, many VRM exports) only have A I U E O; those cover the 5
        // vowel visemes, which is what actually reads as talking.
        private static readonly Dictionary<string, int> VowelFallback = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "a", 10 }, { "あ", 10 },
            { "e", 11 }, { "え", 11 },
            { "i", 12 }, { "い", 12 },
            { "o", 13 }, { "お", 13 },
            { "u", 14 }, { "う", 14 },
        };

        /// <summary>Returns 15 blend shape names (null where nothing matched), in viseme order.</summary>
        public static string[] DetectVisemes(Mesh mesh)
        {
            var result = new string[VisemeCount];
            if (mesh == null) return result;

            int count = mesh.blendShapeCount;
            for (int i = 0; i < count; i++)
            {
                string shape = mesh.GetBlendShapeName(i);
                int viseme = MatchViseme(shape);
                if (viseme >= 0 && result[viseme] == null) result[viseme] = shape;
            }

            // Second pass: vowel-only names, only for slots still empty.
            for (int i = 0; i < count; i++)
            {
                string shape = mesh.GetBlendShapeName(i);
                string key = Normalize(shape);
                if (VowelFallback.TryGetValue(key, out int viseme) && result[viseme] == null) result[viseme] = shape;
            }
            return result;
        }

        /// <summary>How many of the 5 vowel visemes (aa E ih oh ou) a mesh has -- used to pick the face mesh.</summary>
        public static int ScoreFaceMesh(Mesh mesh)
        {
            string[] found = DetectVisemes(mesh);
            int score = 0;
            for (int i = 0; i < found.Length; i++)
            {
                if (found[i] != null) score += i >= 10 ? 3 : 1;
            }
            if (DetectBlink(mesh) != null) score += 2;
            return score;
        }

        /// <summary>The skinned mesh with the most recognisable visemes (the face), or null.</summary>
        public static SkinnedMeshRenderer FindFaceMesh(GameObject root)
        {
            if (root == null) return null;
            SkinnedMeshRenderer best = null;
            int bestScore = 0;
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                int score = ScoreFaceMesh(smr.sharedMesh);
                if (score > bestScore)
                {
                    best = smr;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>The best "both eyes closed" blend shape name, or null.</summary>
        public static string DetectBlink(Mesh mesh)
        {
            if (mesh == null) return null;
            string best = null;
            int bestScore = 0;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string shape = mesh.GetBlendShapeName(i);
                string lower = shape.ToLowerInvariant();
                int score = 0;
                if (lower == "blink" || lower == "vrc.blink" || lower == "eye_blink" || lower == "eyeblink") score = 10;
                else if (lower.Contains("blink") && !lower.Contains("_l") && !lower.Contains("_r") && !lower.Contains("left") && !lower.Contains("right")) score = 8;
                else if (lower == "fcl_eye_close" || lower.EndsWith("eye_close")) score = 7;
                else if (shape == "まばたき") score = 7;
                else if (lower.Contains("blink")) score = 3;
                if (score > bestScore)
                {
                    best = shape;
                    bestScore = score;
                }
            }
            return best;
        }

        private static int MatchViseme(string shape)
        {
            string key = Normalize(shape);
            if (string.IsNullOrEmpty(key)) return -1;
            for (int v = 0; v < VisemeNames.Length; v++)
            {
                if (key == VisemeNames[v].ToLowerInvariant()) return v;
            }
            return -1;
        }

        private static string Normalize(string shape)
        {
            if (string.IsNullOrEmpty(shape)) return string.Empty;
            string lower = shape.Trim().ToLowerInvariant();
            for (int i = 0; i < StripPrefixes.Length; i++)
            {
                if (lower.StartsWith(StripPrefixes[i], StringComparison.Ordinal) && lower.Length > StripPrefixes[i].Length)
                {
                    lower = lower.Substring(StripPrefixes[i].Length);
                    break;
                }
            }
            return lower;
        }

        public static int BlendShapeIndex(SkinnedMeshRenderer smr, string shapeName)
        {
            if (smr == null || smr.sharedMesh == null || string.IsNullOrEmpty(shapeName)) return -1;
            return smr.sharedMesh.GetBlendShapeIndex(shapeName);
        }
    }
}
