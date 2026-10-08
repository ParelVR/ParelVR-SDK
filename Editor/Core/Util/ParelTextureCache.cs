using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using ParelVR.SDK.Core.Settings;

namespace ParelVR.SDK.Core.Util
{
    /// <summary>In-memory cache for remote thumbnails shown in Editor UI lists.</summary>
    public static class ParelTextureCache
    {
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> InFlight = new HashSet<string>();

        public static Texture2D GetOrFetch(string url, Action<Texture2D> onLoaded)
        {
            string resolved = ResolveUrl(url);
            if (string.IsNullOrEmpty(resolved)) return null;
            if (Cache.TryGetValue(resolved, out var cached)) return cached;
            if (!InFlight.Contains(resolved)) _ = FetchAsync(resolved, onLoaded);
            return null;
        }

        private static string ResolveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            return ParelEnvironment.BaseUrl + (url.StartsWith("/") ? url : "/" + url);
        }

        private static async System.Threading.Tasks.Task FetchAsync(string url, Action<Texture2D> onLoaded)
        {
            InFlight.Add(url);
            try
            {
                using var request = UnityWebRequestTexture.GetTexture(url);
                var operation = request.SendWebRequest();
                while (!operation.isDone) await System.Threading.Tasks.Task.Yield();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    var texture = DownloadHandlerTexture.GetContent(request);
                    Cache[url] = texture;
                    onLoaded?.Invoke(texture);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not load thumbnail '{url}': {ex.Message}");
            }
            finally
            {
                InFlight.Remove(url);
            }
        }
    }
}
