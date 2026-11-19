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
    /// session and its saved login -- a creator authoring in the Editor isn't the same thing as a
    /// player logged into the live client. Persisted per computer user in
    /// %LOCALAPPDATA%/ParelVR/SDK/session.json (next to <see cref="ParelSavedLogin"/>), so it
    /// survives a deleted Library folder and is shared by every project. When the server stops
    /// accepting it, the SDK signs back in with the saved sign-in; only Sign Out ends it for good.
    /// </summary>
    public static class ParelSession
    {
        private const string LibraryFolder = "ParelVR";
        private const string LegacyFolder  = "BuildKit";
        private const string StartupSignInKey = "ParelVR.SDK.SavedSignInTried";

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

        private static string SessionFilePath =>
            Path.Combine(ParelSavedLogin.Folder, "session.json");

        // Where older SDKs kept the session, per project.
        private static readonly string[] LegacySessionFilePaths =
        {
            Path.Combine(ProjectDir, "Library", LibraryFolder, "session.json"),
            Path.Combine(ProjectDir, "Library", LegacyFolder, "session.json"),
        };

        static ParelSession()
        {
            ParelPreferences.EnsureLoaded(); // the API environment the saved session belongs to
            MigrateLegacy();
            Restore();
        }

        /// <summary>
        /// Loads the saved session on the main thread as the editor starts, then checks the server still accepts it.
        /// With no session but a saved sign-in, signs in with that (once per editor launch, not on every script reload).
        /// </summary>
        [InitializeOnLoadMethod]
        private static void LoadOnEditorStart()
        {
            _ = IsLoggedIn;
            EditorApplication.delayCall += () =>
            {
                if (IsLoggedIn)
                {
                    _ = ValidateAsync(force: true);
                    return;
                }
                if (SessionState.GetBool(StartupSignInKey, false)) return;
                SessionState.SetBool(StartupSignInKey, true);
                _ = ParelAuth.TrySavedLoginAsync();
            };
        }

        /// <summary>Tells the control panel to redraw (e.g. a saved sign-in now waits on a 2FA code).</summary>
        internal static void NotifyChanged() => OnSessionChanged?.Invoke();

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

        /// <summary>
        /// Moves a session an older SDK kept in this project's Library into the per-user SDK folder (when that has none
        /// yet), then removes the Library copy so there is one session only.
        /// </summary>
        private static void MigrateLegacy()
        {
            try
            {
                string current = SessionFilePath;
                foreach (string legacy in LegacySessionFilePaths)
                {
                    if (!File.Exists(legacy)) continue;
                    if (!File.Exists(current))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(current));
                        File.Copy(legacy, current);
                        Debug.Log("[ParelVR SDK] Moved the SDK session out of this project's Library, so it stays signed in across projects.");
                    }
                    File.Delete(legacy);
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
        /// The server no longer accepts the session (it expired, or it was signed out elsewhere). Signs back in with the
        /// saved SDK sign-in when there is one; otherwise forgets the session, so the control panel asks for a sign-in
        /// instead of showing empty lists and failing uploads.
        /// </summary>
        public static void Expire(string sentTicket, string reason) => _ = RecoverAsync(sentTicket, reason);

        private static Task<bool> _recovering;
        private static string _recoveringTicket = string.Empty;

        /// <summary>
        /// Same as <see cref="Expire"/>, awaitable: true when a good session is in place afterwards (signed back in, or
        /// the ticket had already been replaced), so the caller can send its request once more.
        /// </summary>
        public static Task<bool> RecoverAsync(string sentTicket, string reason)
        {
            if (string.IsNullOrEmpty(sentTicket)) return Task.FromResult(false);
            if (_recovering != null && _recoveringTicket == sentTicket) return _recovering;
            if (sentTicket != SessionTicket) return Task.FromResult(IsLoggedIn); // already replaced
            _recoveringTicket = sentTicket;
            return _recovering = RecoverNowAsync(sentTicket, reason);
        }

        private static async Task<bool> RecoverNowAsync(string sentTicket, string reason)
        {
            await Task.Yield(); // never completes inside RecoverAsync, which keeps the task while it runs
            try
            {
                if (await ParelAuth.TrySavedLoginAsync())
                {
                    Debug.Log("[ParelVR SDK] The server ended the SDK session; signed back in with the saved sign-in.");
                    return true;
                }
                if (sentTicket == SessionTicket)
                {
                    Debug.LogWarning("[ParelVR SDK] " + reason);
                    ClearInternal(reason);
                }
                return IsLoggedIn;
            }
            finally
            {
                _recovering = null;
                _recoveringTicket = string.Empty;
            }
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
                await ParelApiClient.GetRawAsync("/api/auth/profile", ct); // signs back in and asks again on a refused ticket
                _validatedTicket = SessionTicket;
                _validatedAt = DateTime.UtcNow;
                return IsLoggedIn;
            }
            catch (ParelApiException ex) when (ex.StatusCode == 0 || ex.StatusCode == 429 || ex.StatusCode >= 500)
            {
                return IsLoggedIn;
            }
            catch (ParelApiException)
            {
                return await RecoverAsync(ticket, "Your ParelVR session has ended. Sign in again in the ParelVR control panel.");
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
            if (!IsLoggedIn) await ParelAuth.TrySavedLoginAsync(ct);
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
