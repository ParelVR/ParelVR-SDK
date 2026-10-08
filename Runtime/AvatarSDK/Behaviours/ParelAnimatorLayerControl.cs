using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>VRChat's Animator Layer Control: blends one layer (by index) inside a playable layer's controller.</summary>
    public sealed class ParelAnimatorLayerControl : StateMachineBehaviour
    {
        public ParelBlendableLayer playable = ParelBlendableLayer.FX;
        [Tooltip("Index of the layer inside that playable layer's animator controller.")]
        public int layer = 1;
        [Range(0f, 1f)] public float goalWeight = 1f;
        [Tooltip("Seconds to reach the goal weight (0 = instantly).")]
        public float blendDuration;

        [Tooltip("Logged to the console each time this state is entered (handy while building).")]
        public string debugString = string.Empty;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelAnimatorLayerControl] {debugString}", animator);
            ParelAvatarBehaviourHooks.Handler?.OnAnimatorLayerControl(animator, this);
        }
    }
}
