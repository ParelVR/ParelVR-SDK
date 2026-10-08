using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Puts clothing made for this avatar onto it without hand-parenting bones. The clothing's armature
    /// is matched to the avatar's by bone name (prefixes like "Shirt_" and suffixes like ".001" are
    /// detected) and joined to it when the avatar is built. The clothing in your scene is never changed.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar Tools/Clothing (Merge Armature)")]
    public sealed class ParelClothing : MonoBehaviour, IParelEditorOnly
    {
        public enum MergeMode
        {
            /// <summary>Each clothing bone becomes a child of the matching avatar bone. Always safe.</summary>
            ParentToBones = 0,
            /// <summary>The clothing uses the avatar's bones directly and its duplicate bones are removed (better performance).</summary>
            MergeBones = 1,
        }

        [Tooltip("The clothing's own armature (the object above its Hips bone). Empty = found automatically.")]
        public Transform armatureRoot;

        [Tooltip("Merge Bones gives the best performance; Parent To Bones keeps every clothing bone.")]
        public MergeMode mode = MergeMode.MergeBones;

        [Tooltip("Text in front of the clothing's bone names, e.g. \"Shirt_\" in \"Shirt_Hips\". Empty = detected.")]
        public string bonePrefix = string.Empty;

        [Tooltip("Text after the clothing's bone names, e.g. \".001\" in \"Hips.001\". Empty = detected.")]
        public string boneSuffix = string.Empty;

        [Tooltip("Bones that should stay where they are instead of joining the avatar.")]
        public List<Transform> excludedBones = new List<Transform>();

        [Tooltip("Remove an Animator the clothing came with, so it can't fight the avatar's.")]
        public bool removeClothingAnimator = true;
    }
}
