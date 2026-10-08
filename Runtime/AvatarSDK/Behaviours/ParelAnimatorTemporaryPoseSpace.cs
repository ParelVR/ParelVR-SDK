using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Animator Temporary Pose Space: moves the wearer's view to where the animated head is
    /// (for lying-down / sitting emotes), or back to normal.
    /// </summary>
    public sealed class ParelAnimatorTemporaryPoseSpace : StateMachineBehaviour
    {
        [Tooltip("True: move the view to the animated head. False: return to the normal view.")]
        public bool enterPoseSpace = true;
        [Tooltip("True: Delay Time is in seconds. False: it's the state's normalized time (0-1).")]
        public bool fixedDelay = true;
        public float delayTime;

        private float _enteredAt;
        private bool _fired;

        [Tooltip("Logged to the console each time this state is entered (handy while building).")]
        public string debugString = string.Empty;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelAnimatorTemporaryPoseSpace] {debugString}", animator);
            _enteredAt = Time.time;
            _fired = false;
            TryFire(animator, stateInfo);
        }

        public override void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            TryFire(animator, stateInfo);
        }

        private void TryFire(Animator animator, AnimatorStateInfo stateInfo)
        {
            if (_fired) return;
            bool ready = fixedDelay ? Time.time - _enteredAt >= delayTime : stateInfo.normalizedTime >= delayTime;
            if (!ready) return;
            _fired = true;
            ParelAvatarBehaviourHooks.Handler?.OnTemporaryPoseSpace(animator, this);
        }
    }
}
