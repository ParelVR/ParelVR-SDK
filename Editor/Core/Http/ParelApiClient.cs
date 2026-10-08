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
        public static string BaseUrl => ParelEnvironment.BaseUrl;

        [Serializable]
        private sealed class ErrorEnvelope
        {
            public bool success;
            public string message;
        }

        private static void ApplyAuthHeaders(UnityWebRequest request)
        {
            if (!string.IsNullOrEmpty(ParelSession.UserId))
                request.SetRequestHeader("X-ParelVR-Id", ParelSession.UserId);
            if (!string.IsNullOrEmpty(ParelSession.SessionTicket))
                request.SetRequestHeader("Authorization", $"Bearer {ParelSession.SessionTicket}");
        }

        private static async Task PumpAsync(UnityWebRequestAsyncOperation operation, CancellationToken ct)
        {
            while (!operation.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    operation.webRequest.Abort();
                    ct.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }
        }

        private static void ThrowIfFailed(UnityWebRequest request)
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

        public static async Task<string> GetRawAsync(string path, CancellationToken ct = default)
        {
            using var request = UnityWebRequest.Get(BaseUrl + path);
            request.downloadHandler = new DownloadHandlerBuffer();
            ApplyAuthHeaders(request);

            var operation = request.SendWebRequest();
            await PumpAsync(operation, ct);
            ThrowIfFailed(request);
            return request.downloadHandler.text;
        }

        private static async Task<string> SendRawAsync(string method, string path, object payload, CancellationToken ct)
        {
            using var request = new UnityWebRequest(BaseUrl + path, method);
            string json = payload != null ? JsonUtility.ToJson(payload) : "{}";
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            ApplyAuthHeaders(request);

            var operation = request.SendWebRequest();
            await PumpAsync(operation, ct);
            ThrowIfFailed(request);
            return request.downloadHandler.text;
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

        public static async Task<string> PutBytesRawAsync(
            string path,
            byte[] data,
            string contentType,
            IProgress<float> progress = null,
            CancellationToken ct = default)
        {
            using var request = new UnityWebRequest(BaseUrl + path, "PUT");
            request.uploadHandler = new UploadHandlerRaw(data);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
            ApplyAuthHeaders(request);

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    operation.webRequest.Abort();
                    ct.ThrowIfCancellationRequested();
                }
                progress?.Report(request.uploadProgress);
                await Task.Yield();
            }
            progress?.Report(1f);

            ThrowIfFailed(request);
            return request.downloadHandler.text;
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
