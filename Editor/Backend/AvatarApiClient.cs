using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;

namespace ParelVR.SDK.Backend
{
    [Serializable]
    public class AvatarPlatformRecord
    {
        public string platform;
        public string bundleUrl;
        public long size;
        public string sha256;
        public string uploadedAt;
    }

    [Serializable]
    public class AvatarPlatformsRecord
    {
        public AvatarPlatformRecord windows;
        public AvatarPlatformRecord android;
    }

    [Serializable]
    public class AvatarPerformanceRecord
    {
        public string rank;
        public long downloadBytes;
        public long uncompressedBytes;
        public int triangleCount;
        public int materialCount;
        public int skinnedMeshCount;
    }

    [Serializable]
    public class AvatarRecord
    {
        public string id;
        public string name;
        public string description;
        public string ownerId;
        public string authorName;
        /// <summary>"private", "public", "friends-only" or "user-list".</summary>
        public string releaseStatus;
        public bool cloneable;
        public List<string> contentWarnings;
        public List<string> tags;
        public string thumbnailUrl;
        public AvatarPerformanceRecord performance;
        public AvatarPlatformsRecord platforms;
        public string createdAt;
        public string updatedAt;
        public bool isOwner;
        public bool canDelete;
    }

    [Serializable]
    public class AvatarListResponse
    {
        public bool success;
        public string scope;
        public List<AvatarRecord> avatars;
    }

    [Serializable]
    public class AvatarResponse
    {
        public bool success;
        public string message;
        public string thumbnailUrl;
        public AvatarRecord avatar;
    }

    /// <summary>
    /// The avatar endpoints on the ParelVR storage backend (/api/parelvr/avatars): the same flow the
    /// Worlds tab uses -- create (or update) the avatar record, upload the built bundle per platform,
    /// upload the thumbnail -- plus listing / deleting your uploads for the Content Manager.
    /// </summary>
    public static class AvatarApiClient
    {
        private const string Root = "/api/parelvr/avatars";

        [Serializable]
        public class AvatarUpsertRequest
        {
            public string name;
            public string description;
            public string authorName;
            public string releaseStatus = "private";
            public bool cloneable;
            public List<string> tags = new List<string>();
            public List<string> contentWarnings = new List<string>();
            public AvatarPerformanceRecord performance = new AvatarPerformanceRecord();
        }

        /// <summary>Avatars owned by the signed-in creator, newest first.</summary>
        public static async Task<List<AvatarRecord>> GetMyAvatarsAsync(CancellationToken ct = default)
        {
            var res = await ParelApiClient.GetJsonAsync<AvatarListResponse>($"{Root}?scope=mine&limit=200", ct);
            return res?.avatars ?? new List<AvatarRecord>();
        }

        /// <summary>One avatar by id (throws if it doesn't exist or isn't visible to you).</summary>
        public static async Task<AvatarRecord> GetAvatarAsync(string avatarId, CancellationToken ct = default)
        {
            var res = await ParelApiClient.GetJsonAsync<AvatarResponse>($"{Root}/{Uri.EscapeDataString(avatarId)}", ct);
            return res?.avatar;
        }

        /// <summary>Creates a new avatar record (the "blueprint").</summary>
        public static async Task<AvatarRecord> CreateAvatarAsync(AvatarUpsertRequest payload, CancellationToken ct = default)
        {
            var res = await ParelApiClient.PostJsonAsync<AvatarResponse>(Root, payload, ct);
            return res?.avatar;
        }

        /// <summary>Updates an existing avatar's details.</summary>
        public static async Task<AvatarRecord> UpdateAvatarAsync(string avatarId, AvatarUpsertRequest payload, CancellationToken ct = default)
        {
            var res = await ParelApiClient.PatchJsonAsync<AvatarResponse>($"{Root}/{Uri.EscapeDataString(avatarId)}", payload, ct);
            return res?.avatar;
        }

        public static async Task DeleteAvatarAsync(string avatarId, CancellationToken ct = default)
        {
            await ParelApiClient.DeleteJsonAsync<AvatarResponse>($"{Root}/{Uri.EscapeDataString(avatarId)}", ct);
        }

        /// <summary>Uploads a built avatar bundle for one platform ("windows" or "android").</summary>
        public static async Task<AvatarResponse> UploadBundleAsync(
            string avatarId,
            string platform,
            byte[] bundleData,
            IProgress<float> progress = null,
            CancellationToken ct = default)
        {
            string url = $"{Root}/{Uri.EscapeDataString(avatarId)}/bundle?platform={Uri.EscapeDataString(platform)}";
            return await ParelApiClient.PutBytesAsync<AvatarResponse>(url, bundleData, "application/octet-stream", progress, ct);
        }

        /// <summary>Uploads the avatar's thumbnail (PNG or JPEG bytes).</summary>
        public static async Task<AvatarResponse> UploadImageAsync(string avatarId, byte[] imageData, bool isPng, CancellationToken ct = default)
        {
            string ext = isPng ? "png" : "jpg";
            string url = $"{Root}/{Uri.EscapeDataString(avatarId)}/image?ext={ext}";
            return await ParelApiClient.PutBytesAsync<AvatarResponse>(url, imageData, isPng ? "image/png" : "image/jpeg", null, ct);
        }
    }
}
