using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>
    /// The Meta / Standalone VR checks of the SDK's validation, run on PC without switching the project: which materials
    /// get headset shaders and why, shaders with no URP pass, each kept shader pass compiled for the headset (Vulkan,
    /// Android) with the compiler's own messages, components whose scripts don't run in a bundle, and everything the last
    /// Android build printed. Nothing here changes the project.
    /// </summary>
    public static class ParelQuestValidation
    {
        public const string Tag = "Meta / Standalone VR: ";
        private const int MaxListed = 25;
        private const double CompileBudgetSeconds = 12;

        /// <summary>Set by the Builder from its Meta / Standalone VR toggle: off, no Quest checks.</summary>
        public static bool Wanted = true;

        // shader asset + keywords + pass -> compiler messages (empty: compiled fine). Kept for the editor session.
        private static readonly Dictionary<string, List<string>> CompileCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <param name="content">The world's scene path or the avatar's name: the last Quest build is listed when it was of this.</param>
        /// <param name="projectWide">Also the checks that are the same for everything in the project (Android module, scripts).</param>
        public static void AddIssues(ValidationReport report, IEnumerable<GameObject> roots, bool avatar, Material skybox = null, string content = null, bool projectWide = true)
        {
            if (!Wanted) return;
            List<GameObject> list = (roots ?? Enumerable.Empty<GameObject>()).Where(r => r != null).ToList();
            if (projectWide)
            {
                if (!ParelQuestLocalBuild.AndroidSupportInstalled) report.AddWarning(Tag + ParelQuestLocalBuild.AndroidSupportMissingMessage);
                else report.AddInfo(Tag + "built on this PC in the background after the PC build; this project stays on Windows.");
            }

            bool urp = ParelQuestShaders.UrpProject;
            var particles = new HashSet<Material>();
            var materials = new List<Material>();
            foreach (GameObject root in list)
            {
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    bool particle = r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer;
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        materials.Add(m);
                        if (particle) particles.Add(m);
                    }
                    if (r is ParticleSystemRenderer p && p.trailMaterial != null)
                    {
                        materials.Add(p.trailMaterial);
                        particles.Add(p.trailMaterial);
                    }
                }
                foreach (Terrain t in root.GetComponentsInChildren<Terrain>(true))
                    if (t.materialTemplate != null) materials.Add(t.materialTemplate);
            }

            var kept = new List<Material>();
            int swapped = 0, listed = 0;
            foreach (Material m in materials.Distinct())
            {
                string shaderName = m.shader != null ? m.shader.name : null;
                string problem = ParelQuestShaders.Problem(m.shader);
                string target = ParelQuestShaders.Replacement(shaderName, problem, avatar, particles.Contains(m), urp);
                if (target == null)
                {
                    kept.Add(m);
                    continue;
                }
                swapped++;
                if (listed++ >= MaxListed) continue;
                string message = $"{Tag}'{m.name}' ({shaderName ?? "no shader"}) becomes {target} on the headset: {ParelQuestShaders.ReasonFor(shaderName, problem, avatar)}.";
                if (problem != null) report.AddWarning(message, null, m);
                else report.AddInfo(message, null, m);
            }
            if (swapped > listed) report.AddInfo($"{Tag}{swapped - listed} more material(s) get headset shaders (the Console lists them all after the Quest build).");
            if (skybox != null && ParelQuestShaders.Problem(skybox.shader) != null)
                report.AddWarning($"{Tag}the skybox '{skybox.name}' ({skybox.shader?.name}) {ParelQuestShaders.Problem(skybox.shader)}; the headset gets a plain cubemap / panorama sky.", null, skybox);

            if (urp)
                foreach (IGrouping<Shader, Material> group in kept.Where(m => m.shader != null).GroupBy(m => m.shader))
                {
                    string modes = PassModes(group.Key);
                    if (modes != null)
                        report.AddWarning($"{Tag}the shader '{group.Key.name}' has no URP pass (its passes are {modes}). ParelVR draws a simple stand-in for " +
                                          $"{string.Join(", ", group.Take(3).Select(m => "'" + m.name + "'"))}{(group.Count() > 3 ? "..." : "")} on every platform. Give it a UniversalForward pass or use a URP shader.", null, group.Key);
                }

            CompileForHeadset(report, kept);
            if (!avatar) AddScriptComponents(report, list);
            if (projectWide) AddScriptCompile(report);
            AddLastBuild(report, avatar ? ParelQuestSource.AvatarKind : ParelQuestSource.WorldKind, content);
        }

        /// <summary>The project's scripts compiled for Android: what an Android project's Console would show.</summary>
        private static void AddScriptCompile(ValidationReport report)
        {
            List<ParelQuestLogEntry> entries = CompileScriptsForAndroid();
            List<ParelQuestLogEntry> errors = entries.Where(e => e.IsError).ToList();
            foreach (ParelQuestLogEntry error in errors.Take(MaxListed))
                report.AddWarning($"{Tag}script error compiling for Android: {error.message}");
            int warnings = entries.Count - errors.Count;
            if (errors.Count > 0)
                report.AddInfo(Tag + "bundles carry no C# code (world logic runs as Volt scripts), so these errors don't stop the Quest upload. They are what this project would show switched to Android; " +
                               "code under #if UNITY_STANDALONE_WIN or Windows-only plugins are the usual cause.");
            if (warnings > 0)
                report.AddInfo($"{Tag}{warnings} script warning(s) compiling for Android (ParelVR > Meta - Standalone VR > Check Scripts for Android lists them in the Console).");
        }

        /// <summary>The LightMode tags of a shader that URP can't draw, or null when one of its passes is a URP pass.</summary>
        private static string PassModes(Shader shader)
        {
            var lightMode = new ShaderTagId("LightMode");
            var modes = new List<string>();
            for (int i = 0; i < shader.passCount; i++)
            {
                string mode = shader.FindPassTagValue(i, lightMode).name;
                if (string.IsNullOrEmpty(mode) || mode == "SRPDefaultUnlit" || mode == "UniversalForward" || mode == "UniversalForwardOnly" || mode == "UniversalGBuffer" || mode == "Universal2D")
                    return null;
                if (!modes.Contains(mode)) modes.Add(mode);
            }
            return modes.Count == 0 ? null : string.Join(", ", modes);
        }

        /// <summary>
        /// Compiles every pass of each kept shader for the headset (Vulkan on Android) with its materials' keywords and
        /// reports the compiler's errors with the pass name, as an Android project's Console would. Unity's own shaders are
        /// skipped; the Quest build compiles everything anyway.
        /// </summary>
        private static void CompileForHeadset(ValidationReport report, List<Material> kept)
        {
            var timer = Stopwatch.StartNew();
            bool outOfTime = false;
            foreach (IGrouping<Shader, Material> group in kept.Where(m => m.shader != null).GroupBy(m => m.shader))
            {
                Shader shader = group.Key;
                string path = AssetDatabase.GetAssetPath(shader);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/com.unity.", StringComparison.Ordinal) || shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                ShaderData data = ShaderUtil.GetShaderData(shader);
                ShaderData.Subshader sub = data?.ActiveSubshader ?? (data != null && data.SubshaderCount > 0 ? data.GetSubshader(0) : null);
                if (sub == null) continue;
                string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                foreach (string[] keywords in group.Select(m => m.shaderKeywords ?? Array.Empty<string>()).GroupBy(k => string.Join(" ", k.OrderBy(x => x))).Select(g => g.First()).Take(3))
                {
                    for (int p = 0; p < sub.PassCount; p++)
                    {
                        ShaderData.Pass pass = sub.GetPass(p);
                        string key = hash + "|" + string.Join(" ", keywords.OrderBy(x => x)) + "|" + p;
                        if (!CompileCache.TryGetValue(key, out List<string> errors))
                        {
                            if (timer.Elapsed.TotalSeconds > CompileBudgetSeconds)
                            {
                                outOfTime = true;
                                continue;
                            }
                            errors = new List<string>();
                            try
                            {
                                ShaderData.VariantCompileInfo info = pass.CompileVariant(ShaderType.Vertex, keywords, ShaderCompilerPlatform.Vulkan, BuildTarget.Android);
                                if (!info.Success)
                                    foreach (ShaderMessage message in info.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error).Take(3))
                                        errors.Add(message.message + (string.IsNullOrEmpty(message.file) ? string.Empty : $" ({message.file}:{message.line})"));
                            }
                            catch (Exception ex)
                            {
                                errors.Add("couldn't be compiled here: " + ex.Message);
                            }
                            CompileCache[key] = errors;
                        }
                        foreach (string error in errors)
                            report.AddWarning($"{Tag}the shader '{shader.name}', pass '{(string.IsNullOrEmpty(pass.Name) ? "#" + p : pass.Name)}' doesn't compile for the headset (Vulkan): {error}", null, shader);
                    }
                }
            }
            if (outOfTime) report.AddInfo(Tag + "more shader passes than this check has time for; the Quest build compiles all of them and lists any errors in the Console.");
        }

        /// <summary>Components whose scripts are this project's own C#: bundles carry no code, so they don't run in ParelVR (Volt scripts do).</summary>
        private static void AddScriptComponents(ValidationReport report, List<GameObject> roots)
        {
            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (GameObject root in roots)
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || IsVolt(behaviour.GetType())) continue;
                    MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
                    string path = script != null ? AssetDatabase.GetAssetPath(script) : null;
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                    if (!found.TryGetValue(path, out List<string> objects)) found[path] = objects = new List<string>();
                    if (objects.Count < 4) objects.Add(behaviour.gameObject.name);
                }
            foreach (KeyValuePair<string, List<string>> pair in found.Take(MaxListed))
                report.AddWarning($"{Tag}{Path.GetFileNameWithoutExtension(pair.Key)} ({pair.Key}) on {string.Join(", ", pair.Value.Select(o => "'" + o + "'"))}: " +
                                  "world bundles carry no C# code, so this script doesn't run in ParelVR on the headset (nor on PC). Write world logic as Volt scripts (VoltBehaviour).",
                    null, AssetDatabase.LoadAssetAtPath<MonoScript>(pair.Key));
        }

        private static bool IsVolt(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.Name == "VoltBehaviour") return true;
            return false;
        }

        /// <summary>What the last Meta / Standalone VR build said: its errors and warnings, as Unity printed them for Android.</summary>
        public static void AddLastBuild(ValidationReport report, string kind, string content = null)
        {
            ParelQuestLastBuild last = ParelQuestLocalBuild.LastBuild(kind);
            if (last == null || (!string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(last.content) && last.content != content)) return;
            string when = string.IsNullOrEmpty(last.finishedAt) ? string.Empty : " (" + last.finishedAt + ")";
            if (!last.success) report.AddWarning($"{Tag}the last Quest build{when} failed: {last.error}");
            int shown = 0;
            foreach (ParelQuestLogEntry entry in last.logs.OrderByDescending(l => l.IsError))
            {
                if (shown++ >= MaxListed) break;
                report.AddWarning($"{Tag}last Quest build{when}, {entry.source} {entry.type}: {entry.message}");
            }
            if (last.logs.Count > MaxListed) report.AddInfo($"{Tag}{last.logs.Count - MaxListed} more message(s) from the last Quest build (ParelVR > Meta - Standalone VR > Show Last Android Build Log).");
        }

        // ── scripts compiled for Android ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Compiles the project's player scripts for Android, as switching the project to Android would, without
        /// switching: Unity's compiler messages (errors and warnings with file and line) go to the Console and are returned.
        /// Skipped when no script changed since the last check.
        /// </summary>
        public static List<ParelQuestLogEntry> CompileScriptsForAndroid(bool force = false)
        {
            var entries = new List<ParelQuestLogEntry>();
            if (!ParelQuestLocalBuild.AndroidSupportInstalled) return entries;
            string fingerprint = ScriptFingerprint();
            string cachePath = Path.Combine(ParelQuestLocalBuild.ProjectRoot, "Library", "ParelQuestBuild-scripts.json");
            if (!force && File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonUtility.FromJson<ScriptCheck>(File.ReadAllText(cachePath));
                    if (cached != null && cached.fingerprint == fingerprint) return cached.entries ?? entries;
                }
                catch (Exception) { }
            }

            string output = Path.Combine(ParelQuestLocalBuild.ProjectRoot, "Temp", "ParelQuestScripts");
            void Capture(string message, string stack, LogType type)
            {
                if (type == LogType.Log) return;
                ParelQuestLogEntry entry = ParelQuestLocalBuild.ParseLogLine(message.Split('\n')[0]);
                if (entry != null && entry.source == "script" && entries.Count < 200) entries.Add(entry);
            }
            Application.logMessageReceived += Capture;
            try
            {
                Directory.CreateDirectory(output);
                var settings = new ScriptCompilationSettings { target = BuildTarget.Android, group = BuildTargetGroup.Android, options = ScriptCompilationOptions.None };
                ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, output);
                if ((result.assemblies == null || result.assemblies.Count == 0) && !entries.Any(e => e.IsError))
                    entries.Add(new ParelQuestLogEntry { type = "error", source = "script", message = "This project's scripts didn't compile for Android (see the Console)." });
            }
            catch (Exception ex)
            {
                entries.Add(new ParelQuestLogEntry { type = "warning", source = "script", message = "The Android script check couldn't run: " + ex.Message });
            }
            finally
            {
                Application.logMessageReceived -= Capture;
                try { if (Directory.Exists(output)) Directory.Delete(output, true); }
                catch (IOException) { }
            }
            try { File.WriteAllText(cachePath, JsonUtility.ToJson(new ScriptCheck { fingerprint = fingerprint, entries = entries }, true)); }
            catch (IOException) { }
            return entries;
        }

        [Serializable]
        private sealed class ScriptCheck
        {
            public string fingerprint;
            public List<ParelQuestLogEntry> entries = new List<ParelQuestLogEntry>();
        }

        /// <summary>Changes when a script, assembly definition or package of the project changes.</summary>
        private static string ScriptFingerprint()
        {
            long ticks = 0;
            int count = 0;
            foreach (UnityEditor.Compilation.Assembly assembly in UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies))
                foreach (string file in assembly.sourceFiles)
                {
                    count++;
                    try { ticks ^= File.GetLastWriteTimeUtc(file).Ticks + file.Length; }
                    catch (IOException) { }
                }
            string packages = Path.Combine(ParelQuestLocalBuild.ProjectRoot, "Packages", "packages-lock.json");
            long lockTicks = File.Exists(packages) ? File.GetLastWriteTimeUtc(packages).Ticks : 0;
            return count + ":" + ticks + ":" + lockTicks + ":" + Application.unityVersion;
        }

        [MenuItem("ParelVR/Meta - Standalone VR/Check Scripts for Android", priority = 2102)]
        private static void CheckScriptsMenu()
        {
            if (!ParelQuestLocalBuild.AndroidSupportInstalled)
            {
                EditorUtility.DisplayDialog("Meta / Standalone VR", ParelQuestLocalBuild.AndroidSupportMissingMessage, "OK");
                return;
            }
            List<ParelQuestLogEntry> entries = CompileScriptsForAndroid(true);
            int errors = entries.Count(e => e.IsError);
            EditorUtility.DisplayDialog("Scripts for Android",
                errors == 0 && entries.Count == 0
                    ? "This project's scripts compile for Android with no errors or warnings."
                    : $"{errors} error(s) and {entries.Count - errors} warning(s) compiling for Android. They're in the Console with their file and line.\n\n" +
                      "World bundles carry no C# code (world logic runs as Volt scripts), so these don't stop a Quest upload; they're what an Android project would show.",
                "OK");
        }
    }
}
