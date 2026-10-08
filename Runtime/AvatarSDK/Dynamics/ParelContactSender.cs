using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Contact Sender: a shape with tags that Contact Receivers react to. Every avatar also
    /// carries automatic senders on its head, torso, hands, feet and fingers, tagged like VRChat's
    /// (Head, Torso, Hand, HandL, HandR, Foot, FootL, FootR, Finger, FingerL, FingerR, FingerIndex...).
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/Contact Sender")]
    public sealed class ParelContactSender : ParelContactBase
    {
        private void OnEnable() => ParelContactSystem.Register(this);
        private void OnDisable() => ParelContactSystem.Unregister(this);
    }
}
