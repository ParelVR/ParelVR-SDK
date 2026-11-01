using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;

namespace ParelVR.SDK.Backend
{
    [Serializable]
    public class UploadRequestPerson
    {
        public string pvrId;
        public string username;
        public string displayName;
        public string avatarUrl;

        public string Label => string.IsNullOrWhiteSpace(displayName) ? (username ?? "the other player") : displayName;
    }

    /// <summary>A request to upload an avatar or world to another player's account, as the requester sees it.</summary>
    [Serializable]
    public class UploadRequestRecord
    {
        public string id;
        public string kind;
        public string status; // staging, pending, approved, completed, denied, cancelled, expired
        public string role;
        public string title;
        public long sizeBytes;
        public UploadRequestPerson requester;
        public UploadRequestPerson target;
        public string createdAt;
        public string submittedAt;
        public string expiresAt;
        public string decidedAt;
        public string approvedUntil;
        public string contentId;
        public bool hasBuild;
    }

    [Serializable]
    public class UploadRequestResponse
    {
        public bool success;
        public string message;
        public UploadRequestRecord request;
    }

    /// <summary>
    /// "Upload for someone else": the request, the held build and pictures, and the steps after the other
    /// player approves. While approved, the normal avatar and world routes accept the requester for the item
    /// the request created.
    /// </summary>
    public static class UploadRequestApiClient
    {
        private const string Root = "/api/parelvr/upload-requests";

        [Serializable]
        public class CreateBody
        {
            public string kind;
            public string targetParelId;
            public string title;
            public string description;
            public long declaredBytes;
            public List<string> platforms = new List<string>();
            public Stats stats = new Stats();
            public Client client = new Client();
        }

        [Serializable]
        public class Stats
        {
            public long triangles;
            public long materials;
            public long meshes;
            public long textures;
            public long textureBytes;
            public string rank;
        }

        [Serializable]
        public class Client
        {
            public string sdkVersion;
            public string unityVersion;
            public string os;
            public string deviceId;
        }

        private static string Path(string id, string step) => $"{Root}/{Uri.EscapeDataString(id)}{(string.IsNullOrEmpty(step) ? string.Empty : "/" + step)}";

        public static async Task<UploadRequestRecord> CreateAsync(CreateBody body, CancellationToken ct = default) =>
            (await ParelApiClient.PostJsonAsync<UploadRequestResponse>(Root, body, ct))?.request;

        public static async Task<UploadRequestRecord> GetAsync(string id, CancellationToken ct = default) =>
            (await ParelApiClient.GetJsonAsync<UploadRequestResponse>(Path(id, null), ct))?.request;

        /// <summary>The PC build, held by ParelVR until the other player answers. Sent in parts like any bundle.</summary>
        public static async Task<UploadRequestRecord> SendBuildAsync(string id, string platform, byte[] data, IProgress<float> progress, CancellationToken ct = default) =>
            (await ParelApiClient.PutChunkedAsync<UploadRequestResponse>(Path(id, "bundle"), "platform=" + platform, data, progress, ct))?.request;

        /// <summary>role: geometry, textures or combined. JPEG or PNG.</summary>
        public static Task SendPictureAsync(string id, string role, byte[] image, CancellationToken ct = default) =>
            ParelApiClient.PutBytesAsync<UploadRequestResponse>(Path(id, "images/" + role), image, "image/jpeg", null, ct);

        public static async Task<UploadRequestRecord> SubmitAsync(string id, CancellationToken ct = default) =>
            (await ParelApiClient.PostJsonAsync<UploadRequestResponse>(Path(id, "submit"), null, ct))?.request;

        public static async Task<UploadRequestRecord> CancelAsync(string id, CancellationToken ct = default) =>
            (await ParelApiClient.PostJsonAsync<UploadRequestResponse>(Path(id, "cancel"), null, ct))?.request;

        public static async Task<UploadRequestRecord> CompleteAsync(string id, CancellationToken ct = default) =>
            (await ParelApiClient.PostJsonAsync<UploadRequestResponse>(Path(id, "complete"), null, ct))?.request;

        /// <summary>
        /// Puts the held PC build live on the item the request created, instead of uploading it a second time.
        /// The answer is shaped like the avatar or world bundle upload's (AvatarResponse / WorldResponse).
        /// A 410 (code build-gone) means the held copy is no longer there: upload the bundle normally.
        /// </summary>
        public static Task<T> PublishHeldBuildAsync<T>(string id, CancellationToken ct = default) where T : class =>
            ParelApiClient.PostJsonAsync<T>(Path(id, "publish-bundle"), null, ct);
    }
}
