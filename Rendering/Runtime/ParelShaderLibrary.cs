using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// The ParelVR shaders, referenced directly. Because the asset lives in a Resources folder, every shader it
    /// references is included in player builds, so PVRP never depends on a shader surviving stripping by luck.
    /// <see cref="Resolve"/> only falls back to <see cref="Shader.Find(string)"/> when the asset is missing an entry,
    /// and logs that it had to.
    /// </summary>
    [CreateAssetMenu(menuName = "ParelVR/Rendering/Shader Library", fileName = "ParelShaderLibrary")]
    public sealed class ParelShaderLibrary : ScriptableObject
    {
        /// <summary>Path under a Resources folder, without extension.</summary>
        public const string ResourcePath = "ParelVR/Rendering/ParelShaderLibrary";

        [Serializable]
        public sealed class Entry
        {
            public ShaderRole Role;
            [Tooltip("Used on PC, and on Quest when no Quest shader is set.")]
            public Shader Pc;
            [Tooltip("Optional cheaper version for standalone headsets.")]
            public Shader Quest;
        }

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        [Tooltip("The keyword combinations runtime conversion can produce. Being referenced from Resources puts it in every build, which keeps those variants from being stripped.")]
        [SerializeField] private ShaderVariantCollection variants;

        private static ParelShaderLibrary _active;
        private static bool _loadAttempted;
        private static readonly HashSet<string> WarnedLookups = new HashSet<string>();

        public IReadOnlyList<Entry> Entries => entries;
        public ShaderVariantCollection Variants => variants;

        /// <summary>The library in use: whatever was assigned, else the one in Resources.</summary>
        public static ParelShaderLibrary Active
        {
            get
            {
                if (_active == null && !_loadAttempted)
                {
                    _loadAttempted = true;
                    _active = Resources.Load<ParelShaderLibrary>(ResourcePath);
                    if (_active == null) RenderingLog.Warning("No ParelShaderLibrary at Resources/" + ResourcePath + "; shader lookups fall back to Shader.Find.");
                }
                return _active;
            }
            set
            {
                _active = value;
                _loadAttempted = true;
            }
        }

        public Shader Get(ShaderRole role, TargetPlatform target)
        {
            foreach (Entry e in entries)
            {
                if (e == null || e.Role != role) continue;
                if (target == TargetPlatform.Quest && e.Quest != null) return e.Quest;
                return e.Pc;
            }
            return null;
        }

        public IEnumerable<Shader> AllShaders()
        {
            foreach (Entry e in entries)
            {
                if (e == null) continue;
                if (e.Pc != null) yield return e.Pc;
                if (e.Quest != null) yield return e.Quest;
            }
        }

        /// <summary>
        /// The usable shader for a role, walking up <see cref="ParelShaderNames.Parent"/> (e.g. Cutout → Lit) until one
        /// is found that can run here. Returns null only when not even ParelVR/Unlit can be found.
        /// </summary>
        public static Shader Resolve(ShaderRole role, TargetPlatform target, out ShaderRole resolvedRole)
        {
            ShaderRole? current = role;
            while (current.HasValue)
            {
                ShaderRole r = current.Value;
                Shader shader = Active != null ? Active.Get(r, target) : null;
                if (shader == null)
                {
                    string name = ParelShaderNames.For(r);
                    shader = Shader.Find(name);
                    if (shader != null && WarnedLookups.Add(name))
                        RenderingLog.Warning("Shader '" + name + "' was found by name, not through ParelShaderLibrary. Add it to the library so builds always include it.");
                }
                if (shader != null && shader.isSupported)
                {
                    resolvedRole = r;
                    return shader;
                }
                current = ParelShaderNames.Parent(r);
            }
            resolvedRole = role;
            return null;
        }
    }
}
