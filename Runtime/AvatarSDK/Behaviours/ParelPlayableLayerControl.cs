using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Playable Layer Control: blends a whole playable layer's weight (usually the Action
    /// layer in at the start of an emote and back out at the end).
    /// </summary>
    public sealed class ParelPlayableLayerControl : StateMachineBehaviour
    {
        public ParelBlendableLayer layer = ParelBlendableLayer.Action;
        [Range(0f, 1f)] public float goalWeight = 1f;
        [Tooltip("Seconds to reach the goal weight (0 = instantly).")]
        public float blendDuration;

        [Tooltip("Logged to the console each time this state is entered (handy while building).")]
        public string debugString = string.Empty;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelPlayableLayerControl] {debugString}", animator);
            ParelAvatarBehaviourHooks.Handler?.OnPlayableLayerControl(animator, this);
        }
    }
}
