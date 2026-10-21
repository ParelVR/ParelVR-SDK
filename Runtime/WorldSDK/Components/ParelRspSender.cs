using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Sends from this part of the world to RSP apps. Hook the Send methods up to a button or any event,
    /// send this object's on/off state, or follow an Animator parameter and send it whenever it changes.
    /// The address lives under /world/, so "door" is /world/door.
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/RSP Sender")]
    public sealed class ParelRspSender : MonoBehaviour
    {
        [Tooltip("The address apps hear this on. Everything a world sends is under /world/, e.g. /world/door.")]
        [SerializeField] private string address = "/world/example";

        [Tooltip("Sends true when this object is switched on and false when it is switched off.")]
        [SerializeField] private bool sendActiveState;

        [Header("Animator")]
        [Tooltip("Sends the parameter below whenever its value changes.")]
        [SerializeField] private Animator animator;
        [SerializeField] private string parameter;

        private bool watching;
        private AnimatorControllerParameterType watchedType;
        private float lastSent = float.NaN;

        public string Address => ParelRspWorld.Normalize(address);

        public void SendFloat(float value) => ParelRspWorld.Send(address, value);
        public void SendInt(int value) => ParelRspWorld.Send(address, value);
        public void SendBool(bool value) => ParelRspWorld.Send(address, value);
        public void SendString(string value) => ParelRspWorld.Send(address, value ?? string.Empty);
        /// <summary>A press with no value, e.g. from a button.</summary>
        public void SendTrigger() => ParelRspWorld.Send(address);

        private void OnEnable()
        {
            if (sendActiveState) SendBool(true);
            watching = FindParameter();
            lastSent = float.NaN;
        }

        private void OnDisable()
        {
            if (sendActiveState) SendBool(false);
        }

        private void LateUpdate()
        {
            if (!watching || animator == null || !animator.isActiveAndEnabled) return;
            float value;
            switch (watchedType)
            {
                case AnimatorControllerParameterType.Bool: value = animator.GetBool(parameter) ? 1f : 0f; break;
                case AnimatorControllerParameterType.Int: value = animator.GetInteger(parameter); break;
                case AnimatorControllerParameterType.Float: value = animator.GetFloat(parameter); break;
                default: return;
            }
            if (value.Equals(lastSent)) return;
            lastSent = value;
            if (watchedType == AnimatorControllerParameterType.Bool) SendBool(value >= 0.5f);
            else if (watchedType == AnimatorControllerParameterType.Int) SendInt(Mathf.RoundToInt(value));
            else SendFloat(value);
        }

        private bool FindParameter()
        {
            if (animator == null || string.IsNullOrEmpty(parameter)) return false;
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.name != parameter) continue;
                watchedType = p.type;
                return p.type != AnimatorControllerParameterType.Trigger;
            }
            return false;
        }
    }
}
