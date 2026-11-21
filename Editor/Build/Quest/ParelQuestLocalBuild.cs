using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>One line Unity printed while making the Meta / Standalone VR version (or a check found before it).</summary>
    [Serializable]
    public sealed class ParelQuestLogEntry
    {
        /// <summary>"error" or "warning".</summary>
        public string type;
        public string message;
        /// <summary>"shader", "script", "asset" or "build".</summary>
        public string source;
        /// <summary>The shader the message is about, when it is a shader message.</summary>
        public string shader;

        public bool IsError => type == "error";
    }

    [Serializable]
    public sealed class ParelQuestLocalJob
    {
        public string kind;
        public string outputDir;
        public string resultPath;
        public string logPath;
        public long bundleLimitBytes;
        public string sdkVersion;
        public int assetCount;
        public int copiedCount;
        public ParelQuestHints hints;
    }

    [Serializable]
    public sealed class ParelQuestLocalResult
    {
        public bool success;
        public string error;
        public string bundlePath;
        public long bundleBytes;
        public string summary;
        public string rank;
        public int triangleCount;
        public int materialCount;
        public int skinnedMeshCount;
        public double seconds;
        public ParelQuestReport report;
        public List<ParelQuestLogEntry> logs = new List<ParelQuestLogEntry>();

        public int Errors => logs.Count(l => l.IsError);
        public int Warnings => logs.Count(l => !l.IsError);
    }

    /// <summary>What the last Meta / Standalone VR build of a world or avatar said, for the SDK's checks.</summary>
    [Serializable]
    public sealed class ParelQuestLastBuild
    {
        public string kind;
        public string content;
        public string finishedAt;
        public bool success;
        public string error;
        public string summary;
        public long bundleBytes;
        public List<ParelQuestLogEntry> logs = new List<ParelQuestLogEntry>();

        public int Errors => logs.Count(l => l.IsError);
        public int Warnings => logs.Count(l => !l.IsError);
    }

    /// <summary>
    /// The SDK's own Quest bundler. The creator's project never switches to Android: after the PC build, the files the
    /// bundle is made of (the build copy of the scene or avatar and every asset it uses, never code) are copied into a
    /// small project of their own (Library/ParelQuestBuild), and a second Unity, started in the background with
    /// -buildTarget Android, converts and builds the Android bundle there (<see cref="ParelQuestLocalWorker"/>). Only that
    /// content is imported for Android, once: the next build re-imports only the files that changed. Everything Unity
    /// reports while building for Android (shader errors per pass and graphics API, import warnings) comes back into this
    /// Console, as it would in a project switched to Android.
    /// </summary>
    public static class ParelQuestLocalBuild
    {
        public const long WorldLimitBytes = 100L * 1024 * 1024;
        public const long AvatarLimitBytes = 10L * 1024 * 1024;
        /// <summary>Lines the background build prints to say what it is doing.</summary>
        public const string StepPrefix = "[ParelQuestBuild] ";
        public const string ConsolePrefix = "[Meta / Standalone VR] ";
        private const int MaxLogs = 400;

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>The background project the Android bundles are built in.</summary>
        public static string BackgroundProject => Path.Combine(ProjectRoot, "Library", "ParelQuestBuild");

        private static string JobPath => Path.Combine(BackgroundProject, "job.json");

        public static bool AndroidSupportInstalled => BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);

        public static string AndroidSupportMissingMessage =>
            "The Meta / Standalone VR version is built on this PC and needs Unity's Android Build Support module (just the module: the Android SDK, " +
            "NDK and OpenJDK are not needed). Add it in Unity Hub > Installs > " + Application.unityVersion + " > Add Modules > Android Build Support, " +
            "then restart Unity. Your project stays on Windows either way.";

        private static Process _running;

        public static bool IsRunning => _running != null && !_running.HasExited;

        /// <summary>What the running background build is doing (null when none runs).</summary>
        public static string RunningStatus { get; private set; }

        // ── 1. prepare (right after the PC build, while its build copies are on disk) ──────────────────────

        /// <summary>
        /// Copies what the bundle of <paramref name="mainAssets"/> is made of into the background project and writes the job.
        /// Worlds: every scene of the bundle, MainScene first. Avatars: the build copy prefab.
        /// </summary>
        public static ParelQuestLocalJob Prepare(string kind, IList<string> mainAssets, IEnumerable<GameObject> contentRoots, Material skybox = null, IEnumerable<string> extraAssets = null)
        {
            if (!AndroidSupportInstalled) throw new Exception(AndroidSupportMissingMessage);
            if (mainAssets == null || mainAssets.Count == 0 || string.IsNullOrEmpty(mainAssets[0])) throw new ArgumentException("Nothing to build.", nameof(mainAssets));
            if (IsRunning) throw new Exception("A Meta / Standalone VR build is still running. Wait for it to finish, then publish again.");

            var timer = Stopwatch.StartNew();
            string main = mainAssets[0];
            var extras = mainAssets.Skip(1).Concat(extraAssets ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            HashSet<string> assets = ParelQuestSource.Collect(main, skybox, extras, out string skyboxPath);
            foreach (string pipelineAsset in PipelineAssets())
            {
                assets.Add(pipelineAsset);
                foreach (string dependency in AssetDatabase.GetDependencies(pipelineAsset, true)) assets.Add(dependency);
            }

            // Packages are shared with the background project as they are; only the project's own files are copied.
            List<string> files = assets
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !ParelQuestSource.IsCode(p) && File.Exists(Path.Combine(ProjectRoot, p)))
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

            ParelQuestHints hints = ParelQuestSource.BuildHints(kind, main, skyboxPath, files, contentRoots);
            if (kind == ParelQuestSource.WorldKind) hints.scenes = mainAssets.Where(p => !string.IsNullOrEmpty(p)).ToList();

            string dir = BackgroundProject;
            Directory.CreateDirectory(dir);
            SyncSettings(dir);
            SyncPackages(dir);
            int copied = SyncAssets(dir, files);

            string output = Path.Combine(dir, "Output");
            Directory.CreateDirectory(output);
            var job = new ParelQuestLocalJob
            {
                kind = kind,
                outputDir = output,
                resultPath = Path.Combine(dir, "result.json"),
                logPath = Path.Combine(dir, "build.log"),
                bundleLimitBytes = kind == ParelQuestSource.AvatarKind ? AvatarLimitBytes : WorldLimitBytes,
                sdkVersion = ParelQuestSource.SdkVersion(),
                assetCount = files.Count,
                copiedCount = copied,
                hints = hints,
            };
            File.WriteAllText(JobPath, JsonUtility.ToJson(job, true));
            Debug.Log(ConsolePrefix + $"Ready to build: {files.Count} file(s), {copied} new or changed since the last Meta / Standalone VR build ({timer.ElapsedMilliseconds} ms). " +
                      "Your project stays on Windows; the Android build runs in the background.");
            return job;
        }

        /// <summary>The project's URP / HDRP assets (default and per quality level) and pipeline global settings.</summary>
        private static IEnumerable<string> PipelineAssets()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            void Add(UnityEngine.Object o)
            {
                string p = o != null ? AssetDatabase.GetAssetPath(o) : null;
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }
            Add(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline);
            for (int i = 0; i < QualitySettings.names.Length; i++) Add(QualitySettings.GetRenderPipelineAssetAt(i));
            foreach (string type in new[] { "t:RenderPipelineGlobalSettings", "t:UniversalRenderPipelineGlobalSettings" })
                foreach (string guid in AssetDatabase.FindAssets(type, new[] { "Assets" }))
                    paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths;
        }

        // ── project, packages and files of the background project ────────────────────────────────────

        private static void SyncSettings(string dir)
        {
            string source = Path.Combine(ProjectRoot, "ProjectSettings");
            string target = Path.Combine(dir, "ProjectSettings");
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                CopyIfChanged(file, Path.Combine(target, file.Substring(source.Length + 1)));
        }

        /// <summary>
        /// The background project's manifest: every package of this project at the same version. Registry and built-in
        /// packages by version; git, local and embedded packages by path, read where this project has them.
        /// </summary>
        private static void SyncPackages(string dir)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"dependencies\": {\n");
            var entries = new List<string>();
            foreach (UnityEditor.PackageManager.PackageInfo info in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                         .OrderBy(p => p.name, StringComparer.Ordinal))
            {
                string value;
                switch (info.source)
                {
                    case UnityEditor.PackageManager.PackageSource.Registry:
                    case UnityEditor.PackageManager.PackageSource.BuiltIn:
                        value = info.version;
                        break;
                    default:
                        if (string.IsNullOrEmpty(info.resolvedPath) || !Directory.Exists(info.resolvedPath)) continue;
                        value = "file:" + Path.GetFullPath(info.resolvedPath).Replace('\\', '/');
                        break;
                }
                entries.Add("    " + Quote(info.name) + ": " + Quote(value));
            }
            sb.Append(string.Join(",\n", entries)).Append("\n  }");
            string scoped = ScopedRegistries(Path.Combine(ProjectRoot, "Packages", "manifest.json"));
            if (!string.IsNullOrEmpty(scoped)) sb.Append(",\n  \"scopedRegistries\": ").Append(scoped);
            sb.Append("\n}\n");

            string manifest = Path.Combine(dir, "Packages", "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifest));
            string text = sb.ToString();
            if (!File.Exists(manifest) || File.ReadAllText(manifest) != text) File.WriteAllText(manifest, text);
        }

        /// <summary>The "scopedRegistries" array of a manifest, as it is written there, or null.</summary>
        private static string ScopedRegistries(string manifestPath)
        {
            if (!File.Exists(manifestPath)) return null;
            string text = File.ReadAllText(manifestPath);
            int key = text.IndexOf("\"scopedRegistries\"", StringComparison.Ordinal);
            if (key < 0) return null;
            int start = text.IndexOf('[', key);
            if (start < 0) return null;
            int depth = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            return null;
        }

        /// <summary>
        /// Puts exactly <paramref name="files"/> (with their .meta files and their folders' .meta files) under the background
        /// project's Assets. Unchanged files are left alone, so Unity re-imports only what changed; files the bundle no longer
        /// uses are removed. Files the last Quest build converted there differ from yours, so they are copied again.
        /// </summary>
        private static int SyncAssets(string dir, List<string> files)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int copied = 0;
            void Want(string relative)
            {
                if (!wanted.Add(relative)) return;
                string source = Path.Combine(ProjectRoot, relative);
                if (File.Exists(source) && CopyIfChanged(source, Path.Combine(dir, relative))) copied++;
            }
            foreach (string file in files)
            {
                Want(file);
                Want(file + ".meta");
                for (string folder = ParentOf(file); folder != null && folder != "Assets"; folder = ParentOf(folder)) Want(folder + ".meta");
            }

            string assets = Path.Combine(dir, "Assets");
            Directory.CreateDirectory(assets);
            foreach (string path in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
            {
                string relative = "Assets/" + path.Substring(assets.Length + 1).Replace('\\', '/');
                if (wanted.Contains(relative)) continue;
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                catch (IOException) { }
            }
            foreach (string folder in Directory.GetDirectories(assets, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            return copied;
        }

        private static string ParentOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : null;
        }

        /// <summary>Copies when the target is missing or differs in size or time; true when it copied.</summary>
        private static bool CopyIfChanged(string source, string target)
        {
            var from = new FileInfo(source);
            var to = new FileInfo(target);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (to.Exists) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(source, target, true);
            File.SetAttributes(target, FileAttributes.Normal);
            File.SetLastWriteTimeUtc(target, from.LastWriteTimeUtc);
            return true;
        }

        private static string Quote(string s) => "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ── 2. run the background Unity ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts Unity in the background on the prepared job and waits for it without blocking the editor.
        /// <paramref name="status"/> is told what it is doing. Never throws for a failed build: the result says why.
        /// </summary>
        /// <summary><see cref="RunAsync"/>, then <see cref="Report"/>: the Console gets what the Android build said.</summary>
        public static async Task<ParelQuestLocalResult> BuildAsync(ParelQuestLocalJob job, string content, Action<string> status, CancellationToken ct)
        {
            try
            {
                ParelQuestLocalResult result = await RunAsync(job, text =>
                {
                    RunningStatus = text;
                    status?.Invoke(text);
                }, ct);
                Report(job, result, content);
                return result;
            }
            finally
            {
                RunningStatus = null;
            }
        }

        public static async Task<ParelQuestLocalResult> RunAsync(ParelQuestLocalJob job, Action<string> status, CancellationToken ct)
        {
            var timer = Stopwatch.StartNew();
            if (File.Exists(job.resultPath)) File.Delete(job.resultPath);
            if (File.Exists(job.logPath)) File.Delete(job.logPath);
            string unity = UnityExecutable();
            string args = "-batchmode -nographics -buildTarget Android -projectPath " + Arg(BackgroundProject) +
                          " -executeMethod ParelVR.SDK.Build.Quest.ParelQuestLocalWorker.Run -parelJob " + Arg(JobPath) +
                          " -logFile " + Arg(job.logPath) +
                          // Half the cores for its jobs: it ran every core at 100% beside the creator's own editor (11-21).
                          " -job-worker-count " + Math.Max(1, Environment.ProcessorCount / 2);
            var start = new ProcessStartInfo(unity, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = BackgroundProject };

            status?.Invoke("Starting Unity for Android in the background...");
            Process process;
            try
            {
                process = Process.Start(start);
                // Below normal: the PC stays usable while it builds. Its shader compilers start from it and keep the
                // same priority on Windows.
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                return Failed("Couldn't start Unity for the Android build (" + unity + "): " + ex.Message, timer);
            }
            _running = process;
            EditorApplication.quitting += KillRunning;

            var importLogs = new List<ParelQuestLogEntry>();
            long position = 0;
            int imported = 0;
            string lastStep = null;
            try
            {
                while (!process.HasExited)
                {
                    if (ct.IsCancellationRequested)
                    {
                        KillRunning();
                        ct.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(500);
                    foreach (string line in ReadNewLines(job.logPath, ref position))
                    {
                        if (line.StartsWith(StepPrefix, StringComparison.Ordinal)) lastStep = line.Substring(StepPrefix.Length).Trim();
                        else if (line.StartsWith("Start importing ", StringComparison.Ordinal)) imported++;
                        ParelQuestLogEntry entry = ParseLogLine(line);
                        if (entry != null && importLogs.Count < MaxLogs) importLogs.Add(entry);
                    }
                    status?.Invoke(lastStep ?? (imported > 0
                        ? $"Getting the files ready for Android... {imported} imported (the first build takes longest; later ones only redo what changed)"
                        : "Opening the background Android project..."));
                }
                foreach (string line in ReadNewLines(job.logPath, ref position))
                {
                    ParelQuestLogEntry entry = ParseLogLine(line);
                    if (entry != null && importLogs.Count < MaxLogs) importLogs.Add(entry);
                }
            }
            finally
            {
                EditorApplication.quitting -= KillRunning;
                if (_running == process) _running = null;
            }

            ParelQuestLocalResult result = null;
            if (File.Exists(job.resultPath))
            {
                try { result = JsonUtility.FromJson<ParelQuestLocalResult>(File.ReadAllText(job.resultPath)); }
                catch (Exception ex) { result = Failed("The Android build's result couldn't be read: " + ex.Message, timer); }
            }
            if (result == null) result = Failed(WhyNoResult(job.logPath, process.ExitCode), timer);
            result.logs = Merge(importLogs, result.logs);
            result.seconds = timer.Elapsed.TotalSeconds;
            return result;
        }

        private static ParelQuestLocalResult Failed(string error, Stopwatch timer) =>
            new ParelQuestLocalResult { success = false, error = error, seconds = timer.Elapsed.TotalSeconds };

        private static void KillRunning()
        {
            try
            {
                if (_running != null && !_running.HasExited) _running.Kill();
            }
            catch (Exception) { }
        }

        private static string UnityExecutable()
        {
            string path = EditorApplication.applicationPath;
            if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(path, "Contents", "MacOS", "Unity");
            return path;
        }

        private static string Arg(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static IEnumerable<string> ReadNewLines(string path, ref long position)
        {
            var lines = new List<string>();
            if (!File.Exists(path)) return lines;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < position) position = 0;
                    stream.Seek(position, SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream))
                    {
                        string text = reader.ReadToEnd();
                        int end = text.LastIndexOf('\n');
                        if (end < 0) return lines;
                        string complete = text.Substring(0, end + 1);
                        position += Encoding.UTF8.GetByteCount(complete);
                        lines.AddRange(complete.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0));
                    }
                }
            }
            catch (IOException) { }
            return lines;
        }

        private static readonly Regex ShaderLine = new Regex(@"^Shader (error|warning) in '([^']+)': (.+)$");
        private static readonly Regex ScriptLine = new Regex(@"^(.+\.cs)\((\d+),(\d+)\): (error|warning) (CS\d+): (.+)$");

        /// <summary>A Unity log line as an issue: shader compile messages and script compile messages, as the Console shows them.</summary>
        public static ParelQuestLogEntry ParseLogLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            Match shader = ShaderLine.Match(line);
            if (shader.Success)
                return new ParelQuestLogEntry { type = shader.Groups[1].Value, message = line, source = "shader", shader = shader.Groups[2].Value };
            Match script = ScriptLine.Match(line);
            if (script.Success && !line.Contains("/PackageCache/"))
                return new ParelQuestLogEntry { type = script.Groups[4].Value, message = line, source = "script" };
            return null;
        }

        private static List<ParelQuestLogEntry> Merge(IEnumerable<ParelQuestLogEntry> first, IEnumerable<ParelQuestLogEntry> second)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<ParelQuestLogEntry>();
            foreach (ParelQuestLogEntry e in (first ?? Enumerable.Empty<ParelQuestLogEntry>()).Concat(second ?? Enumerable.Empty<ParelQuestLogEntry>()))
            {
                if (e == null || string.IsNullOrEmpty(e.message) || !seen.Add(e.type + "|" + e.message)) continue;
                if (all.Count >= MaxLogs) break;
                all.Add(e);
            }
            return all;
        }

        /// <summary>Why Unity stopped without a result, from its log.</summary>
        private static string WhyNoResult(string logPath, int exitCode)
        {
            string log = string.Empty;
            try
            {
                if (File.Exists(logPath))
                    using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream))
                        log = reader.ReadToEnd();
            }
            catch (IOException) { }
            if (log.Contains("Multiple Unity instances cannot open the same project"))
                return "Another Unity already has the Meta / Standalone VR build project open. Close it (or wait for it), then publish again.";
            if (log.IndexOf("No valid Unity Editor license", StringComparison.OrdinalIgnoreCase) >= 0 || log.IndexOf("license is not valid", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Unity's license check failed for the background Android build. Sign in to Unity Hub, then publish again.";
            string[] compile = log.Split('\n').Where(l => Regex.IsMatch(l, @"error CS\d+")).Take(3).Select(l => l.Trim()).ToArray();
            if (compile.Length > 0)
                return "The packages of this project don't compile for Android, so the Meta / Standalone VR build couldn't start: " + string.Join(" | ", compile);
            return "Unity stopped before the Android build finished (exit code " + exitCode + "). Its log: " + logPath;
        }

        // ── 3. tell the creator ───────────────────────────────────────────────────────────────────────

        private static string LastBuildPath(string kind) => Path.Combine(ProjectRoot, "Library", "ParelQuestBuild-last-" + kind + ".json");

        /// <summary>
        /// Prints what the Android build said into this Console (errors as errors, warnings as warnings, as an Android
        /// project's Console would show them) and keeps it for the SDK's checks.
        /// </summary>
        public static void Report(ParelQuestLocalJob job, ParelQuestLocalResult result, string content)
        {
            foreach (ParelQuestLogEntry entry in result.logs)
            {
                UnityEngine.Object context = !string.IsNullOrEmpty(entry.shader) ? Shader.Find(entry.shader) : null;
                if (entry.IsError) Debug.LogError(ConsolePrefix + entry.message, context);
                else Debug.LogWarning(ConsolePrefix + entry.message, context);
            }
            foreach (string note in result.report?.notes ?? new List<string>()) Debug.Log(ConsolePrefix + note);
            string counts = $"{result.Errors} error(s), {result.Warnings} warning(s) from Unity's Android build";
            if (result.success)
                Debug.Log(ConsolePrefix + $"Built in {result.seconds:0}s: {EditorUtility.FormatBytes(result.bundleBytes)}. {result.summary} {counts}.");
            else
                Debug.LogError(ConsolePrefix + "Not built: " + result.error + " (" + counts + ")");

            var last = new ParelQuestLastBuild
            {
                kind = job?.kind ?? string.Empty,
                content = content ?? string.Empty,
                finishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                success = result.success,
                error = result.error ?? string.Empty,
                summary = result.summary ?? string.Empty,
                bundleBytes = result.bundleBytes,
                logs = result.logs,
            };
            try { File.WriteAllText(LastBuildPath(last.kind), JsonUtility.ToJson(last, true)); }
            catch (IOException) { }
        }

        /// <summary>The last Meta / Standalone VR build of this kind ("world" / "avatar") made in this project, or null.</summary>
        public static ParelQuestLastBuild LastBuild(string kind)
        {
            string path = LastBuildPath(kind);
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<ParelQuestLastBuild>(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }

        /// <summary>The Builder's line about the Meta / Standalone VR version of <paramref name="content"/>; bad when it failed.</summary>
        public static string StatusLine(string kind, string content, out bool bad)
        {
            bad = false;
            if (IsRunning) return "Meta / Standalone VR version: " + (RunningStatus ?? "building in the background...");
            if (!AndroidSupportInstalled)
            {
                bad = true;
                return AndroidSupportMissingMessage;
            }
            ParelQuestLastBuild last = LastBuild(kind);
            if (last == null || (!string.IsNullOrEmpty(content) && last.content != content))
                return "The Meta / Standalone VR version is built on this PC after the PC build, in the background: this project stays on Windows.";
            if (!last.success)
            {
                bad = true;
                return $"The last Meta / Standalone VR build ({last.finishedAt}) failed: {last.error}";
            }
            string counts = last.Errors + last.Warnings == 0 ? "no errors or warnings" : $"{last.Errors} error(s), {last.Warnings} warning(s) (see Validation)";
            return $"Meta / Standalone VR version last built {last.finishedAt}: {EditorUtility.FormatBytes(last.bundleBytes)}, {counts}.";
        }

        [MenuItem("ParelVR/Meta - Standalone VR/Show Last Android Build Log", priority = 2100)]
        private static void ShowLog()
        {
            string log = Path.Combine(BackgroundProject, "build.log");
            if (File.Exists(log)) EditorUtility.RevealInFinder(log);
            else EditorUtility.DisplayDialog("Meta / Standalone VR", "No Meta / Standalone VR build has run in this project yet.", "OK");
        }

        [MenuItem("ParelVR/Meta - Standalone VR/Clear Android Build Cache", priority = 2101)]
        private static void ClearCache()
        {
            if (IsRunning)
            {
                EditorUtility.DisplayDialog("Meta / Standalone VR", "A Meta / Standalone VR build is running. Wait for it to finish first.", "OK");
                return;
            }
            if (!Directory.Exists(BackgroundProject)) return;
            if (!EditorUtility.DisplayDialog("Clear Android Build Cache",
                    "Deletes the background project the Meta / Standalone VR versions are built in (" + BackgroundProject + "). The next Quest build " +
                    "imports everything for Android again, which takes longer. Your project isn't touched.", "Delete", "Cancel"))
                return;
            try { Directory.Delete(BackgroundProject, true); }
            catch (Exception ex) { EditorUtility.DisplayDialog("Clear Android Build Cache", "Couldn't delete it: " + ex.Message, "OK"); }
        }
    }
}
