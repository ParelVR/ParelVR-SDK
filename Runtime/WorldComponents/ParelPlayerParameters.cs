using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.SDK.Worlds.Components
{
    /// <summary>
    /// The world's Player Parameters: named values every player carries while they are in the world (team, role, VIP,
    /// a toggle), set up like animator parameters and read or written per player by world scripts through the
    /// PlayerParameters script. Synced parameters share a 256-bit budget: Bool = 1 bit, Int = 8 bits, Float = 8 bits.
    /// Edited in ParelVR SDK > Volt > Settings > Player Parameters; the world descriptor points at it.
    /// </summary>
    [CreateAssetMenu(fileName = "PlayerParameters", menuName = "ParelVR/Worlds/Player Parameters", order = 2)]
    public sealed class ParelPlayerParameters : ScriptableObject
    {
        public const int MaxSyncedBits = 256;
        public const int MaxParameters = 128;
        public const int MaxNameLength = 32;

        public enum ValueType
        {
            Bool = 0,
            Int = 1,
            Float = 2,
        }

        [Serializable]
        public sealed class Parameter
        {
            public string name = string.Empty;
            public ValueType valueType = ValueType.Bool;
            [Tooltip("Value every player starts with (Bool: 0 or 1, Int: 0-255, Float: -1 to 1).")]
            public float defaultValue;
            [Tooltip("Remember the player's last value the next time they visit this world (kept on their own account).")]
            public bool saved;
            [Tooltip("Send the value to everyone in the instance, so every client sees the same thing.")]
            public bool synced = true;

            public int SyncCost => synced ? CostOf(valueType) : 0;

            public Parameter Clone() => new Parameter { name = name, valueType = valueType, defaultValue = defaultValue, saved = saved, synced = synced };
        }

        public List<Parameter> parameters = new List<Parameter>();

        public static int CostOf(ValueType type) => type == ValueType.Bool ? 1 : 8;

        /// <summary>Bits used by every synced parameter.</summary>
        public int CalcTotalCost()
        {
            int total = 0;
            if (parameters == null) return 0;
            foreach (Parameter p in parameters)
                if (p != null && !string.IsNullOrEmpty(p.name)) total += p.SyncCost;
            return total;
        }

        public Parameter FindParameter(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName) || parameters == null) return null;
            foreach (Parameter p in parameters)
                if (p != null && p.name == parameterName) return p;
            return null;
        }

        public bool AnySaved()
        {
            if (parameters != null)
                foreach (Parameter p in parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name) && p.saved) return true;
            return false;
        }

        public bool AnySynced()
        {
            if (parameters != null)
                foreach (Parameter p in parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name) && p.synced) return true;
            return false;
        }

        /// <summary>Clamps a value into the legal range of its type, the same way the world script does.</summary>
        public static float Sanitize(ValueType type, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            switch (type)
            {
                case ValueType.Bool: return value >= 0.5f ? 1f : 0f;
                case ValueType.Int: return Mathf.Clamp(Mathf.Round(value), 0f, 255f);
                default: return Mathf.Round(Mathf.Clamp(value, -1f, 1f) * 127f) / 127f;
            }
        }

        /// <summary>
        /// Fingerprint of the synced layout (names, types, order). Clients only apply each other's values when theirs
        /// match, so a client still running an older build of the world never misreads them. Never 0.
        /// </summary>
        public int ComputeSchemaHash()
        {
            unchecked
            {
                uint hash = 2166136261u;
                if (parameters != null)
                {
                    foreach (Parameter p in parameters)
                    {
                        if (p == null || string.IsNullOrEmpty(p.name) || !p.synced) continue;
                        foreach (char c in p.name)
                        {
                            hash ^= c;
                            hash *= 16777619u;
                        }
                        hash ^= (uint)p.valueType + 1u;
                        hash *= 16777619u;
                    }
                }
                int result = (int)(hash & 0x7FFFFFFF);
                return result == 0 ? 1 : result;
            }
        }

        /// <summary>Names that are empty, too long, repeated, or not letters, digits, dots, dashes and underscores.</summary>
        public List<string> FindProblems()
        {
            var problems = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (parameters == null) return problems;
            for (int i = 0; i < parameters.Count; i++)
            {
                Parameter p = parameters[i];
                if (p == null) continue;
                if (string.IsNullOrEmpty(p.name)) { problems.Add("Parameter " + (i + 1) + " has no name."); continue; }
                if (p.name.Length > MaxNameLength) problems.Add("'" + p.name + "' is longer than " + MaxNameLength + " characters.");
                foreach (char c in p.name)
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) { problems.Add("'" + p.name + "' may only use letters, digits, dots, dashes and underscores."); break; }
                if (!seen.Add(p.name)) problems.Add("'" + p.name + "' is used twice.");
            }
            if (parameters.Count > MaxParameters) problems.Add("More than " + MaxParameters + " parameters.");
            if (CalcTotalCost() > MaxSyncedBits) problems.Add("Synced parameters use " + CalcTotalCost() + " of " + MaxSyncedBits + " bits.");
            return problems;
        }
    }
}
