using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Head Chop: in first person ParelVR hides your head so it doesn't block your view.
    /// Head Chop chooses which bones get hidden and how much -- keep long hair or a hat brim visible,
    /// or hide extra bones (glasses on a neck bone) that the default would miss.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/Head Chop")]
    public sealed class ParelHeadChop : MonoBehaviour
    {
        public enum ApplyCondition
        {
            /// <summary>Scaled whenever you see yourself in first person.</summary>
            AlwaysApply = 0,
            /// <summary>Only while the bone would otherwise be hidden (inside the head).</summary>
            ApplyOnlyWhenHidden = 1,
        }

        [Serializable]
        public sealed class Target
        {
            public Transform transform;
            [Range(0f, 1f), Tooltip("0 = hidden in first person, 1 = fully visible.")]
            public float scaleFactor = 1f;
            public ApplyCondition applyCondition = ApplyCondition.AlwaysApply;
        }

        public List<Target> targetBones = new List<Target>();
        [Range(0f, 1f), Tooltip("Multiplies every target's scale factor.")]
        public float globalScaleFactor = 1f;

        /// <summary>The scale a bone should have in first person, or null if Head Chop doesn't mention it.</summary>
        public float? ScaleFor(Transform bone)
        {
            if (bone == null || targetBones == null) return null;
            for (int i = 0; i < targetBones.Count; i++)
            {
                Target t = targetBones[i];
                if (t != null && t.transform == bone) return Mathf.Clamp01(t.scaleFactor * globalScaleFactor);
            }
            return null;
        }
    }
}
