using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>VRChat's Animator Locomotion Control: stops (or restores) the wearer's movement.</summary>
    public sealed class ParelAnimatorLocomotionControl : StateMachineBehaviour
    {
        [Tooltip("True: the wearer can't walk while this state is active. False: movement comes back.")]
        public bool disableLocomotion;

        [Tooltip("Logged to the console each time this state is entered (handy while building).")]
        public string debugString = string.Empty;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelAnimatorLocomotionControl] {debugString}", animator);
            ParelAvatarBehaviourHooks.Handler?.OnLocomotionControl(animator, disableLocomotion);
        }
    }
}
