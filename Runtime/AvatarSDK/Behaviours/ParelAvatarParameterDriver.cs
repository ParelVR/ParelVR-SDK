using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Avatar Parameter Driver: when its state is entered, sets / adds to / randomizes /
    /// copies avatar parameters. By default it runs only for the avatar's wearer (Local Only) and the
    /// results reach everyone else through parameter sync, exactly like VRChat.
    /// </summary>
    public sealed class ParelAvatarParameterDriver : StateMachineBehaviour
    {
        public enum ChangeType
        {
            Set = 0,
            Add = 1,
            Random = 2,
            Copy = 3,
        }

        [Serializable]
        public sealed class Parameter
        {
            public ChangeType type = ChangeType.Set;
            [Tooltip("The parameter to change.")]
            public string name = string.Empty;
            [Tooltip("Copy: the parameter to read from.")]
            public string source = string.Empty;
            public float value;
            [Tooltip("Random (Int / Float): lowest value.")]
            public float valueMin;
            [Tooltip("Random (Int / Float): highest value.")]
            public float valueMax = 1f;
            [Tooltip("Random (Bool): chance of true.")]
            [Range(0f, 1f)] public float chance = 0.5f;
            [Tooltip("Random (Int): never pick the value it already has.")]
            public bool preventRepeats;
            [Tooltip("Copy: remap the source range to the destination range.")]
            public bool convertRange;
            public float sourceMin;
            public float sourceMax = 1f;
            public float destMin;
            public float destMax = 1f;
        }

        public List<Parameter> parameters = new List<Parameter>();
        [Tooltip("Only run for the avatar's wearer (synced results still reach everyone).")]
        public bool localOnly = true;
        [Tooltip("Logged to the console each time this driver runs (handy while building).")]
        public string debugString = string.Empty;

        private static readonly System.Random Rng = new System.Random();

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            IParelAvatarParameterSink sink = ParelAvatarSinks.Find(animator);
            if (sink == null || parameters == null) return;
            if (localOnly && !sink.IsLocalAvatar) return;

            if (!string.IsNullOrEmpty(debugString)) Debug.Log($"[ParelAvatarParameterDriver] {debugString}");

            for (int i = 0; i < parameters.Count; i++)
            {
                Parameter p = parameters[i];
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                Apply(sink, p);
            }
        }

        private static void Apply(IParelAvatarParameterSink sink, Parameter p)
        {
            ParelParameterKind kind = sink.GetParameterKind(p.name);
            sink.TryGetParameter(p.name, out float current);

            float next;
            switch (p.type)
            {
                case ChangeType.Set:
                    next = p.value;
                    break;
                case ChangeType.Add:
                    next = current + p.value;
                    break;
                case ChangeType.Random:
                    next = RandomValue(kind, p, current);
                    break;
                case ChangeType.Copy:
                    if (!sink.TryGetParameter(p.source, out float source)) return;
                    next = p.convertRange ? Remap(source, p) : source;
                    break;
                default:
                    return;
            }

            sink.SetParameter(p.name, next, ParelParameterSource.Driver);
        }

        private static float RandomValue(ParelParameterKind kind, Parameter p, float current)
        {
            switch (kind)
            {
                case ParelParameterKind.Bool:
                    return Rng.NextDouble() < p.chance ? 1f : 0f;
                case ParelParameterKind.Int:
                {
                    int min = Mathf.RoundToInt(Mathf.Min(p.valueMin, p.valueMax));
                    int max = Mathf.RoundToInt(Mathf.Max(p.valueMin, p.valueMax));
                    if (max <= min) return min;
                    int pick = Rng.Next(min, max + 1);
                    if (p.preventRepeats && pick == Mathf.RoundToInt(current))
                    {
                        // Shift to a different value in range instead of re-rolling forever.
                        pick = pick + 1 + Rng.Next(0, max - min);
                        if (pick > max) pick = min + (pick - max - 1);
                    }
                    return pick;
                }
                default:
                {
                    float min = Mathf.Min(p.valueMin, p.valueMax);
                    float max = Mathf.Max(p.valueMin, p.valueMax);
                    return min + (float)Rng.NextDouble() * (max - min);
                }
            }
        }

        private static float Remap(float value, Parameter p)
        {
            float span = p.sourceMax - p.sourceMin;
            float t = Mathf.Approximately(span, 0f) ? 0f : Mathf.Clamp01((value - p.sourceMin) / span);
            return Mathf.Lerp(p.destMin, p.destMax, t);
        }
    }
}
