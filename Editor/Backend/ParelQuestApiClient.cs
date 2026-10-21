using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;

namespace ParelVR.SDK.Backend
{
    /// <summary>Where the Meta / Standalone VR version of a world or avatar is at, as the server reports it.</summary>
    [Serializable]
    public class ParelQuestConversionRecord
    {
        /// <summary>"queued", "converting", "done", "held", "failed" or "waiting" (the server needs an update for this SDK).</summary>
        public string status;
        public string message;
        public string summary;
        public string requestedAt;
        public string updatedAt;
        public string finishedAt;

        public bool InProgress => status == "queued" || status == "converting" || status == "waiting";
        public bool Failed => status == "failed";
        public bool Done => status == "done";

        /// <summary>A short line for pills and the Builder, e.g. "Converting".</summary>
        public string Label
        {
            get
            {
                switch (status)
                {
                    case "queued": return "Queued";
                    case "converting": return "Converting";
                    case "waiting": return "Waiting for server update";
                    case "done": return "Ready";
                    case "held": return "Safety check";
                    case "failed": return "Failed";
                    default: return string.IsNullOrEmpty(status) ? "Not made yet" : status;
                }
            }
        }
    }

    /// <summary>
    /// Platform picking and the Meta / Standalone VR conversion. The SDK sends what the PC bundle was built from; ParelVR's
    /// servers build the standalone version from it and file it under the "android" platform of the world or avatar.
    /// </summary>
    public static class ParelQuestApiClient
    {
        public const string Worlds = "worlds";
        public const string Avatars = "avatars";

        /// <summary>The proxy in front of the API refuses bodies over 100 MB, so packages go up in parts.</summary>
        private const int ChunkBytes = 32 * 1024 * 1024;
        public const long SourceLimitBytes = 1024L * 1024 * 1024;

        [Serializable]
        private sealed class TargetsRequest
        {
            public List<string> targets;
        }

        [Serializable]
        public sealed class QuestResponse
        {
            public bool success;
            public string message;
            public List<string> targetPlatforms;
            public ParelQuestConversionRecord questConversion;
        }

        /// <summary>Which platforms the content is available on ("pc", "standalone"). A platform left out stops being offered.</summary>
        public static Task<QuestResponse> SetTargetsAsync(string kind, string id, List<string> targets, CancellationToken ct = default)
        {
            return ParelApiClient.PutJsonAsync<QuestResponse>($"/api/parelvr/{kind}/{Uri.EscapeDataString(id)}/platforms",
                new TargetsRequest { targets = targets ?? new List<string>() }, ct);
        }

        public static Task<QuestResponse> GetStatusAsync(string kind, string id, CancellationToken ct = default)
        {
            return ParelApiClient.GetJsonAsync<QuestResponse>($"/api/parelvr/quest/{kind}/{Uri.EscapeDataString(id)}", ct);
        }

        /// <summary>
        /// Uploads the package the standalone version is built from (and, for worlds with scripts, the PC Volt package
        /// the server re-issues for it), which queues the conversion. Progress runs 0..1 over both files.
        /// </summary>
        public static async Task<QuestResponse> UploadSourceAsync(string kind, string id, string packagePath, string voltPackagePath,
            string sdkVersion, IProgress<float> progress = null, CancellationToken ct = default)
        {
            long packageSize = new FileInfo(packagePath).Length;
            if (packageSize > SourceLimitBytes)
                throw new Exception($"The Meta / Standalone VR package is {packageSize / (1024 * 1024)} MB; the limit is {SourceLimitBytes / (1024 * 1024)} MB. Lower texture sizes or leave unused assets out of the scene.");
            bool hasVolt = !string.IsNullOrEmpty(voltPackagePath) && File.Exists(voltPackagePath);
            long voltSize = hasVolt ? new FileInfo(voltPackagePath).Length : 0;
            long total = Math.Max(1, packageSize + voltSize);
            string uploadId = Guid.NewGuid().ToString("N");
            string root = $"/api/parelvr/quest/{kind}/{Uri.EscapeDataString(id)}/source";

            if (hasVolt)
            {
                await UploadPartAsync(root, uploadId, "volt", voltPackagePath, sdkVersion,
                    new Progress<float>(p => progress?.Report(p * voltSize / total)), ct);
            }
            return await UploadPartAsync(root, uploadId, "package", packagePath, sdkVersion,
                new Progress<float>(p => progress?.Report((voltSize + p * packageSize) / total)), ct);
        }

        private static async Task<QuestResponse> UploadPartAsync(string root, string uploadId, string part, string path, string sdkVersion,
            IProgress<float> progress, CancellationToken ct)
        {
            long size = new FileInfo(path).Length;
            int chunks = (int)Math.Max(1, (size + ChunkBytes - 1) / ChunkBytes);
            string sha;
            using (var stream = File.OpenRead(path))
            {
                using (SHA256 hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                stream.Position = 0;
                var buffer = new byte[ChunkBytes];
                for (int index = 0; index < chunks; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = stream.Read(buffer, read, buffer.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    byte[] body = buffer;
                    if (read < buffer.Length)
                    {
                        body = new byte[read];
                        Buffer.BlockCopy(buffer, 0, body, 0, read);
                    }
                    int done = index;
                    await ParelApiClient.PutBytesRawAsync(
                        $"{root}/chunk?uploadId={uploadId}&part={part}&index={index}&total={chunks}",
                        body, "application/octet-stream",
                        new Progress<float>(p => progress?.Report(Math.Min(1f, (done + p) / chunks))), ct);
                }
            }
            return await ParelApiClient.PostJsonAsync<QuestResponse>(
                $"{root}/complete?uploadId={uploadId}&part={part}&total={chunks}&sha256={sha}&sdk={Uri.EscapeDataString(sdkVersion ?? string.Empty)}",
                null, ct);
        }
    }
}
