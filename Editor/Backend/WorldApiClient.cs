using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;

namespace ParelVR.SDK.Backend
{
    [Serializable]
    public class WorldRecord
    {
        public string id;
        public string name;
        public string description;
        
        // --- Fields below are mapped from backend but not currently used in the SDK UI ---
        public string authorName;
        public string releaseStatus;
        public string thumbnailUrl;
        public int recommendedCapacity;
        public int maximumCapacity;
        public List<string> tags;
        public bool allowDebugging;
        public List<string> contentWarnings;
        public string createdAt;
        public string updatedAt;
        
        // Note: 'platforms' is omitted here because JsonUtility does not natively 
        // support dictionaries with dynamic keys (like "windows", "android").
    }

    [Serializable]
    public class WorldListResponse
    {
        public bool success;
        public string scope;
        public List<WorldRecord> worlds;
    }

    [Serializable]
    public class WorldResponse
    {
        public bool success;
        public string message;
        public WorldRecord world;
    }

    public static class WorldApiClient
    {
        /// <summary>
        /// Fetches the worlds owned by the currently authenticated user.
        /// </summary>
        public static async Task<List<WorldRecord>> GetMyWorldsAsync(CancellationToken ct = default)
        {
            var res = await ParelApiClient.GetJsonAsync<WorldListResponse>("/api/parelvr/worlds?scope=mine", ct);
            if (res != null) UnityEngine.Debug.Log($"[WorldApiClient] GetMyWorldsAsync Response:\n{UnityEngine.JsonUtility.ToJson(res, true)}");
            return res?.worlds ?? new List<WorldRecord>();
        }

        /// <summary>
        /// Uploads a built AssetBundle to a specific world and platform.
        /// </summary>
        public static async Task<WorldResponse> UploadBundleAsync(
            string worldId, 
            string platform, 
            byte[] bundleData, 
            IProgress<float> progress = null, 
            CancellationToken ct = default)
        {
            string url = $"/api/parelvr/worlds/{worldId}/bundle?platform={platform}";
            
            // Log payload info (size instead of raw bytes to avoid console freeze)
            UnityEngine.Debug.Log($"[WorldApiClient] UploadBundleAsync Request:\nURL: {url}\nPayload Size: {bundleData?.Length ?? 0} bytes\nPlatform: {platform}");
            
            var res = await ParelApiClient.PutBytesAsync<WorldResponse>(url, bundleData, "application/octet-stream", progress, ct);
            if (res != null) UnityEngine.Debug.Log($"[WorldApiClient] UploadBundleAsync Response:\n{UnityEngine.JsonUtility.ToJson(res, true)}");
            return res;
        }

        /// <summary>
        /// Uploads a thumbnail image to a specific world.
        /// </summary>
        public static async Task<WorldResponse> UploadImageAsync(
            string worldId, 
            byte[] imageData, 
            CancellationToken ct = default)
        {
            string url = $"/api/parelvr/worlds/{worldId}/image";
            
            // Log payload info (size instead of raw bytes to avoid console freeze)
            UnityEngine.Debug.Log($"[WorldApiClient] UploadImageAsync Request:\nURL: {url}\nPayload Size: {imageData?.Length ?? 0} bytes");
            
            var res = await ParelApiClient.PutBytesAsync<WorldResponse>(url, imageData, "image/jpeg", null, ct);
            if (res != null) UnityEngine.Debug.Log($"[WorldApiClient] UploadImageAsync Response:\n{UnityEngine.JsonUtility.ToJson(res, true)}");
            return res;
        }

        /// <summary>
        /// Saves the world's menu page: credits, shop theme, store, tiers and in-game products.
        /// </summary>
        public static Task<WorldResponse> SaveWorldPageAsync(string worldId, string pageJson, CancellationToken ct = default)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(pageJson ?? "{}");
            return ParelApiClient.PutBytesAsync<WorldResponse>($"/api/parelvr/worlds/{worldId}/page", body, "application/json", null, ct);
        }

        /// <summary>
        /// Uploads one picture of the world's menu page (slot = "shop-background" or "store/items/{id}/image").
        /// </summary>
        public static Task<WorldResponse> UploadWorldPageImageAsync(string worldId, string slot, byte[] imageData, string contentType, CancellationToken ct = default)
        {
            return ParelApiClient.PutBytesAsync<WorldResponse>($"/api/parelvr/worlds/{worldId}/page/{slot}", imageData, contentType, null, ct);
        }

        [Serializable]
        public struct CreateWorldRequest
        {
            public string blueprintId;
            public string name;
            public string description;
            public string authorName;
            public string releaseStatus;
            public List<string> tags;
            public int recommendedCapacity;
            public int maximumCapacity;
            public bool allowDebugging;
            public bool portalEnabled;
            public List<string> contentWarnings;
        }

        /// <summary>
        /// Creates a new world entry or updates an existing one (if blueprintId is provided).
        /// </summary>
        public static async Task<WorldRecord> CreateWorldAsync(CreateWorldRequest payload, CancellationToken ct = default)
        {
            UnityEngine.Debug.Log($"[WorldApiClient] CreateWorldAsync Request Payload:\n{UnityEngine.JsonUtility.ToJson(payload, true)}");
            var res = await ParelApiClient.PostJsonAsync<WorldResponse>("/api/parelvr/worlds", payload, ct);
            if (res != null) UnityEngine.Debug.Log($"[WorldApiClient] CreateWorldAsync Response:\n{UnityEngine.JsonUtility.ToJson(res, true)}");
            return res?.world;
        }
    }
}
