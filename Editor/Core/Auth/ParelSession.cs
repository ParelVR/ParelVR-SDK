using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;
using ParelVR.SDK.Core.Settings;
using UnityEditor;
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

        /// <summary>Why the last session ended by itself (the server stopped accepting it); empty after a sign-in or sign-out.</summary>
        public static string EndedReason { get; private set; } = string.Empty;

        public static event Action OnSessionChanged;

        // Read once on the main thread: Application.dataPath throws on any other.
        private static readonly string ProjectDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string LibraryDir =>
            Path.Combine(ProjectDir, "Library", LibraryFolder);

        private static string SessionFilePath =>
            Path.Combine(LibraryDir, "session.json");

        private static string LegacySessionFilePath =>
            Path.Combine(ProjectDir, "Library", LegacyFolder, "session.json");

        static ParelSession()
        {
            ParelPreferences.EnsureLoaded(); // the API environment the saved session belongs to
            MigrateLegacy();
            Restore();
        }

        /// <summary>Loads the saved session on the main thread as the editor starts, then checks the server still accepts it.</summary>
        [InitializeOnLoadMethod]
        private static void LoadOnEditorStart()
        {
            _ = IsLoggedIn;
            EditorApplication.delayCall += () => _ = ValidateAsync(force: true);
        }

        [Serializable]
        private sealed class StoredSession
        {
            public string playFabId;
            public string username;
            public string displayName;
            public string sessionTicket;
            public string rank;
            public string environment;
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
                // A ticket from another API environment is never accepted by this one.
                if (!string.IsNullOrEmpty(stored.environment) && stored.environment != ParelEnvironment.Current.ToString()) return;

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
            EndedReason = string.Empty;
            _validatedTicket = SessionTicket; // the server just handed it out
            _validatedAt = DateTime.UtcNow;

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
                    rank          = Rank,
                    environment   = ParelEnvironment.Current.ToString()
                };
                File.WriteAllText(path, JsonUtility.ToJson(stored));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not persist session: {ex.Message}");
            }

            OnSessionChanged?.Invoke();
        }

        public static void Clear() => ClearInternal(string.Empty);

        /// <summary>
        /// The server no longer accepts the session (it expired, or it was signed out elsewhere): forget it, so the
        /// control panel asks for a sign-in instead of showing empty lists and failing uploads.
        /// </summary>
        public static void Expire(string sentTicket, string reason)
        {
            if (string.IsNullOrEmpty(sentTicket) || sentTicket != SessionTicket) return; // already replaced
            Debug.LogWarning("[ParelVR SDK] " + reason);
            ClearInternal(reason);
        }

        private static string _validatedTicket = string.Empty;
        private static DateTime _validatedAt;
        private static Task<bool> _validating;
        private const double RecheckSeconds = 120;

        /// <summary>
        /// Asks the server whether the session is still good. False when it ended (the session is then cleared);
        /// true when it is good or the server could not be reached, since a network problem is no reason to sign out.
        /// </summary>
        public static Task<bool> ValidateAsync(bool force = false, CancellationToken ct = default)
        {
            if (!IsLoggedIn) return Task.FromResult(false);
            if (!force && _validatedTicket == SessionTicket && (DateTime.UtcNow - _validatedAt).TotalSeconds < RecheckSeconds)
                return Task.FromResult(true);
            return _validating ??= ValidateNowAsync(ct);
        }

        private static async Task<bool> ValidateNowAsync(CancellationToken ct)
        {
            string ticket = SessionTicket;
            await Task.Yield(); // never completes inside ValidateAsync, which keeps the task while it runs
            try
            {
                await ParelApiClient.GetRawAsync("/api/auth/profile", ct);
                _validatedTicket = ticket;
                _validatedAt = DateTime.UtcNow;
                return true;
            }
            catch (ParelApiException ex) when (ex.StatusCode == 0 || ex.StatusCode == 429 || ex.StatusCode >= 500)
            {
                return true;
            }
            catch (ParelApiException)
            {
                Expire(ticket, "Your ParelVR session has ended. Sign in again in the ParelVR control panel.");
                return false;
            }
            catch (OperationCanceledException)
            {
                return IsLoggedIn;
            }
            finally
            {
                _validating = null;
            }
        }

        /// <summary>Before a long build and upload: throws when there is no good session, so the work never starts for nothing.</summary>
        public static async Task EnsureValidAsync(CancellationToken ct = default)
        {
            if (!IsLoggedIn || !await ValidateAsync(true, ct))
                throw new ParelApiException("You are not signed in to ParelVR (the session ended). Sign in again in the control panel, then publish.", 401);
        }

        private static void ClearInternal(string reason)
        {
            EndedReason = reason ?? string.Empty;
            _validatedTicket = string.Empty;
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
