using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Attaches this object (a hat, glasses, a prop) to one of the avatar's humanoid bones when the
    /// avatar is built, so it follows that bone -- no dragging it into the armature by hand.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar Tools/Attach To Bone")]
    public sealed class ParelBoneAttachment : MonoBehaviour, IParelEditorOnly
    {
        public HumanBodyBones bone = HumanBodyBones.Head;

        [Tooltip("Stay exactly where it is now (on) or snap onto the bone (off).")]
        public bool keepWorldPose = true;
    }
}
