using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// The avatar's Expression Parameters -- the same idea as VRChat's: every value the Expressions
    /// Menu (the radial menu's Expressions page) can drive, and the avatar's FX animator reads.
    /// Network-synced parameters share a 256-bit budget, costed exactly like VRChat:
    /// Bool = 1 bit, Int = 8 bits, Float = 8 bits.
    /// </summary>
    [CreateAssetMenu(fileName = "ExpressionParameters", menuName = "ParelVR/Avatars/Expression Parameters", order = 1)]
    public sealed class ParelExpressionParameters : ScriptableObject
    {
        public const int MaxSyncedBits = 256;
        public const int MaxParameters = 256;

        public enum ValueType
        {
            Int,
            Float,
            Bool,
        }

        [Serializable]
        public sealed class Parameter
        {
            public string name = string.Empty;
            public ValueType valueType = ValueType.Bool;
            [Tooltip("Value the parameter starts at (Bool: 0 or 1, Int: 0-255, Float: -1 to 1).")]
            public float defaultValue;
            [Tooltip("Remember the last value between sessions, per avatar.")]
            public bool saved = true;
            [Tooltip("Send this parameter to everyone else in the instance so they see the same result.")]
            public bool networkSynced = true;

            public int SyncCost => networkSynced ? CostOf(valueType) : 0;

            public Parameter Clone()
            {
                return new Parameter
                {
                    name = name,
                    valueType = valueType,
                    defaultValue = defaultValue,
                    saved = saved,
                    networkSynced = networkSynced,
                };
            }
        }

        public List<Parameter> parameters = new List<Parameter>();

        public static int CostOf(ValueType type) => type == ValueType.Bool ? 1 : 8;

        /// <summary>Bits used by every network-synced parameter (VRChat's "Total Cost").</summary>
        public int CalcTotalCost()
        {
            int total = 0;
            if (parameters == null) return 0;
            for (int i = 0; i < parameters.Count; i++)
            {
                Parameter p = parameters[i];
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                total += p.SyncCost;
            }
            return total;
        }

        public Parameter FindParameter(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName) || parameters == null) return null;
            for (int i = 0; i < parameters.Count; i++)
            {
                Parameter p = parameters[i];
                if (p != null && p.name == parameterName) return p;
            }
            return null;
        }

        /// <summary>Clamps a raw value into the legal range for the parameter's type.</summary>
        public static float Sanitize(ValueType type, float value)
        {
            switch (type)
            {
                case ValueType.Bool:
                    return value >= 0.5f ? 1f : 0f;
                case ValueType.Int:
                    return Mathf.Clamp(Mathf.Round(value), 0f, 255f);
                default:
                    if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
                    return Mathf.Clamp(value, -1f, 1f);
            }
        }

        /// <summary>
        /// Stable fingerprint of the synced layout (names, types, order). Sender and receiver must
        /// agree on it before the bit-packed values mean the same thing on both ends, so a peer that
        /// is still loading the previous avatar ignores state meant for the new one.
        /// </summary>
        public uint ComputeSyncSchemaHash()
        {
            unchecked
            {
                uint hash = 2166136261u;
                if (parameters == null) return hash;
                for (int i = 0; i < parameters.Count; i++)
                {
                    Parameter p = parameters[i];
                    if (p == null || string.IsNullOrEmpty(p.name) || !p.networkSynced) continue;
                    string key = p.name;
                    for (int c = 0; c < key.Length; c++)
                    {
                        hash ^= key[c];
                        hash *= 16777619u;
                    }
                    hash ^= (uint)p.valueType + 1u;
                    hash *= 16777619u;
                }
                return hash;
            }
        }
    }
}
