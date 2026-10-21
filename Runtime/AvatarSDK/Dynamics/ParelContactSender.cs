using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// A Contact Sender: a shape with tags that Contact Receivers react to. Every avatar also carries automatic senders
    /// on its head, torso, hands, feet and fingers (see <see cref="ParelStandardContacts"/>), tagged Head, Torso, Hand,
    /// HandL, HandR, Foot, FootL, FootR, Finger, FingerL, FingerR, FingerIndex, FingerIndexL ... so receivers only
    /// need those tags to react to people.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/Contact Sender")]
    public sealed class ParelContactSender : ParelContactBase
    {
        /// <summary>Set on the wearer's own hand and finger senders: the hand that vibrates when it touches a receiver with haptics.</summary>
        [System.NonSerialized] public int HapticHand; // 0 none, 1 left, 2 right

        private void OnEnable() => ParelContactSystem.Register(this);
        private void OnDisable() => ParelContactSystem.Unregister(this);
    }
}
