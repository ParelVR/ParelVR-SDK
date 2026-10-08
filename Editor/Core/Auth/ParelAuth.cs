using System;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Http;

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
                return result;

            ParelSession.Apply(result.userId, result.username, result.displayName, result.sessionTicket, result.rank);
            return result;
        }

        public static void Logout()
        {
            _ = ParelApiClient.PostJsonAsync<AuthResponse>("/api/auth/logout", new AuthEmptyRequest());
            ParelSession.Clear();
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
            return result;
        }
    }

    [Serializable]
    internal sealed class AuthTotpRequest
    {
        public string pendingToken;
        public string code;
    }
}
