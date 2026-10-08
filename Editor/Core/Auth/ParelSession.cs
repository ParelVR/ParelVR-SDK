using System;
using System.IO;
using UnityEngine;

namespace ParelVR.SDK.Core.Auth
{
    /// <summary>
    /// ParelVR SDK's own login session. Deliberately separate from the main game's PlayerAPI
    /// session -- a creator authoring in the Editor isn't the same thing as a player logged
    /// into the live client. Persisted to Library/ParelVR/session.json (per-project, always
    /// gitignored by Unity's default .gitignore).
    /// </summary>
    public static class ParelSession
    {
        private const string LibraryFolder = "ParelVR";
        private const string LegacyFolder  = "BuildKit";

        public static string SessionTicket { get; private set; } = string.Empty;
        public static string UserId        { get; private set; } = string.Empty;
        public static string Username      { get; private set; } = string.Empty;
        public static string DisplayName   { get; private set; } = string.Empty;
        public static string Rank          { get; private set; } = string.Empty;

        public static bool IsLoggedIn => !string.IsNullOrEmpty(SessionTicket) && !string.IsNullOrEmpty(UserId);

        public static event Action OnSessionChanged;

        private static string LibraryDir =>
            Path.Combine(Application.dataPath, "..", "Library", LibraryFolder);

        private static string SessionFilePath =>
            Path.Combine(LibraryDir, "session.json");

        private static string LegacySessionFilePath =>
            Path.Combine(Application.dataPath, "..", "Library", LegacyFolder, "session.json");

        static ParelSession()
        {
            MigrateLegacy();
            Restore();
        }

        [Serializable]
        private sealed class StoredSession
        {
            public string playFabId;
            public string username;
            public string displayName;
            public string sessionTicket;
            public string rank;
        }

        /// <summary>Migrates session from the old BuildKit path if the new path doesn't exist yet.</summary>
        private static void MigrateLegacy()
        {
            try
            {
                string legacy = LegacySessionFilePath;
                string current = SessionFilePath;
                if (File.Exists(legacy) && !File.Exists(current))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(current));
                    File.Copy(legacy, current);
                    Debug.Log("[ParelVR SDK] Migrated session from BuildKit to ParelVR.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Legacy session migration failed: {ex.Message}");
            }
        }

        private static void Restore()
        {
            try
            {
                string path = SessionFilePath;
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path);
                var stored = JsonUtility.FromJson<StoredSession>(json);
                if (stored == null || string.IsNullOrEmpty(stored.sessionTicket)) return;

                UserId      = stored.playFabId ?? string.Empty;
                Username    = stored.username ?? string.Empty;
                DisplayName = stored.displayName ?? string.Empty;
                SessionTicket = stored.sessionTicket ?? string.Empty;
                Rank        = stored.rank ?? string.Empty;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not restore saved session: {ex.Message}");
            }
        }

        public static void Apply(string userId, string username, string displayName, string sessionTicket, string rank)
        {
            UserId      = userId ?? string.Empty;
            Username    = username ?? string.Empty;
            DisplayName = string.IsNullOrEmpty(displayName) ? Username : displayName;
            SessionTicket = sessionTicket ?? string.Empty;
            Rank        = rank ?? string.Empty;

            try
            {
                string path = SessionFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var stored = new StoredSession
                {
                    playFabId     = UserId,
                    username      = Username,
                    displayName   = DisplayName,
                    sessionTicket = SessionTicket,
                    rank          = Rank
                };
                File.WriteAllText(path, JsonUtility.ToJson(stored));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not persist session: {ex.Message}");
            }

            OnSessionChanged?.Invoke();
        }

        public static void Clear()
        {
            UserId      = string.Empty;
            Username    = string.Empty;
            DisplayName = string.Empty;
            SessionTicket = string.Empty;
            Rank        = string.Empty;

            try
            {
                string path = SessionFilePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not clear persisted session: {ex.Message}");
            }

            OnSessionChanged?.Invoke();
        }
    }
}
