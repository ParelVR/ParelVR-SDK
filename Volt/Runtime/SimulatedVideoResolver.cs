using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ParelVR.VoltHost;
using UnityEngine;
using UnityEngine.Networking;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// Plays YouTube links in world video players. A YouTube link is a web page, not a video file, and the address
    /// of the file behind it only works from the computer that asked for it — so it is looked up here, on the
    /// player's own machine, with yt-dlp. Windows only.
    ///
    /// Unity's player on Windows (Media Foundation) opens one kind of YouTube stream: the single mp4 that holds
    /// picture and sound together, which YouTube offers at 360p. YouTube's HLS playlists and its separate picture
    /// and sound streams (everything above 360p) do not open in it, so they are not asked for.
    /// </summary>
    internal sealed class SimulatedVideoResolver : MonoBehaviour
    {
        private const string ToolUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        // Live streams (YouTube live, Twitch, Kick) have no single file, only an HLS playlist: that is the fallback.
        // HD first: a 1080p-or-lower H.264 picture and an AAC sound file, played side by side (see VoltUnityVideoBackend).
        private const string VodFormat = "bv*[height<=1080][ext=mp4][vcodec^=avc1][protocol=https]+ba[ext=m4a][protocol=https]/b[ext=mp4][vcodec^=avc1][protocol=https]/18/b";
        private const string MuxedFormat = "b[ext=mp4][vcodec^=avc1][protocol=https]/18/b";
        // YouTube live now has separate picture and sound playlists only: the pair prints as two lines, muxed by the relay.
        private const string LiveFormat = "b[protocol^=m3u8][height<=1080]/bv*[protocol^=m3u8][height<=1080][vcodec^=avc1]+ba[protocol^=m3u8]/b[protocol^=m3u8]/b";
        private const string StreamOnlyFormat = "b[protocol^=m3u8]/b";
        private const int VodTimeoutMs = 30000;
        private const int LiveTimeoutMs = 60000;
        // YouTube changes often and an old yt-dlp stops working, so it is fetched again now and then.
        private const double RefreshDays = 14;
        private const int MaxCachedLookups = 128;
        private const int MaxLookupSlots = 2;

        private static SimulatedVideoResolver _instance;
        private static readonly object StartLock = new object();
        private static readonly SemaphoreSlim LookupSlots = new SemaphoreSlim(MaxLookupSlots, MaxLookupSlots);
        private bool _fetching;

        private enum Provider
        {
            Unknown,
            YouTube,
            Twitch,
            Kick
        }

        private readonly struct Attempt
        {
            public readonly string Format;
            public readonly string Clients;

            public Attempt(string format, string clients)
            {
                Format = format;
                Clients = clients;
            }
        }

        private sealed class Lookup
        {
            public volatile bool Done;
            public bool Running;
            public string Media;
            public string Problem;
            public bool IsLive;
            public double ExpiresAt;
            public readonly List<Action<string>> Waiters = new List<Action<string>>();
        }

        private static readonly Dictionary<string, Lookup> Lookups = new Dictionary<string, Lookup>(StringComparer.Ordinal);

        /// <summary>Lets the simulator play YouTube links, unless the project (the ParelVR client itself) already does.</summary>
        internal static void InstallIfMissing()
        {
            if (VoltUnityVideoBackend.ResolverHandles != null && VoltUnityVideoBackend.Resolver != null) return;
            VoltUnityVideoBackend.ResolverHandles = Handles;
            VoltUnityVideoBackend.Resolver = Resolve;
        }

        // ── asking YouTube directly ──────────────────────────────────────────
        // Works on every platform (Windows, Quest): the same question YouTube's own Android app asks, which is
        // answered with the address of the 360p file. When YouTube stops answering it, these values are what to
        // update (yt-dlp's "android" client lists the current ones); on Windows yt-dlp is the fallback meanwhile.

        private const string AppVersion = "21.26.364";
        private const string AppAgent = "com.google.android.youtube/" + AppVersion + " (Linux; U; Android 11) gzip";
        private const string PlayerUrl = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";

        [Serializable] private sealed class PlayerAnswer { public Playability playabilityStatus; public Streaming streamingData; }
        [Serializable] private sealed class Playability { public string status; public string reason; }
        [Serializable] private sealed class Streaming { public StreamFormat[] formats; public StreamFormat[] adaptiveFormats; public string hlsManifestUrl; }
        [Serializable] private sealed class StreamFormat { public int itag; public string url; public string mimeType; public int height; public int bitrate; }

        /// <summary>The highest H.264 picture up to 1080p and the best AAC sound, or nulls.</summary>
        private static void PickHd(StreamFormat[] adaptive, out string video, out string audio)
        {
            video = null;
            audio = null;
            if (adaptive == null) return;
            int bestHeight = 0, bestAudio = 0;
            foreach (StreamFormat f in adaptive)
            {
                if (f == null || string.IsNullOrEmpty(f.url) || f.mimeType == null) continue;
                if (f.mimeType.StartsWith("video/mp4", StringComparison.Ordinal) && f.mimeType.Contains("avc1") && f.height <= 1080 && f.height > bestHeight)
                {
                    bestHeight = f.height;
                    video = f.url;
                }
                else if (f.mimeType.StartsWith("audio/mp4", StringComparison.Ordinal) && f.bitrate > bestAudio)
                {
                    bestAudio = f.bitrate;
                    audio = f.url;
                }
            }
            if (bestHeight <= 360 || audio == null) video = audio = null;   // no better than the single file
        }

        /// <summary>The eleven-character id of the video a YouTube link points at, or null.</summary>
        public static string VideoId(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return null;
            string id = null;
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (uri.Host.ToLowerInvariant() == "youtu.be") id = parts[0];
            else if (parts.Length >= 2 && (parts[0] == "shorts" || parts[0] == "embed" || parts[0] == "live" || parts[0] == "v")) id = parts[1];
            else
            {
                foreach (string pair in uri.Query.TrimStart('?').Split('&'))
                    if (pair.StartsWith("v=", StringComparison.Ordinal)) id = pair.Substring(2);
            }
            if (id == null || id.Length != 11) return null;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_') return null;
            return id;
        }

        private static Provider ProviderOf(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return Provider.Unknown;
            string host = uri.Host.ToLowerInvariant();
            if (host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal)) return Provider.YouTube;
            if (host == "twitch.tv" || host.EndsWith(".twitch.tv", StringComparison.Ordinal)) return Provider.Twitch;
            if (host == "kick.com" || host.EndsWith(".kick.com", StringComparison.Ordinal)) return Provider.Kick;
            return Provider.Unknown;
        }

        private static bool IsLiveUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
            string path = uri.AbsolutePath.Trim('/').ToLowerInvariant();
            switch (ProviderOf(url))
            {
                case Provider.YouTube:
                    return path == "live" || path.StartsWith("live/", StringComparison.Ordinal) || path.EndsWith("/live", StringComparison.Ordinal);
                case Provider.Twitch:
                    return !(path.StartsWith("videos/", StringComparison.Ordinal) || path.StartsWith("clip/", StringComparison.Ordinal) ||
                             path.StartsWith("collections/", StringComparison.Ordinal));
                case Provider.Kick:
                    return !(path.StartsWith("video/", StringComparison.Ordinal) || path.StartsWith("clips/", StringComparison.Ordinal));
                default:
                    return false;
            }
        }

        private static bool ContainsHls(string media) =>
            !string.IsNullOrEmpty(media) && media.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

        private static Attempt[] AttemptsFor(Provider provider, bool live)
        {
            if (provider == Provider.Twitch || provider == Provider.Kick)
                return new[] { new Attempt(StreamOnlyFormat, null) };

            if (provider == Provider.YouTube)
            {
                if (live)
                {
                    return new[]
                    {
                        new Attempt(LiveFormat, null),
                        new Attempt(LiveFormat, "youtube:player_client=mweb"),
                        new Attempt(LiveFormat, "youtube:player_client=android_vr")
                    };
                }

                return new[]
                {
                    new Attempt(VodFormat, null),
                    new Attempt(MuxedFormat, null),
                    new Attempt(VodFormat, "youtube:player_client=mweb"),
                    new Attempt(MuxedFormat, "youtube:player_client=mweb"),
                    new Attempt(VodFormat, "youtube:player_client=android_vr")
                };
            }

            return new[] { new Attempt(MuxedFormat, null) };
        }

        private IEnumerator AskOrRun(string url, Action<string> done, Lookup lookup)
        {
            string id = VideoId(url);
            if (id != null)
            {
                string body = "{\"context\":{\"client\":{\"clientName\":\"ANDROID\",\"clientVersion\":\"" + AppVersion + "\",\"androidSdkVersion\":30,\"userAgent\":\"" + AppAgent +
                              "\",\"osName\":\"Android\",\"osVersion\":\"11\",\"hl\":\"en\",\"timeZone\":\"UTC\",\"utcOffsetMinutes\":0}},\"videoId\":\"" + id +
                              "\",\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\"}},\"contentCheckOk\":true,\"racyCheckOk\":true}";
                string media = null, problem;
                using (var request = new UnityWebRequest(PlayerUrl, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 20;
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("X-Youtube-Client-Name", "3");
                    request.SetRequestHeader("X-Youtube-Client-Version", AppVersion);
                    // Unity does not let every platform choose this header; YouTube answers without it too.
                    try { request.SetRequestHeader("User-Agent", AppAgent); }
                    catch (InvalidOperationException) { }
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success) problem = request.error;
                    else media = PickFile(request.downloadHandler.text, out problem);
                }
                if (media != null)
                {
                    lookup.IsLive = ContainsHls(media);
                    done(media);
                    yield break;
                }
                Debug.LogWarning("[VideoResolver] YouTube gave no playable file for " + id + ": " + problem);
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            yield return Run(url, done, lookup);
#else
            done(null);
#endif
        }

        // The file with picture and sound together, out of YouTube's answer.
        private static string PickFile(string json, out string problem)
        {
            problem = "no file with picture and sound";
            PlayerAnswer answer;
            try { answer = JsonUtility.FromJson<PlayerAnswer>(json); }
            catch (ArgumentException e) { problem = e.Message; return null; }
            if (answer == null) return null;
            if (answer.playabilityStatus != null && answer.playabilityStatus.status != "OK" && !string.IsNullOrEmpty(answer.playabilityStatus.status))
                problem = answer.playabilityStatus.status + " " + answer.playabilityStatus.reason;
            if (answer.streamingData == null) return null;
            // A live stream has no files, only the playlist.
            if (answer.streamingData.formats == null || answer.streamingData.formats.Length == 0)
                return string.IsNullOrEmpty(answer.streamingData.hlsManifestUrl) ? null : answer.streamingData.hlsManifestUrl;
            string fallback = null;
            foreach (StreamFormat format in answer.streamingData.formats)
            {
                if (format == null || string.IsNullOrEmpty(format.url) || format.mimeType == null || !format.mimeType.StartsWith("video/mp4", StringComparison.Ordinal)) continue;
                if (format.itag == 18)
                {
                    fallback = format.url;
                    break;
                }
                if (fallback == null) fallback = format.url;
            }
            // HD as "picture / sound / single file": the player falls back to the single file if the pair will not open.
            PickHd(answer.streamingData.adaptiveFormats, out string video, out string audio);
            if (video != null) return video + "\n" + audio + (fallback != null ? "\n" + fallback : string.Empty);
            return fallback;
        }

        // Not under Unity's data folder: that lives in LocalLow, and a program started from there cannot unpack itself
        // ("Could not create temporary directory").
        private static string ToolPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParelVR", "VideoResolver", "yt-dlp.exe");
        private static double ToolAgeDays => File.Exists(ToolPath) ? (DateTime.UtcNow - File.GetLastWriteTimeUtc(ToolPath)).TotalDays : double.MaxValue;

        /// <summary>True for a link to a YouTube video or a Twitch / Kick stream.</summary>
        public static bool Handles(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.IsLoopback || uri.UserInfo.Length > 0 || IPAddress.TryParse(uri.Host, out _)) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "youtu.be" || host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) ||
                   host == "twitch.tv" || host.EndsWith(".twitch.tv", StringComparison.Ordinal) ||
                   host == "kick.com" || host.EndsWith(".kick.com", StringComparison.Ordinal);
        }

        /// <summary>Answers with the address of a file the video player can open, or null when there is none.</summary>
        public static void Resolve(string url, Action<string> done)
        {
            if (done == null) return;
            // The address becomes a program argument: nothing that could end the argument or start another.
            if (!Handles(url) || url.IndexOf('"') >= 0 || url.IndexOf(' ') >= 0 || url.IndexOf('\n') >= 0 || url.IndexOf('\r') >= 0 || url.Length > 500)
            {
                done(null);
                return;
            }
            if (_instance == null)
            {
                var go = new GameObject("Parel Video Resolver") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<SimulatedVideoResolver>();
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (Lookups.TryGetValue(url, out Lookup cached) && cached.Done)
            {
                if (cached.ExpiresAt > now)
                {
                    done(cached.Media);
                    return;
                }
                Lookups.Remove(url);
            }

            if (!Lookups.TryGetValue(url, out Lookup lookup))
            {
                lookup = new Lookup();
                Lookups[url] = lookup;
            }

            if (lookup.Running)
            {
                lookup.Waiters.Add(done);
                return;
            }

            lookup.Running = true;
            lookup.Done = false;
            lookup.Media = null;
            lookup.Problem = null;
            lookup.IsLive = IsLiveUrl(url);
            lookup.Waiters.Add(done);
            _instance.StartCoroutine(_instance.ResolveAndPublish(url, lookup));
        }

        private IEnumerator ResolveAndPublish(string url, Lookup lookup)
        {
            string media = null;
            yield return AskOrRun(url, value => media = value, lookup);

            lookup.Media = media;
            lookup.IsLive = lookup.IsLive || ContainsHls(media);
            lookup.Done = true;
            lookup.Running = false;
            lookup.ExpiresAt = Time.realtimeSinceStartupAsDouble + (media == null ? 15.0 : lookup.IsLive ? 30.0 : 600.0);

            Action<string>[] waiters = lookup.Waiters.ToArray();
            lookup.Waiters.Clear();
            foreach (Action<string> waiter in waiters)
            {
                try { waiter(media); }
                catch (Exception e) { Debug.LogException(e); }
            }

            PruneLookups();
        }

        private static void PruneLookups()
        {
            if (Lookups.Count <= MaxCachedLookups) return;
            double now = Time.realtimeSinceStartupAsDouble;
            var remove = new List<string>();
            foreach (KeyValuePair<string, Lookup> pair in Lookups)
                if (pair.Value.Done && pair.Value.ExpiresAt <= now) remove.Add(pair.Key);
            foreach (string key in remove) Lookups.Remove(key);
        }

        private IEnumerator Run(string url, Action<string> done, Lookup lookup)
        {
            Provider provider = ProviderOf(url);
            bool live = lookup.IsLive || IsLiveUrl(url);
            Attempt[] attempts = AttemptsFor(provider, live);

            for (int pass = 0; pass < 2; pass++)
            {
                // A second try is made with a newly fetched yt-dlp.
                yield return FetchTool(pass > 0);
                if (!File.Exists(ToolPath)) break;

                double age = ToolAgeDays;
                string tool = ToolPath;
                foreach (Attempt attempt in attempts)
                {
                    var result = new Lookup { IsLive = live };
                    new Thread(() => Look(tool, url, attempt.Format, attempt.Clients, result, live)) { IsBackground = true, Name = "Parel Video Resolver" }.Start();
                    while (!result.Done) yield return null;
                    if (result.Media != null)
                    {
                        lookup.IsLive = result.IsLive;
                        done(result.Media);
                        yield break;
                    }
                    lookup.Problem = result.Problem;
                    Debug.LogWarning("[VideoResolver] " + provider + " lookup failed: " + result.Problem);
                }
                if (age < 1) break; // already current: fetching it again would not change the answer
            }
            done(null);
        }

        private IEnumerator FetchTool(bool force)
        {
            while (_fetching) yield return null;
            if (!force && ToolAgeDays < RefreshDays) yield break;
            _fetching = true;
            Debug.Log("[VideoResolver] Fetching yt-dlp.");
            string partial = ToolPath + "." + Guid.NewGuid().ToString("N") + ".part";
            bool ready = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ToolPath));
                ready = true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning("[VideoResolver] " + e.Message);
            }
            if (ready)
            {
                bool fetched;
                using (UnityWebRequest request = UnityWebRequest.Get(ToolUrl))
                {
                    request.downloadHandler = new DownloadHandlerFile(partial) { removeFileOnAbort = true };
                    yield return request.SendWebRequest();
                    fetched = request.result == UnityWebRequest.Result.Success;
                    if (!fetched) Debug.LogWarning("[VideoResolver] yt-dlp could not be fetched: " + request.error);
                }
                // A failed refresh leaves the copy that is already there in use.
                if (fetched && File.Exists(partial) && new FileInfo(partial).Length > 1024 * 1024)
                {
                    try
                    {
                        if (File.Exists(ToolPath))
                        {
                            try { File.Replace(partial, ToolPath, null); }
                            catch (Exception)
                            {
                                File.Delete(ToolPath);
                                File.Move(partial, ToolPath);
                            }
                        }
                        else
                        {
                            File.Move(partial, ToolPath);
                        }
                        File.SetLastWriteTimeUtc(ToolPath, DateTime.UtcNow);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Debug.LogWarning("[VideoResolver] " + e.Message);
                    }
                }
                else if (File.Exists(partial))
                {
                    try { File.Delete(partial); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Debug.LogWarning("[VideoResolver] " + e.Message); }
                }
            }
            _fetching = false;
        }

        // Runs on its own thread: yt-dlp takes a few seconds.
        private static void Look(string tool, string url, string format, string clients, Lookup lookup, bool live)
        {
            bool slot = false;
            try
            {
                if (!LookupSlots.Wait(15000))
                {
                    lookup.Problem = "yt-dlp queue is busy";
                    return;
                }
                slot = true;

                var arguments = new StringBuilder();
                arguments.Append("-f \"").Append(format).Append("\" --print urls --no-playlist --playlist-end 1 --no-warnings --ignore-config --no-cache-dir --socket-timeout 15 --retries 3 --fragment-retries 3");
                if (!string.IsNullOrEmpty(clients)) arguments.Append(" --extractor-args \"").Append(clients).Append('"');
                arguments.Append(" -- \"").Append(url).Append('"');

                string output = RunHidden(tool, arguments.ToString(), live ? LiveTimeoutMs : VodTimeoutMs, out bool timedOut, out int exitCode);
                if (timedOut)
                {
                    lookup.Problem = "yt-dlp took too long";
                    return;
                }

                foreach (string line in output.Split('\n'))
                {
                    string candidate = line.Trim();
                    if (!candidate.StartsWith("https://", StringComparison.Ordinal) && !candidate.StartsWith("http://", StringComparison.Ordinal)) continue;
                    // A picture + sound pair prints two addresses: both are passed on, one per line.
                    lookup.Media = lookup.Media == null ? candidate : lookup.Media + "\n" + candidate;
                    if (lookup.Media.IndexOf('\n') >= 0) break;
                }

                lookup.IsLive = live || ContainsHls(lookup.Media);
                if (lookup.Media == null)
                {
                    string detail = output.Trim();
                    if (detail.Length > 400) detail = detail.Substring(detail.Length - 400);
                    lookup.Problem = (detail.Length > 0 ? detail : "no stream this player can open") + " (exit " + exitCode + ")";
                }
            }
            catch (Exception e)
            {
                lookup.Problem = e.Message;
            }
            finally
            {
                if (slot) LookupSlots.Release();
                lookup.Done = true;
            }
        }

        // ── running the tool ─────────────────────────────────────────────────
        // The client is built with IL2CPP, where System.Diagnostics.Process does not exist: the Windows calls directly.

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        private const int UseStdHandles = 0x100, CreateNoWindow = 0x08000000, HandleFlagInherit = 1;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES attributes, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit,
            int flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(IntPtr file, byte[] buffer, int count, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll")] private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);
        [DllImport("kernel32.dll")] private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, int code);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Runs a program without a window and returns everything it printed.</summary>
        private static string RunHidden(string program, string arguments, int timeoutMs, out bool timedOut, out int exitCode)
        {
            IntPtr read, process, thread;
            // One at a time: a program started meanwhile would otherwise inherit this pipe too and hold it open.
            lock (StartLock)
            {
                var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
                if (!CreatePipe(out read, out IntPtr write, ref security, 0)) throw new IOException("pipe failed");
                SetHandleInformation(read, HandleFlagInherit, 0); // our end is not handed to the child
                var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = UseStdHandles, hStdOutput = write, hStdError = write, hStdInput = IntPtr.Zero };
                var command = new StringBuilder("\"" + program + "\" " + arguments);
                bool started = CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow, IntPtr.Zero, null, ref startup, out PROCESS_INFORMATION information);
                int error = Marshal.GetLastWin32Error();
                CloseHandle(write);
                if (!started)
                {
                    CloseHandle(read);
                    throw new IOException("yt-dlp could not be started (" + error + ")");
                }
                process = information.hProcess;
                thread = information.hThread;
            }

            int late = 0, finished = 0;
            var watchdog = new Thread(() =>
            {
                if (WaitForSingleObject(process, timeoutMs) == 0) return;
                Volatile.Write(ref late, 1);
                TerminateProcess(process, 1);
                // Whatever it started may still hold the pipe open: stop waiting for it.
                for (int i = 0; i < 100 && Volatile.Read(ref finished) == 0; i++)
                {
                    CancelIoEx(read, IntPtr.Zero);
                    Thread.Sleep(50);
                }
            }) { IsBackground = true };
            watchdog.Start();

            var text = new StringBuilder();
            var buffer = new byte[4096];
            while (Volatile.Read(ref late) == 0 && ReadFile(read, buffer, buffer.Length, out int count, IntPtr.Zero) && count > 0)
                text.Append(Encoding.UTF8.GetString(buffer, 0, count));
            Volatile.Write(ref finished, 1);
            watchdog.Join();
            timedOut = Volatile.Read(ref late) != 0;
            if (!GetExitCodeProcess(process, out exitCode)) exitCode = -1;
            if (timedOut) exitCode = -1;
            CloseHandle(read);
            CloseHandle(thread);
            CloseHandle(process);
            return text.ToString();
        }
    }
}
