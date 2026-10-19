using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace ParelVR.SDK.Core.Updates
{
    /// <summary>
    /// Finds out which SDK this project has, what the newest one on GitHub is, and installs it.
    ///
    /// A Git install is updated through the Package Manager (pinned to the newest commit). Any other install (a
    /// tarball, or a copy in Packages/) is updated without Git: the newest commit is downloaded as a zip and put in
    /// Packages/com.parelvrsdk.pvr, which Unity uses ahead of any other source of the same package.
    /// </summary>
    public static class ParelSdkUpdater
    {
        public const string PackageName = "com.parelvrsdk.pvr";
        public const string Repo = "ParelVR/ParelVR-SDK";
        public const string RepoUrl = "https://github.com/" + Repo;
        private const string GitUrl = RepoUrl + ".git";
        private const string Branch = "main";
        private const string UserAgent = "ParelVR-SDK-Updater";
        private const string MarkerPath = "ProjectSettings/ParelSdkInstall.json";
        internal const string JustUpdatedKey = "ParelVR.SDK.JustUpdated";

        public enum InstallKind { Git, Tarball, Folder, Managed, Unknown }

        public sealed class Installed
        {
            public string Version = "unknown";
            public string Commit;
            public InstallKind Kind = InstallKind.Unknown;
            public string GitUrl;

            public string Describe()
            {
                switch (Kind)
                {
                    case InstallKind.Git: return "Installed from Git";
                    case InstallKind.Tarball: return "Installed from a tarball";
                    case InstallKind.Folder: return "Installed in Packages";
                    case InstallKind.Managed: return "Part of this project";
                    default: return "Installed";
                }
            }
        }

        public sealed class Latest
        {
            public string Version = "?";
            public string Commit = string.Empty;
            public DateTime Date;
            public string Title = string.Empty;
            public string Notes;
            public string ShortCommit => Commit.Length > 7 ? Commit.Substring(0, 7) : Commit;
        }

        public sealed class CheckResult
        {
            public Installed Installed;
            public Latest Latest;
            public bool UpdateAvailable;
            public string Error;
        }

        [Serializable] private class CommitJson { public string sha; public CommitBody commit; }
        [Serializable] private class CommitBody { public string message; public Person committer; }
        [Serializable] private class Person { public string date; }
        [Serializable] private class PackageJson { public string version; }
        [Serializable] private class Marker { public string commit; public string version; }

        // ------------------------------------------------------------------------------------------ installed

        public static Installed ReadInstalled()
        {
            var installed = new Installed();
            PackageInfo info = PackageInfo.FindForAssembly(typeof(ParelSdkUpdater).Assembly);
            if (info == null) return installed;

            installed.Version = info.version;
            switch (info.source)
            {
                case PackageSource.Git:
                    installed.Kind = InstallKind.Git;
                    installed.Commit = info.git != null ? info.git.hash : null;
                    int at = info.packageId.IndexOf('@');
                    installed.GitUrl = at >= 0 ? StripRevision(info.packageId.Substring(at + 1)) : GitUrl;
                    break;
                case PackageSource.LocalTarball:
                    installed.Kind = InstallKind.Tarball;
                    break;
                case PackageSource.Embedded:
                case PackageSource.Local:
                    // The ParelVR game project builds the Volt assemblies from source and keeps the SDK without them;
                    // a copy from GitHub would bring them back as duplicates, so that project updates it by hand.
                    installed.Kind = Directory.Exists(Path.Combine(info.resolvedPath, "Assemblies")) ? InstallKind.Folder : InstallKind.Managed;
                    break;
            }

            if (installed.Commit == null)
            {
                Marker marker = ReadMarker();
                if (marker != null && marker.version == installed.Version) installed.Commit = marker.commit;
            }
            return installed;
        }

        // ------------------------------------------------------------------------------------------ checking

        public static async Task<CheckResult> CheckAsync()
        {
            var result = new CheckResult { Installed = ReadInstalled() };
            try
            {
                CommitJson head = JsonUtility.FromJson<CommitJson>(await GetTextAsync($"https://api.github.com/repos/{Repo}/commits/{Branch}"));
                if (head == null || string.IsNullOrEmpty(head.sha)) throw new Exception("GitHub did not say what the newest version is.");

                var latest = new Latest { Commit = head.sha, Title = FirstLine(head.commit?.message) };
                if (DateTime.TryParse(head.commit?.committer?.date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime date)) latest.Date = date;
                PackageJson package = JsonUtility.FromJson<PackageJson>(await GetTextAsync(RawUrl(head.sha, "package.json")));
                if (package != null && !string.IsNullOrEmpty(package.version)) latest.Version = package.version;
                try { latest.Notes = NewestChangelogSection(await GetTextAsync(RawUrl(head.sha, "CHANGELOG.md"))); }
                catch (Exception) { latest.Notes = null; }

                result.Latest = latest;
                result.UpdateAvailable = IsNewer(result.Installed, latest);
            }
            catch (Exception e)
            {
                result.Error = e.Message;
            }
            return result;
        }

        private static bool IsNewer(Installed installed, Latest latest)
        {
            if (installed.Kind == InstallKind.Managed) return false;
            int order = CompareVersions(latest.Version, installed.Version);
            if (order != 0) return order > 0;
            // Same version number: a newer commit still counts when this project knows which commit it has.
            return !string.IsNullOrEmpty(installed.Commit) &&
                   !latest.Commit.StartsWith(installed.Commit, StringComparison.OrdinalIgnoreCase) &&
                   !installed.Commit.StartsWith(latest.Commit, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------------------------------ updating

        /// <summary>Installs <paramref name="check"/>'s newest SDK. Unity reloads its scripts once it is in.</summary>
        public static async Task UpdateAsync(CheckResult check, Action<float, string> progress)
        {
            Latest latest = check.Latest;
            SessionState.SetString(JustUpdatedKey, latest.Version);
            try
            {
                if (check.Installed.Kind == InstallKind.Git)
                {
                    progress(0.3f, "Asking the Package Manager for " + latest.Version + "...");
                    AddRequest request = Client.Add((check.Installed.GitUrl ?? GitUrl) + "#" + latest.Commit);
                    while (!request.IsCompleted) await Task.Delay(100);
                    if (request.Status == StatusCode.Failure) throw new Exception(request.Error != null ? request.Error.message : "The Package Manager could not update the SDK.");
                    progress(1f, "Updated. Unity is reloading the SDK...");
                    return;
                }
                await InstallFromZipAsync(latest, progress);
            }
            catch
            {
                SessionState.EraseString(JustUpdatedKey);
                throw;
            }
        }

        private static async Task InstallFromZipAsync(Latest latest, Action<float, string> progress)
        {
            string work = Path.GetFullPath(Path.Combine("Temp", "ParelSdkUpdate"));
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "sdk.zip");

            using (var request = new UnityWebRequest($"https://codeload.github.com/{Repo}/zip/{latest.Commit}", UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerFile(zip) { removeFileOnAbort = true };
                request.SetRequestHeader("User-Agent", UserAgent);
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    progress(0.05f + request.downloadProgress * 0.55f, $"Downloading {latest.Version}... {EditorUtility.FormatBytes((long)request.downloadedBytes)}");
                    await Task.Delay(50);
                }
                if (request.result != UnityWebRequest.Result.Success) throw new Exception("The download failed: " + request.error);
            }

            progress(0.65f, "Unpacking...");
            string extract = Path.Combine(work, "extract");
            await Task.Run(() => Unzip(zip, extract));
            string[] folders = Directory.GetDirectories(extract);
            string source = folders.Length == 1 ? folders[0] : extract;
            if (!File.Exists(Path.Combine(source, "package.json"))) throw new Exception("The download does not look like the ParelVR SDK.");

            progress(0.85f, "Installing...");
            string target = Path.GetFullPath(Path.Combine("Packages", PackageName));
            string previous = Path.Combine(work, "previous");
            if (Directory.Exists(target)) Directory.Move(target, previous);
            try
            {
                Directory.Move(source, target);
            }
            catch
            {
                if (Directory.Exists(previous) && !Directory.Exists(target)) Directory.Move(previous, target);
                throw;
            }

            WriteMarker(latest);
            progress(1f, "Updated. Unity is importing the new SDK...");
            Client.Resolve();
            AssetDatabase.Refresh();
        }

        private static void Unzip(string zip, string destination)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using (FileStream file = File.OpenRead(zip))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // nothing outside the folder
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (Stream input = entry.Open())
                    using (FileStream output = File.Create(path))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static async Task<string> GetTextAsync(string url)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("User-Agent", UserAgent);
                request.timeout = 20;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Delay(30);
                if (request.result == UnityWebRequest.Result.Success) return request.downloadHandler.text;
                if (request.responseCode == 403) throw new Exception("GitHub is limiting update checks from this network right now. Try again in a few minutes.");
                if (request.result == UnityWebRequest.Result.ConnectionError) throw new Exception("Could not reach GitHub. Check your internet connection.");
                throw new Exception("GitHub answered " + request.responseCode + ": " + request.error);
            }
        }

        private static string RawUrl(string commit, string file) => $"https://raw.githubusercontent.com/{Repo}/{commit}/{file}";

        /// <summary>The first "## " section of the changelog (the newest release's notes), heading included.</summary>
        private static string NewestChangelogSection(string changelog)
        {
            if (string.IsNullOrEmpty(changelog)) return null;
            var section = new StringBuilder();
            bool inSection = false;
            foreach (string raw in changelog.Replace("\r", string.Empty).Split('\n'))
            {
                if (raw.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (inSection) break;
                    inSection = true;
                }
                if (inSection) section.AppendLine(raw);
            }
            return section.Length > 0 ? section.ToString().Trim() : null;
        }

        /// <summary>Compares dotted versions number by number ("1.10.0" is newer than "1.9.2"); unreadable parts count as 0.</summary>
        public static int CompareVersions(string a, string b)
        {
            string[] left = (a ?? string.Empty).Split('-')[0].Split('.');
            string[] right = (b ?? string.Empty).Split('-')[0].Split('.');
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                int x = i < left.Length && int.TryParse(left[i], out int l) ? l : 0;
                int y = i < right.Length && int.TryParse(right[i], out int r) ? r : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static string StripRevision(string url)
        {
            int hash = url.IndexOf('#');
            return hash >= 0 ? url.Substring(0, hash) : url;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int end = text.IndexOf('\n');
            return (end >= 0 ? text.Substring(0, end) : text).Trim();
        }

        private static Marker ReadMarker()
        {
            try { return File.Exists(MarkerPath) ? JsonUtility.FromJson<Marker>(File.ReadAllText(MarkerPath)) : null; }
            catch (Exception) { return null; }
        }

        private static void WriteMarker(Latest latest)
        {
            File.WriteAllText(MarkerPath, JsonUtility.ToJson(new Marker { commit = latest.Commit, version = latest.Version }, true));
        }
    }

    /// <summary>After an update Unity reloads its scripts; this opens the updates pane again to say it is done.</summary>
    [InitializeOnLoad]
    internal static class ParelSdkUpdateResume
    {
        static ParelSdkUpdateResume()
        {
            string version = SessionState.GetString(ParelSdkUpdater.JustUpdatedKey, null);
            if (string.IsNullOrEmpty(version)) return;
            SessionState.EraseString(ParelSdkUpdater.JustUpdatedKey);
            EditorApplication.delayCall += () => ParelUpdateWindow.ShowUpdated(version);
        }
    }
}
