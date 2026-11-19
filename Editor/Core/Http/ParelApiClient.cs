using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.Settings;

namespace ParelVR.SDK.Core.Http
{
    /// <summary>
    /// HTTP client for ParelVR SDK Editor tooling. Uses configurable BaseUrl from
    /// ParelEnvironment instead of hardcoded localhost.
    /// </summary>
    public static class ParelApiClient
    {
        public static string BaseUrl
        {
            get
            {
                ParelPreferences.EnsureLoaded();
                return ParelEnvironment.BaseUrl;
            }
        }

        [Serializable]
        private sealed class ErrorEnvelope
        {
            public bool success;
            public string message;
        }

        private const string SessionEndedReason =
            "Your ParelVR session has ended (the server no longer accepts it). Sign in again in the ParelVR control panel.";

        private static void ApplyAuthHeaders(UnityWebRequest request)
        {
            // A sign-in goes out bare, as from a signed-out panel, even when it replaces a ticket the server dropped.
            if (IsLoginCall(request.url)) return;
            if (!string.IsNullOrEmpty(ParelSession.UserId))
                request.SetRequestHeader("X-ParelVR-Id", ParelSession.UserId);
            if (!string.IsNullOrEmpty(ParelSession.SessionTicket))
                request.SetRequestHeader("Authorization", $"Bearer {ParelSession.SessionTicket}");
        }

        private static async Task PumpAsync(UnityWebRequestAsyncOperation operation, CancellationToken ct, IProgress<float> uploadProgress = null)
        {
            while (!operation.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    operation.webRequest.Abort();
                    ct.ThrowIfCancellationRequested();
                }
                uploadProgress?.Report(operation.webRequest.uploadProgress);
                await Task.Yield();
            }
        }

        private static string BearerOf(UnityWebRequest request)
        {
            string auth = request.GetRequestHeader("Authorization");
            return !string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth.Substring(7) : string.Empty;
        }

        /// <summary>
        /// Sends the request make() builds and returns the body. When the server refuses the session ticket it carried,
        /// the SDK signs back in with its saved sign-in and sends a fresh copy once more, so an ended session never
        /// shows up as a failed call or a sign-out. Without a saved sign-in the session ends as before.
        /// </summary>
        private static async Task<string> SendAsync(Func<UnityWebRequest> make, CancellationToken ct, IProgress<float> uploadProgress = null)
        {
            for (int attempt = 0; ; attempt++)
            {
                string sentTicket;
                ParelApiException refused;
                using (UnityWebRequest request = make())
                {
                    ApplyAuthHeaders(request);
                    sentTicket = BearerOf(request);
                    await PumpAsync(request.SendWebRequest(), ct, uploadProgress);
                    try
                    {
                        ThrowIfFailedCore(request);
                        return request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                    }
                    catch (ParelApiException ex) when (ex.StatusCode == 401 && !string.IsNullOrEmpty(sentTicket) && !IsSignInCall(request.url))
                    {
                        refused = ex;
                    }
                }

                if (attempt > 0)
                {
                    ParelSession.Expire(sentTicket, SessionEndedReason); // refused again straight after signing back in
                    throw refused;
                }
                if (!await ParelSession.RecoverAsync(sentTicket, SessionEndedReason)) throw refused;
            }
        }

        private static bool IsSignInCall(string url) =>
            url != null && (url.Contains("/api/auth/login") || url.Contains("/api/auth/logout") || url.Contains("/api/auth/register"));

        private static bool IsLoginCall(string url) => url != null && url.Contains("/api/auth/login");

        private static void ThrowIfFailedCore(UnityWebRequest request)
        {
            bool networkFailure = request.result == UnityWebRequest.Result.ConnectionError
                || request.result == UnityWebRequest.Result.DataProcessingError;
            if (networkFailure)
                throw new ParelApiException(request.error ?? "Network error.", 0);

            long status = request.responseCode;
            string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

            bool serverSaysFailed = false;
            string serverMessage = null;
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("{"))
            {
                try
                {
                    var envelope = JsonUtility.FromJson<ErrorEnvelope>(text);
                    // JsonUtility defaults a missing bool to false, so only trust it when the key is present.
                    serverSaysFailed = envelope != null && !envelope.success && text.Contains("\"success\"");
                    serverMessage = envelope?.message;
                }
                catch (ArgumentException) { }
            }

            if (status < 200 || status >= 300 || serverSaysFailed)
            {
                string message = !string.IsNullOrEmpty(serverMessage) ? serverMessage : $"Request failed ({status}).";
                throw new ParelApiException(message, status);
            }
        }

        public static Task<string> GetRawAsync(string path, CancellationToken ct = default)
        {
            string url = BaseUrl + path;
            return SendAsync(() =>
            {
                var request = UnityWebRequest.Get(url);
                request.downloadHandler = new DownloadHandlerBuffer();
                return request;
            }, ct);
        }

        private static Task<string> SendRawAsync(string method, string path, object payload, CancellationToken ct)
        {
            string url = BaseUrl + path;
            byte[] body = Encoding.UTF8.GetBytes(payload != null ? JsonUtility.ToJson(payload) : "{}");
            return SendAsync(() =>
            {
                var request = new UnityWebRequest(url, method);
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                return request;
            }, ct);
        }

        public static async Task<T> GetJsonAsync<T>(string path, CancellationToken ct = default) where T : class
        {
            string text = await GetRawAsync(path, ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonUtility.FromJson<T>(text);
        }

        public static async Task<T> PostJsonAsync<T>(string path, object payload, CancellationToken ct = default) where T : class
        {
            string text = await SendRawAsync("POST", path, payload, ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonUtility.FromJson<T>(text);
        }

        public static async Task<T> PutJsonAsync<T>(string path, object payload, CancellationToken ct = default) where T : class
        {
            string text = await SendRawAsync("PUT", path, payload, ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonUtility.FromJson<T>(text);
        }

        public static async Task<T> PatchJsonAsync<T>(string path, object payload, CancellationToken ct = default) where T : class
        {
            string text = await SendRawAsync("PATCH", path, payload, ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonUtility.FromJson<T>(text);
        }

        public static async Task<T> DeleteJsonAsync<T>(string path, CancellationToken ct = default) where T : class
        {
            string text = await SendRawAsync("DELETE", path, null, ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonUtility.FromJson<T>(text);
        }

        public static async Task<Dictionary<string, object>> GetJsonDictAsync(string path, CancellationToken ct = default)
        {
            string text = await GetRawAsync(path, ct);
            return string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : MiniJson.AsDict(MiniJson.Parse(text));
        }

        public static async Task<Dictionary<string, object>> PostJsonDictAsync(string path, object payload, CancellationToken ct = default)
        {
            string text = await SendRawAsync("POST", path, payload, ct);
            return string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : MiniJson.AsDict(MiniJson.Parse(text));
        }

        public static async Task<Dictionary<string, object>> PatchJsonDictAsync(string path, object payload, CancellationToken ct = default)
        {
            string text = await SendRawAsync("PATCH", path, payload, ct);
            return string.IsNullOrWhiteSpace(text) ? new Dictionary<string, object>() : MiniJson.AsDict(MiniJson.Parse(text));
        }

        /// <summary>Bodies above this go up in parts: the proxy in front of the API refuses anything over 100 MB.</summary>
        public const int ChunkThresholdBytes = 48 * 1024 * 1024;
        private const int ChunkBytes = 32 * 1024 * 1024;

        /// <summary>
        /// Uploads a large body in 32 MB parts to "{root}/chunk" and joins them with "{root}/complete" (checked by
        /// SHA-256), the way world and avatar bundles go up. extraQuery is appended to both, e.g. "platform=windows".
        /// </summary>
        public static async Task<T> PutChunkedAsync<T>(string root, string extraQuery, byte[] data, IProgress<float> progress = null, CancellationToken ct = default) where T : class
        {
            string uploadId = Guid.NewGuid().ToString("N");
            int chunks = Math.Max(1, (data.Length + ChunkBytes - 1) / ChunkBytes);
            string sha;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                sha = BitConverter.ToString(hash.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
            string extra = string.IsNullOrEmpty(extraQuery) ? string.Empty : "&" + extraQuery;
            for (int index = 0; index < chunks; index++)
            {
                ct.ThrowIfCancellationRequested();
                int offset = index * ChunkBytes;
                int length = Math.Min(ChunkBytes, data.Length - offset);
                var body = new byte[length];
                Buffer.BlockCopy(data, offset, body, 0, length);
                int done = index;
                await PutBytesRawAsync($"{root}/chunk?uploadId={uploadId}&index={index}&total={chunks}{extra}", body, "application/octet-stream",
                    new Progress<float>(p => progress?.Report(Math.Min(1f, (done + p) / chunks))), ct);
            }
            return await PostJsonAsync<T>($"{root}/complete?uploadId={uploadId}&total={chunks}&sha256={sha}{extra}", null, ct);
        }

        /// <summary>Waits before each retry of an upload that failed on the way (about 2 minutes in all).</summary>
        private static readonly int[] UploadRetryDelaysMs = { 3000, 10000, 30000, 60000 };

        /// <summary>
        /// Failures that say nothing about the request itself: no connection, a timeout, or the proxy / tunnel in
        /// front of the API not reaching it (Cloudflare 520-530). The same PUT is safe to send again.
        /// </summary>
        private static bool IsTransient(ParelApiException ex) =>
            ex.StatusCode == 0 || ex.StatusCode == 408 || ex.StatusCode == 429
            || ex.StatusCode == 502 || ex.StatusCode == 503 || ex.StatusCode == 504
            || (ex.StatusCode >= 520 && ex.StatusCode <= 530);

        public static async Task<string> PutBytesRawAsync(
            string path,
            byte[] data,
            string contentType,
            IProgress<float> progress = null,
            CancellationToken ct = default)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await PutBytesOnceAsync(path, data, contentType, progress, ct);
                }
                catch (ParelApiException ex) when (IsTransient(ex) && attempt < UploadRetryDelaysMs.Length)
                {
                    int delay = UploadRetryDelaysMs[attempt];
                    string where = path.Split('?')[0];
                    Debug.LogWarning($"[ParelApiClient] Upload to {where} failed ({ex.Message}). Trying again in {delay / 1000} s " +
                                     $"({attempt + 1}/{UploadRetryDelaysMs.Length}).");
                    await Task.Delay(delay, ct);
                }
            }
        }

        private static async Task<string> PutBytesOnceAsync(
            string path,
            byte[] data,
            string contentType,
            IProgress<float> progress,
            CancellationToken ct)
        {
            string url = BaseUrl + path;
            string type = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
            string text = await SendAsync(() =>
            {
                var request = new UnityWebRequest(url, "PUT");
                request.uploadHandler = new UploadHandlerRaw(data);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", type);
                return request;
            }, ct, progress);
            progress?.Report(1f);
            return text;
        }

        public static async Task<T> PutBytesAsync<T>(
            string path,
            byte[] data,
            string contentType,
            IProgress<float> progress = null,
            CancellationToken ct = default) where T : class
        {
            string text = await PutBytesRawAsync(path, data, contentType, progress, ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonUtility.FromJson<T>(text);
        }
    }
}
