using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Checks every player build for what PVRP's pink-material protection depends on: the shader library and its
    /// variant collection are in Resources, every library shader compiles for the target, and ParelVR variants survive
    /// every stripper in the project. It only reports (Console + Logs/PVRP/BuildShaders.txt); it never stops a build or
    /// removes a variant.
    /// </summary>
    public sealed class ParelBuildShaderCheck : IPreprocessBuildWithReport, IPreprocessShaders, IPostprocessBuildWithReport
    {
        public const string LogPath = "Logs/PVRP/BuildShaders.txt";
        private static readonly Dictionary<string, int> Kept = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Emptied = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<string> Findings = new List<string>();
        private static bool _active;

        // Runs after every other stripper, so the counts are what actually ships.
        public int callbackOrder => int.MaxValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            Kept.Clear();
            Emptied.Clear();
            Findings.Clear();
            _active = true;
            foreach (string finding in Check(report.summary.platform)) Findings.Add(finding);
            foreach (string f in Findings) Debug.LogWarning(RenderingLog.Prefix + f);
        }

        /// <summary>The problems a build for <paramref name="target"/> would have, in plain words. Empty when all is well.</summary>
        public static List<string> Check(BuildTarget target)
        {
            var findings = new List<string>();
            var library = AssetDatabase.LoadAssetAtPath<ParelShaderLibrary>(ParelShaderLibraryBuilder.AssetPath);
            if (library == null)
            {
                findings.Add("The ParelVR shader library is missing (" + ParelShaderLibraryBuilder.AssetPath + "). Run ParelVR > Rendering > Rebuild Shader Library, or broken materials can't be repaired in this build.");
                return findings;
            }
            foreach (ParelShaderLibrary.Entry e in library.Entries)
            {
                if (e == null) continue;
                if (e.Pc == null) findings.Add("The shader library has no shader for " + e.Role + ".");
                foreach (Shader s in new[] { e.Pc, e.Quest })
                    if (s != null && ShaderUtil.ShaderHasError(s)) findings.Add(s.name + " has compile errors; materials converted to it would render magenta.");
            }
            if (library.Variants == null || library.Variants.variantCount == 0)
                findings.Add("The shader library has no variant collection, so variants used only by runtime conversion can be stripped. Run ParelVR > Rendering > Rebuild Shader Library.");

// Graphics settings keep these in m_LightmapStripping / m_FogStripping (0 = automatic, 1 = custom).
            var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            if (graphics.FindProperty("m_LightmapStripping")?.intValue == 0)
                findings.Add("Lightmap variants are stripped automatically (Graphics settings), from the scenes in this build. ParelVR materials made at runtime in a lightmapped world need those variants; a stripped one renders without its baked light. Setting lightmap stripping to Custom with every mode kept avoids this (a project settings change: ask before making it).");
            if (graphics.FindProperty("m_FogStripping")?.intValue == 0)
                findings.Add("Fog variants are stripped automatically (Graphics settings), from the scenes in this build. ParelVR materials made at runtime in a world with fog modes the build's scenes don't use show no fog.");
            return findings;
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!_active || shader == null) return;
            string name = shader.name;
            if (!name.StartsWith("ParelVR/", StringComparison.Ordinal)) return;
            string key = name + " / " + snippet.passName + " (" + snippet.shaderType + ")";
            Kept.TryGetValue(key, out int n);
            Kept[key] = n + data.Count;
            if (data.Count == 0)
            {
                Emptied.TryGetValue(key, out int e);
                Emptied[key] = e + 1;
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!_active) return;
            _active = false;
            var sb = new StringBuilder();
            sb.Append("PVRP build shader check — ").Append(report.summary.platform).Append(", ").Append(report.summary.result).Append(", ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            if (Findings.Count == 0) sb.AppendLine("Library, variant collection and stripping settings: no problems.");
            foreach (string f in Findings) sb.Append("! ").AppendLine(f);
            sb.AppendLine().AppendLine("ParelVR variants kept in the build (after every stripper):");
            int total = 0;
            var keys = new List<string>(Kept.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                total += Kept[k];
                sb.Append("  ").Append(Kept[k].ToString().PadLeft(6)).Append("  ").AppendLine(k);
            }
            sb.Append("  ").Append(total.ToString().PadLeft(6)).AppendLine("  total");
            foreach (KeyValuePair<string, int> e in Emptied)
                sb.Append("! another stripper removed every variant of a snippet: ").AppendLine(e.Key);
            if (keys.Count == 0) sb.AppendLine("! No ParelVR shader was compiled into this build.");

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.WriteAllText(LogPath, sb.ToString());
            Debug.Log(RenderingLog.Prefix + "Build kept " + total + " ParelVR shader variants across " + keys.Count + " snippets; details in " + LogPath);
        }
    }
}
