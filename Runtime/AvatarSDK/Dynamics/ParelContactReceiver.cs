using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Contact Receiver: sets an avatar parameter when a sender with a matching tag touches
    /// it -- Constant (true while touching), On Enter (true for one frame as contact starts, if the
    /// sender moves at least Min Velocity) or Proximity (0 at the edge to 1 at the center).
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/Contact Receiver")]
    public sealed class ParelContactReceiver : ParelContactBase
    {
        public enum ReceiverType
        {
            Constant = 0,
            OnEnter = 1,
            Proximity = 2,
        }

        public ReceiverType receiverType = ReceiverType.Constant;
        [Tooltip("The avatar parameter this receiver sets (Bool / Int / Float).")]
        public string parameter = string.Empty;
        [Tooltip("React to senders on this same avatar.")]
        public bool allowSelf = true;
        [Tooltip("React to senders on other avatars and in the world.")]
        public bool allowOthers = true;
        [Tooltip("On Enter: slowest sender speed (m/s) that counts.")]
        public float minVelocity = 0.05f;

        [System.NonSerialized] internal bool WasTouching;
        [System.NonSerialized] internal float LastValue = -1f;
        [System.NonSerialized] internal bool PendingReset;

        /// <summary>The value this receiver currently reports (for UI / debugging).</summary>
        public float CurrentValue => LastValue < 0f ? 0f : LastValue;

        private void OnEnable() => ParelContactSystem.Register(this);

        private void OnDisable()
        {
            ParelContactSystem.Unregister(this);
            WasTouching = false;
            LastValue = -1f;
        }
    }
}
