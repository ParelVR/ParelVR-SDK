using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;

namespace ParelVR.SDK.Backend
{
    /// <summary>A world's or avatar's protection, as its uploader sees it.</summary>
    [Serializable]
    public class ParelCopyrightStatus
    {
        public bool success;
        public string message;
        public bool isOwner;
        /// <summary>"protected", "unprotected", "revoked" or "barred" (owner); others only get <see cref="protectedBy"/>.</summary>
        public string status;
        public string keyId;
        public string protectedBy;
        public int version;
        public bool hasSource;
        public string sourceAt;
    }

    /// <summary>A protection key, as whoever asks sees it.</summary>
    [Serializable]
    public class ParelProtectedKeyInfo
    {
        public bool success;
        public string message;
        public bool valid;
        public string keyId;
        public string kind;
        public string contentId;
        public string name;
        public string owner;
        /// <summary>"owner", "whitelisted", "pending", "blacklisted" or "none" (signed out: "none").</summary>
        public string access;
        public bool hasSource;

        public bool MayPublish => access == "owner" || access == "whitelisted";
    }

    /// <summary>The signed header ParelVR returns once the real files are sealed.</summary>
    [Serializable]
    public class ParelProtectedSealResponse
    {
        public bool success;
        public string message;
        public string h;
        public string sig;
    }

    [Serializable]
    public class ParelLockedBuildResponse
    {
        public bool success;
        public string message;
        public ParelQuestConversionRecord protectedBuild;
    }

    /// <summary>Protected content: sealing the creator's real files, checking a buyer's access, server builds.</summary>
    public static class ParelCopyrightApiClient
    {
        public static Task<ParelCopyrightStatus> GetStatusAsync(string kind, string id, CancellationToken ct = default) =>
            ParelApiClient.GetJsonAsync<ParelCopyrightStatus>($"/api/parelvr/copyright/{kind}/{Uri.EscapeDataString(id)}", ct);

        /// <summary>
        /// Sends the creator's real files to ParelVR, which seals them and signs the header of the stand-in package
        /// (<paramref name="standInSha256"/>) that goes inside the .pvrprotected file.
        /// </summary>
        public static Task<ParelProtectedSealResponse> SealSourceAsync(string kind, string id, string sourcePackage, string standInSha256,
            long standInSize, string sdkVersion, IProgress<float> progress = null, CancellationToken ct = default)
        {
            return ParelQuestApiClient.UploadFileAsync<ParelProtectedSealResponse>(
                $"/api/parelvr/copyright/{kind}/{Uri.EscapeDataString(id)}/source", Guid.NewGuid().ToString("N"), string.Empty,
                $"standInSha256={standInSha256}&standInSize={standInSize}&sdk={Uri.EscapeDataString(sdkVersion ?? string.Empty)}",
                sourcePackage, progress, ct);
        }

        [Serializable]
        private sealed class VerifyRequest
        {
            public string h;
            public string sig;
        }

        /// <summary>Checks a .pvrprotected header's signature, and what the signed-in user may do with it.</summary>
        public static Task<ParelProtectedKeyInfo> VerifyAsync(string header, string signature, CancellationToken ct = default) =>
            ParelApiClient.PostJsonAsync<ParelProtectedKeyInfo>("/api/parelvr/copyright/verify", new VerifyRequest { h = header, sig = signature }, ct);

        public static Task<ParelProtectedKeyInfo> GetKeyAsync(string keyId, CancellationToken ct = default) =>
            ParelApiClient.GetJsonAsync<ParelProtectedKeyInfo>($"/api/parelvr/copyright/key/{Uri.EscapeDataString(keyId)}", ct);

        /// <summary>Puts the signed-in user on the creator's pending list (the creator is told).</summary>
        public static Task<ParelProtectedKeyInfo> RequestAccessAsync(string keyId, CancellationToken ct = default) =>
            ParelApiClient.PostJsonAsync<ParelProtectedKeyInfo>($"/api/parelvr/copyright/key/{Uri.EscapeDataString(keyId)}/request", null, ct);

        /// <summary>
        /// Sends an avatar that uses protected stand-ins; ParelVR's build server swaps in the real files and builds it for
        /// <paramref name="targets"/> ("pc", "standalone").
        /// </summary>
        public static Task<ParelLockedBuildResponse> UploadLockedSourceAsync(string avatarId, string packagePath, IEnumerable<string> keys,
            IEnumerable<string> targets, string sdkVersion, IProgress<float> progress = null, CancellationToken ct = default)
        {
            return ParelQuestApiClient.UploadFileAsync<ParelLockedBuildResponse>(
                $"/api/parelvr/locked/avatars/{Uri.EscapeDataString(avatarId)}/source", Guid.NewGuid().ToString("N"), string.Empty,
                $"keys={Uri.EscapeDataString(string.Join(",", keys))}&targets={Uri.EscapeDataString(string.Join(",", targets))}&sdk={Uri.EscapeDataString(sdkVersion ?? string.Empty)}",
                packagePath, progress, ct);
        }

        public static Task<ParelLockedBuildResponse> GetLockedBuildAsync(string avatarId, CancellationToken ct = default) =>
            ParelApiClient.GetJsonAsync<ParelLockedBuildResponse>($"/api/parelvr/locked/avatars/{Uri.EscapeDataString(avatarId)}", ct);
    }
}
