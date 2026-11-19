using System;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;
using UnityEngine;

namespace ParelVR.SDK.Core.Auth
{
    [Serializable]
    public sealed class AuthResponse
    {
        public bool success;
        public string userId;
        public string sessionTicket;
        public string username;
        public string displayName;
        public string createdAt;
        public string email;
        public string rank;
        public string message;
        public bool requiresTotp;
        public string pendingToken;
        public string reason;
    }

    [Serializable]
    internal sealed class AuthLoginRequest
    {
        public string identifier;
        public string password;
    }

    [Serializable]
    internal sealed class AuthEmptyRequest { }

    /// <summary>Login/logout against /api/auth/* endpoints.</summary>
    public static class ParelAuth
    {
        // The sign-in waiting on its 2FA code: saved once the code goes through.
        private static string _totpIdentifier = string.Empty;
        private static string _totpPassword = string.Empty;

        /// <summary>
        /// Set when a saved sign-in needs a 2FA code to finish (it cannot be done without the creator): the control
        /// panel then asks for just the code. Empty otherwise.
        /// </summary>
        public static string SavedLoginTotpToken { get; private set; } = string.Empty;
        public static string SavedLoginTotpUser { get; private set; } = string.Empty;

        public static async Task<AuthResponse> LoginAsync(string usernameOrEmail, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrWhiteSpace(password))
                throw new ParelApiException("Enter your username/email and password.", 0);

            var request = new AuthLoginRequest { identifier = usernameOrEmail.Trim(), password = password };
            AuthResponse result = await ParelApiClient.PostJsonAsync<AuthResponse>("/api/auth/login", request, ct);

            if (result == null || !result.success)
                throw new ParelApiException(result?.message ?? "Sign in failed.", 401);

            // If TOTP is required, return the result without applying session
            if (result.requiresTotp)
            {
                _totpIdentifier = request.identifier;
                _totpPassword = password;
                return result;
            }

            ParelSession.Apply(result.userId, result.username, result.displayName, result.sessionTicket, result.rank);
            ParelSavedLogin.Save(request.identifier, password, result.username);
            ClearPendingTotp();
            return result;
        }

        /// <summary>Signs out and forgets the saved sign-in, so the SDK does not sign back in by itself.</summary>
        public static void Logout() => Logout(forgetSavedLogin: true);

        /// <summary>
        /// Ends the session with the server. forgetSavedLogin false keeps the saved sign-in (used when switching API
        /// environment: the saved sign-in belongs to one environment and is used again when the SDK points back there).
        /// </summary>
        public static void Logout(bool forgetSavedLogin)
        {
            if (forgetSavedLogin) ParelSavedLogin.Clear();
            ClearPendingTotp();
            if (ParelSession.IsLoggedIn)
                _ = PostLogoutAsync();
            ParelSession.Clear();
        }

        private static async Task PostLogoutAsync()
        {
            try { await ParelApiClient.PostJsonAsync<AuthResponse>("/api/auth/logout", new AuthEmptyRequest()); }
            catch (Exception) { } // the session is forgotten here either way
        }

        public static async Task<AuthResponse> LoginTotpAsync(string pendingToken, string code, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(pendingToken) || string.IsNullOrWhiteSpace(code))
                throw new ParelApiException("Enter your 2FA code.", 0);

            var request = new AuthTotpRequest { pendingToken = pendingToken, code = code.Trim() };
            AuthResponse result = await ParelApiClient.PostJsonAsync<AuthResponse>("/api/auth/login/totp", request, ct);

            if (result == null || !result.success)
                throw new ParelApiException(result?.message ?? "2FA verification failed.", 401);

            ParelSession.Apply(result.userId, result.username, result.displayName, result.sessionTicket, result.rank);
            if (!string.IsNullOrEmpty(_totpIdentifier)) ParelSavedLogin.Save(_totpIdentifier, _totpPassword, result.username);
            ClearPendingTotp();
            return result;
        }

        /// <summary>Drops a 2FA step that was waiting (the creator cancelled it, or signed in another way).</summary>
        public static void ClearPendingTotp()
        {
            _totpIdentifier = string.Empty;
            _totpPassword = string.Empty;
            SavedLoginTotpToken = string.Empty;
            SavedLoginTotpUser = string.Empty;
        }

        private static Task<bool> _savedLogin;

        /// <summary>
        /// Signs in with the SDK's saved sign-in, without asking. True when a session is in place afterwards. A saved
        /// sign-in the server turns down (password changed, account gone) is forgotten; one that only failed to reach
        /// the server is kept for next time. Accounts with 2FA stop at the code (see SavedLoginTotpToken).
        /// </summary>
        public static Task<bool> TrySavedLoginAsync(CancellationToken ct = default)
        {
            if (!ParelSavedLogin.Exists) return Task.FromResult(false);
            return _savedLogin ??= SavedLoginNowAsync(ct);
        }

        private static async Task<bool> SavedLoginNowAsync(CancellationToken ct)
        {
            await Task.Yield(); // never completes inside TrySavedLoginAsync, which keeps the task while it runs
            try
            {
                if (!ParelSavedLogin.TryLoad(out string identifier, out string password, out string username)) return false;

                var request = new AuthLoginRequest { identifier = identifier, password = password };
                AuthResponse result = await ParelApiClient.PostJsonAsync<AuthResponse>("/api/auth/login", request, ct);
                if (result == null || !result.success)
                {
                    Debug.LogWarning("[ParelVR SDK] The saved sign-in was turned down" + (string.IsNullOrEmpty(result?.message) ? "." : ": " + result.message) + " Sign in again.");
                    ParelSavedLogin.Clear();
                    return false;
                }

                if (result.requiresTotp)
                {
                    _totpIdentifier = identifier;
                    _totpPassword = password;
                    SavedLoginTotpToken = result.pendingToken ?? string.Empty;
                    SavedLoginTotpUser = string.IsNullOrEmpty(username) ? identifier : username;
                    ParelSession.NotifyChanged(); // the control panel switches to the 2FA code
                    return false;
                }

                ParelSession.Apply(result.userId, result.username, result.displayName, result.sessionTicket, result.rank);
                Debug.Log("[ParelVR SDK] Signed in with the saved SDK sign-in as " + (string.IsNullOrEmpty(result.username) ? identifier : result.username) + ".");
                return true;
            }
            catch (ParelApiException ex) when (ex.StatusCode == 0 || ex.StatusCode == 408 || ex.StatusCode == 429 || ex.StatusCode >= 500)
            {
                Debug.LogWarning("[ParelVR SDK] Could not reach ParelVR to sign in with the saved sign-in (" + ex.Message + "). It is kept for next time.");
                return false;
            }
            catch (ParelApiException ex)
            {
                Debug.LogWarning("[ParelVR SDK] The saved sign-in was turned down (" + ex.Message + "). Sign in again.");
                ParelSavedLogin.Clear();
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _savedLogin = null;
            }
        }
    }

    [Serializable]
    internal sealed class AuthTotpRequest
    {
        public string pendingToken;
        public string code;
    }
}
