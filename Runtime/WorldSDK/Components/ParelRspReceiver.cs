using System;
using UnityEngine;
using UnityEngine.Events;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Lets RSP apps drive this part of the world. Pick an address (it lives under /world/, so "lights" is
    /// /world/lights), then choose what a message does: switch objects on and off, set an Animator
    /// parameter, and/or call your own events with the value. Put it on an object that stays on: a
    /// receiver that is switched off stops listening.
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/RSP Receiver")]
    public sealed class ParelRspReceiver : MonoBehaviour
    {
        [Tooltip("The address apps send to. Everything a world listens on is under /world/, e.g. /world/lights.")]
        [SerializeField] private string address = "/world/example";

        [Header("Switch Objects")]
        [Tooltip("Switched on by true / 1 and off by false / 0.")]
        [SerializeField] private GameObject[] toggleObjects = Array.Empty<GameObject>();
        [Tooltip("Switch them the other way round: true / 1 switches them off.")]
        [SerializeField] private bool invert;

        [Header("Animator")]
        [Tooltip("Gets the value in the parameter below. Bool, Int, Float and Trigger parameters all work.")]
        [SerializeField] private Animator animator;
        [SerializeField] private string parameter;

        [Header("Events")]
        public UnityEvent<float> onFloat = new UnityEvent<float>();
        public UnityEvent<bool> onBool = new UnityEvent<bool>();
        public UnityEvent<string> onString = new UnityEvent<string>();
        [Tooltip("Called for every message, whatever it carries.")]
        public UnityEvent onMessage = new UnityEvent();

        private string registered;

        /// <summary>The full address this listens on, e.g. /world/lights.</summary>
        public string Address => ParelRspWorld.Normalize(address);
        public float LastValue { get; private set; }
        public string LastText { get; private set; } = string.Empty;

        private void OnEnable()
        {
            registered = ParelRspWorld.Normalize(address);
            if (registered != null) ParelRspWorld.Register(registered, this);
        }

        private void OnDisable()
        {
            ParelRspWorld.Unregister(registered, this);
            registered = null;
        }

        private void OnValidate()
        {
            if (!isActiveAndEnabled || !Application.isPlaying) return;
            OnDisable();
            OnEnable();
        }

        internal void Receive(object[] args)
        {
            object first = args.Length > 0 ? args[0] : null;
            float value = ToFloat(first);
            bool on = value >= 0.5f;
            string text = first as string ?? (first != null ? Convert.ToString(first, System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
            LastValue = value;
            LastText = text;

            for (int i = 0; i < toggleObjects.Length; i++)
            {
                if (toggleObjects[i] != null) toggleObjects[i].SetActive(on != invert);
            }

            if (animator != null && !string.IsNullOrEmpty(parameter)) SetAnimator(value, on, first == null);

            onFloat?.Invoke(value);
            onBool?.Invoke(on);
            onString?.Invoke(text);
            onMessage?.Invoke();
        }

        private void SetAnimator(float value, bool on, bool noValue)
        {
            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name != parameter) continue;
                switch (parameters[i].type)
                {
                    case AnimatorControllerParameterType.Bool: animator.SetBool(parameter, on); break;
                    case AnimatorControllerParameterType.Int: animator.SetInteger(parameter, Mathf.RoundToInt(value)); break;
                    case AnimatorControllerParameterType.Float: animator.SetFloat(parameter, value); break;
                    case AnimatorControllerParameterType.Trigger: if (on || noValue) animator.SetTrigger(parameter); break;
                }
                return;
            }
        }

        private static float ToFloat(object arg)
        {
            switch (arg)
            {
                case null: return 1f;   // a message with nothing in it counts as "on" / a press
                case float f: return f;
                case double d: return (float)d;
                case int n: return n;
                case long l: return l;
                case bool b: return b ? 1f : 0f;
                case string s:
                    if (bool.TryParse(s, out bool sb)) return sb ? 1f : 0f;
                    return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sf) ? sf : 0f;
                default: return 0f;
            }
        }
    }
}
