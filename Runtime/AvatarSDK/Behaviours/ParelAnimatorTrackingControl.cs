using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Animator Tracking Control: switches body parts between full tracking (your headset,
    /// controllers and trackers drive them) and animation (the playable layers drive them) -- used by
    /// emotes, sitting poses, custom hand poses and face animations.
    /// </summary>
    public sealed class ParelAnimatorTrackingControl : StateMachineBehaviour
    {
        public enum TrackingType
        {
            NoChange = 0,
            Tracking = 1,
            Animation = 2,
        }

        public TrackingType trackingHead;
        public TrackingType trackingLeftHand;
        public TrackingType trackingRightHand;
        public TrackingType trackingHip;
        public TrackingType trackingLeftFoot;
        public TrackingType trackingRightFoot;
        public TrackingType trackingLeftFingers;
        public TrackingType trackingRightFingers;
        public TrackingType trackingEyes;
        public TrackingType trackingMouth;

        [Tooltip("Logged to the console each time this state is entered (handy while building).")]
        public string debugString = string.Empty;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelAnimatorTrackingControl] {debugString}", animator);
            ParelAvatarBehaviourHooks.Handler?.OnTrackingControl(animator, this);
        }
    }
}
