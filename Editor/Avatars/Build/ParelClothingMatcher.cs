using System;
using System.Collections.Generic;
using ParelVR.AvatarSDK;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Build
{
    /// <summary>
    /// Works out which clothing bone belongs to which avatar bone for <see cref="ParelClothing"/>.
    /// Matching starts at the Hips and walks down both hierarchies together, so a clothing bone only
    /// matches the avatar bone in the same place -- two "Twist" bones on different arms never mix up.
    /// Name prefixes / suffixes the clothing's author added ("Shirt_Hips", "Hips.001") are detected
    /// from the Hips and stripped from every bone.
    /// </summary>
    public static class ParelClothingMatcher
    {
        public sealed class Result
        {
            public Transform AvatarArmature;
            public Transform ClothingArmature;
            public Transform ClothingHips;
            public string Prefix = string.Empty;
            public string Suffix = string.Empty;
            /// <summary>Clothing bone -> avatar bone, parents before children.</summary>
            public readonly List<KeyValuePair<Transform, Transform>> Pairs = new List<KeyValuePair<Transform, Transform>>();
            /// <summary>Clothing bones with no avatar counterpart (skirt, hood, physics bones...).</summary>
            public readonly List<Transform> Unmatched = new List<Transform>();
            public string Error;

            public bool Ok => Error == null && Pairs.Count > 0;
        }

        public static Result Match(ParelAvatarDescriptor descriptor, ParelClothing clothing)
        {
            var result = new Result();
            Animator animator = descriptor != null ? descriptor.AvatarAnimator : null;
            if (animator == null || !animator.isHuman)
            {
                result.Error = "The avatar needs a Humanoid Animator for clothing to be merged onto it.";
                return result;
            }

            Transform avatarHips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (avatarHips == null)
            {
                result.Error = "The avatar's rig has no Hips bone.";
                return result;
            }
            result.AvatarArmature = avatarHips.parent != null ? avatarHips.parent : avatarHips;

            Transform clothingRoot = clothing.transform;
            if (avatarHips.IsChildOf(clothingRoot))
            {
                result.Error = "Clothing (Merge Armature) is on the avatar itself. Put it on the clothing's own root object instead.";
                return result;
            }

            Transform hips = FindClothingHips(clothing, avatarHips.name, out string prefix, out string suffix);
            if (hips == null)
            {
                result.Error = $"Couldn't find the clothing's Hips bone (a bone named like \"{avatarHips.name}\"). Set the Armature Root, or the bone name Prefix / Suffix.";
                return result;
            }
            result.ClothingHips = hips;
            result.ClothingArmature = clothing.armatureRoot != null ? clothing.armatureRoot : (hips.parent != null && hips.parent != clothingRoot ? hips.parent : hips);
            result.Prefix = string.IsNullOrEmpty(clothing.bonePrefix) ? prefix : clothing.bonePrefix;
            result.Suffix = string.IsNullOrEmpty(clothing.boneSuffix) ? suffix : clothing.boneSuffix;

            var excluded = new HashSet<Transform>();
            if (clothing.excludedBones != null)
            {
                foreach (Transform t in clothing.excludedBones)
                {
                    if (t != null) excluded.Add(t);
                }
            }

            Walk(hips, avatarHips, result, excluded);
            if (result.Pairs.Count == 0) result.Error = "None of the clothing's bones match the avatar's.";
            return result;
        }

        private static void Walk(Transform clothingBone, Transform avatarBone, Result result, HashSet<Transform> excluded)
        {
            result.Pairs.Add(new KeyValuePair<Transform, Transform>(clothingBone, avatarBone));
            for (int i = 0; i < clothingBone.childCount; i++)
            {
                Transform child = clothingBone.GetChild(i);
                if (excluded.Contains(child))
                {
                    AddUnmatched(child, result);
                    continue;
                }
                string name = Strip(child.name, result.Prefix, result.Suffix);
                Transform match = FindChild(avatarBone, name);
                if (match != null) Walk(child, match, result, excluded);
                else AddUnmatched(child, result);
            }
        }

        private static void AddUnmatched(Transform bone, Result result)
        {
            result.Unmatched.Add(bone);
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase)) return child;
            }
            // Tolerate small spelling differences in common rigs ("Spine1" vs "Spine.1", "Upper_Leg.L" vs "UpperLeg_L").
            string wanted = Normalize(name);
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (Normalize(child.name) == wanted) return child;
            }
            return null;
        }

        private static string Normalize(string name)
        {
            var chars = new List<char>(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) chars.Add(char.ToLowerInvariant(c));
            }
            return new string(chars.ToArray());
        }

        public static string Strip(string name, string prefix, string suffix)
        {
            string result = name;
            if (!string.IsNullOrEmpty(prefix) && result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result = result.Substring(prefix.Length);
            if (!string.IsNullOrEmpty(suffix) && result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) result = result.Substring(0, result.Length - suffix.Length);
            return result;
        }

        /// <summary>Finds the clothing's Hips: a bone whose name contains the avatar's Hips name, nearest the clothing root.</summary>
        private static Transform FindClothingHips(ParelClothing clothing, string avatarHipsName, out string prefix, out string suffix)
        {
            prefix = string.Empty;
            suffix = string.Empty;
            Transform searchRoot = clothing.armatureRoot != null ? clothing.armatureRoot : clothing.transform;

            Transform best = null;
            int bestDepth = int.MaxValue;
            var queue = new Queue<(Transform t, int depth)>();
            queue.Enqueue((searchRoot, 0));
            while (queue.Count > 0)
            {
                (Transform t, int depth) = queue.Dequeue();
                if (depth > 6) continue;
                if (t != searchRoot || clothing.armatureRoot != null)
                {
                    int index = t.name.IndexOf(avatarHipsName, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0 && depth < bestDepth && t.childCount > 0)
                    {
                        best = t;
                        bestDepth = depth;
                        prefix = t.name.Substring(0, index);
                        suffix = t.name.Substring(index + avatarHipsName.Length);
                    }
                }
                for (int i = 0; i < t.childCount; i++) queue.Enqueue((t.GetChild(i), depth + 1));
            }

            if (best == null)
            {
                // Rigs that call it "Hip" / "pelvis" while the avatar says "Hips".
                foreach (Transform t in searchRoot.GetComponentsInChildren<Transform>(true))
                {
                    string lower = t.name.ToLowerInvariant();
                    if ((lower.Contains("hip") || lower.Contains("pelvis")) && t.childCount > 0)
                    {
                        return t;
                    }
                }
            }
            return best;
        }
    }
}
