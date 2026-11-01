using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.Http;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Backend
{
    /// <summary>The other player denied the request. Nothing was uploaded.</summary>
    public sealed class ParelUploadRequestDeniedException : Exception
    {
        public UploadRequestRecord Request { get; }
        public ParelUploadRequestDeniedException(UploadRequestRecord request, string message) : base(message) { Request = request; }
    }

    /// <summary>
    /// Sends an "Upload for someone else" request after the build: the build and its pictures go to ParelVR
    /// (held, never public), the other player is emailed, and this waits until they approve or deny it.
    /// Script reloads are held off while it waits; a request cut off anyway (Unity closed or reloaded) is
    /// cancelled the next time the editor loads, and the Builder says so.
    /// </summary>
    [InitializeOnLoad]
    public static class ParelUploadRequestFlow
    {
        public sealed class Options
        {
            public string Kind;                 // "avatar" or "world"
            public ParelUploadForOther.Pick Target;
            public string Title;
            public string Description;
            public string BuildPath;            // the PC bundle
            public string Platform = "windows";
            public List<string> Platforms = new List<string>();
            public ParelBuildPictures Pictures;
            public UploadRequestApiClient.Stats Stats = new UploadRequestApiClient.Stats();
            public Action Cancel;               // the Builder's cancel (the pending box's Cancel Request button)
        }

        private const string OpenKey = "ParelVR.UploadRequest.Open";
        private const int PollMs = 4000;

        [Serializable] private sealed class OpenRequest { public string id; public string kind; public string target; public string project; }

        /// <summary>Set when a request was cut off by a reload or Unity closing; the Builder shows it once.</summary>
        public static string InterruptedNotice { get; private set; }

        static ParelUploadRequestFlow()
        {
            EditorApplication.delayCall += () => _ = CancelInterruptedAsync();
        }

        /// <summary>
        /// Sends the request and waits for the answer. Returns the approved request; throws
        /// ParelUploadRequestDeniedException when denied, OperationCanceledException when cancelled here,
        /// and Exception when it ran out or was cancelled elsewhere.
        /// </summary>
        public static async Task<UploadRequestRecord> SendAndWaitAsync(Options o, ParelUploadForOther ui, Action<float, string> progress, CancellationToken ct)
        {
            string name = o.Target.LabelWithHandle;
            progress?.Invoke(0.02f, $"Sending your request to {name}...");
            ui?.ShowStatus(ParelUploadForOther.Tone.Wait, $"Sending the request to {o.Target.Label}...", "The build and its pictures go to ParelVR first. They're held there and aren't public.");

            byte[] build = File.ReadAllBytes(o.BuildPath);
            var body = new UploadRequestApiClient.CreateBody
            {
                kind = o.Kind,
                targetParelId = o.Target.id,
                title = o.Title,
                description = o.Description ?? string.Empty,
                declaredBytes = build.LongLength,
                platforms = o.Platforms ?? new List<string>(),
                stats = o.Stats ?? new UploadRequestApiClient.Stats(),
                client = ClientInfo(),
            };
            if (o.Pictures != null)
            {
                body.stats.textures = o.Pictures.TextureCount;
                body.stats.textureBytes = o.Pictures.TextureBytes;
            }

            UploadRequestRecord request = await UploadRequestApiClient.CreateAsync(body, ct);
            if (request == null || string.IsNullOrEmpty(request.id)) throw new Exception("ParelVR didn't start the request.");
            string id = request.id;
            bool approved = false;
            bool locked = false;
            try
            {
                if (o.Pictures != null)
                {
                    progress?.Invoke(0.04f, "Sending the pictures...");
                    await SendPicture(id, "geometry", o.Pictures.Geometry, ct);
                    await SendPicture(id, "textures", o.Pictures.Textures, ct);
                    await SendPicture(id, "combined", o.Pictures.Combined, ct);
                }

                await UploadRequestApiClient.SendBuildAsync(id, o.Platform, build,
                    new Progress<float>(p => progress?.Invoke(0.06f + p * 0.9f, $"Sending the build to ParelVR for {name}... {(int)(p * 100)}%")), ct);
                build = null;
                request = await UploadRequestApiClient.SubmitAsync(id, ct) ?? request;
                Remember(id, o.Kind, name);

                // A script reload would end this wait with nobody left to finish the upload.
                EditorApplication.LockReloadAssemblies();
                locked = true;

                DateTime started = DateTime.UtcNow;
                for (;;)
                {
                    string waited = Elapsed(DateTime.UtcNow - started);
                    progress?.Invoke(1f, $"Request pending: waiting for {name} to approve it ({waited})");
                    ui?.ShowStatus(ParelUploadForOther.Tone.Wait, $"Request pending: waiting for {o.Target.Label}",
                        $"ParelVR emailed {name}. Nothing is uploaded until they approve it on their dashboard. Request ID {id} · waiting {waited}.\n" +
                        "Script reloads are paused until they answer or you cancel.",
                        "Cancel Request", o.Cancel);
                    await Task.Delay(PollMs, ct);

                    UploadRequestRecord fresh;
                    try { fresh = await UploadRequestApiClient.GetAsync(id, ct); }
                    catch (ParelApiException ex) when (ex.StatusCode != 404 && ex.StatusCode != 401) { continue; } // offline for a moment
                    if (fresh == null) continue;
                    request = fresh;

                    switch (request.status)
                    {
                        case "approved":
                            approved = true;
                            ui?.ShowStatus(ParelUploadForOther.Tone.Good, $"Approved by {o.Target.Label}", $"Uploading it to {name}'s {o.Kind}s now. Request ID {id}.");
                            return request;
                        case "denied":
                            ui?.ShowStatus(ParelUploadForOther.Tone.Bad, $"Denied: {o.Target.Label} denied your request",
                                $"The upload was cancelled and the build was removed from this computer. Request ID {id}.", "Dismiss", () => ui.HideStatus());
                            throw new ParelUploadRequestDeniedException(request, $"{name} denied your request to upload \"{o.Title}\". The upload was cancelled and the build was removed.");
                        case "pending":
                            continue;
                        default:
                            string why = request.status == "expired" ? "ran out before it was answered" : request.status;
                            ui?.ShowStatus(ParelUploadForOther.Tone.Neutral, $"The request {why}", $"Nothing was uploaded. Request ID {id}.", "Dismiss", () => ui.HideStatus());
                            throw new Exception($"Your request to {name} {why}. Nothing was uploaded.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                try { await UploadRequestApiClient.CancelAsync(id, CancellationToken.None); }
                catch (Exception e) { Debug.LogWarning($"[ParelVR SDK] Upload request {id} wasn't cancelled on ParelVR: {e.Message}"); }
                ui?.ShowStatus(ParelUploadForOther.Tone.Neutral, "Request cancelled", $"Nothing was uploaded to {name}. Request ID {id}.", "Dismiss", () => ui.HideStatus());
                throw;
            }
            finally
            {
                if (locked) EditorApplication.UnlockReloadAssemblies();
                Forget(id);
                if (!approved) Debug.Log($"[ParelVR SDK] Upload request {id} ended without an approval.");
            }
        }

        /// <summary>Ends the requester's access to the item once everything is uploaded (best effort).</summary>
        public static async Task FinishAsync(string id)
        {
            try { await UploadRequestApiClient.CompleteAsync(id, CancellationToken.None); }
            catch (Exception e) { Debug.LogWarning($"[ParelVR SDK] Upload request {id} wasn't marked finished: {e.Message} (its access ends on its own)."); }
        }

        private static async Task SendPicture(string id, string role, byte[] image, CancellationToken ct)
        {
            if (image == null || image.Length == 0) return;
            try { await UploadRequestApiClient.SendPictureAsync(id, role, image, ct); }
            catch (ParelApiException e) { Debug.LogWarning($"[ParelVR SDK] The {role} picture wasn't sent: {e.Message}"); }
        }

        private static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

        private static UploadRequestApiClient.Client ClientInfo()
        {
            string device = SystemInfo.deviceUniqueIdentifier ?? string.Empty;
            string deviceId = string.Empty;
            if (device.Length > 0 && device != SystemInfo.unsupportedIdentifier)
            {
                using var sha = SHA256.Create();
                deviceId = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("parelvr-sdk:" + device))).Replace("-", string.Empty).Substring(0, 32).ToLowerInvariant();
            }
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ParelUploadRequestFlow).Assembly);
            return new UploadRequestApiClient.Client
            {
                sdkVersion = package?.version ?? string.Empty,
                unityVersion = Application.unityVersion,
                os = SystemInfo.operatingSystem,
                deviceId = deviceId,
            };
        }

        // ---- a request cut off by a reload or Unity closing --------------------------------------

        private static string ProjectKey => Application.dataPath;

        private static void Remember(string id, string kind, string target)
        {
            EditorPrefs.SetString(OpenKey, JsonUtility.ToJson(new OpenRequest { id = id, kind = kind, target = target, project = ProjectKey }));
        }

        private static void Forget(string id)
        {
            OpenRequest open = Read();
            if (open != null && open.id == id) EditorPrefs.DeleteKey(OpenKey);
        }

        private static OpenRequest Read()
        {
            string json = EditorPrefs.GetString(OpenKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<OpenRequest>(json); }
            catch (Exception) { return null; }
        }

        private static async Task CancelInterruptedAsync()
        {
            OpenRequest open = Read();
            if (open == null || open.project != ProjectKey) return;
            // The session is restored a moment after load; wait for it briefly.
            for (int i = 0; i < 20 && !ParelSession.IsLoggedIn; i++) await Task.Delay(500);
            if (!ParelSession.IsLoggedIn) return; // try again next load
            EditorPrefs.DeleteKey(OpenKey);
            try
            {
                await UploadRequestApiClient.CancelAsync(open.id);
                InterruptedNotice = $"Your request to upload for {open.target} ({open.id}) stopped when Unity reloaded or closed, so it was cancelled. Nothing was uploaded; send it again.";
                Debug.Log("[ParelVR SDK] " + InterruptedNotice);
            }
            catch (ParelApiException e) when (e.StatusCode == 409)
            {
                // Already answered or finished: nothing to cancel.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ParelVR SDK] Couldn't cancel the interrupted upload request {open.id}: {e.Message}");
            }
        }

        /// <summary>Returns the notice once (then clears it).</summary>
        public static string TakeInterruptedNotice()
        {
            string notice = InterruptedNotice;
            InterruptedNotice = null;
            return notice;
        }
    }
}
