using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ParelVR.SDK.Core.Settings;
using UnityEngine;

namespace ParelVR.SDK.Core.Auth
{
    /// <summary>
    /// The SDK's own "stay signed in" store. It belongs to the creator tools only: it is not the game's saved login
    /// (that one lives under LocalLow/&lt;company&gt;/&lt;product&gt;/Saved Logins with its own key), so signing in or out of
    /// the game never touches it, and the other way round.
    ///
    /// It keeps the last sign-in (encrypted, tied to this computer) in the user's local app data, so it outlives a
    /// deleted Library folder and works in every Unity project on this computer. When the server stops accepting the
    /// session, the SDK signs back in with it on its own. Sign Out forgets it, so nothing signs back in after that.
    /// </summary>
    public static class ParelSavedLogin
    {
        // Ships in the SDK, so not a real secret: it keeps the file from being a plain password and, with the
        // per-computer id, from working when copied to another computer. Different from the game's key on purpose.
        private const string AppSecret = "ParelVR.SDK.SavedLogin.v1";

        [Serializable]
        private sealed class SavedLoginData
        {
            public string identifier;
            public string password;
            public string username;
            public string environment;
        }

        /// <summary>Per-user folder for SDK sign-in data: %LOCALAPPDATA%\ParelVR\SDK on Windows.</summary>
        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParelVR", "SDK");

        private static string FilePath => Path.Combine(Folder, "saved-login.dat");

        /// <summary>True when there is a saved sign-in for the API environment the SDK is pointed at.</summary>
        public static bool Exists => TryLoad(out _, out _, out _);

        public static bool TryLoad(out string identifier, out string password, out string username)
        {
            identifier = password = username = string.Empty;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return false;

                if (!TryDecrypt(File.ReadAllText(path), out string json))
                {
                    Debug.LogWarning("[ParelVR SDK] The saved sign-in could not be read (it was made on another computer or is damaged). Sign in again.");
                    Clear();
                    return false;
                }

                var data = JsonUtility.FromJson<SavedLoginData>(json);
                if (data == null || string.IsNullOrWhiteSpace(data.identifier) || string.IsNullOrEmpty(data.password)) return false;
                // A sign-in for another API environment is not used here; it stays for when the SDK points back there.
                if (!string.IsNullOrEmpty(data.environment) && data.environment != ParelEnvironment.Current.ToString()) return false;

                identifier = data.identifier;
                password = data.password;
                username = data.username ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ParelVR SDK] Could not read the saved sign-in: " + ex.Message);
                return false;
            }
        }

        public static void Save(string identifier, string password, string username)
        {
            if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(password)) return;
            try
            {
                Directory.CreateDirectory(Folder);
                var data = new SavedLoginData
                {
                    identifier = identifier.Trim(),
                    password = password,
                    username = username ?? string.Empty,
                    environment = ParelEnvironment.Current.ToString()
                };
                File.WriteAllText(FilePath, Encrypt(JsonUtility.ToJson(data)));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ParelVR SDK] Could not save the sign-in: " + ex.Message);
            }
        }

        public static void Clear()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ParelVR SDK] Could not forget the saved sign-in: " + ex.Message);
            }
        }

        private static string Encrypt(string plaintext)
        {
            using Aes aes = Aes.Create();
            aes.Key = DeriveKey();
            aes.GenerateIV();
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            byte[] plain = Encoding.UTF8.GetBytes(plaintext ?? string.Empty);
            byte[] cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            var combined = new byte[aes.IV.Length + cipher.Length];
            Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
            Buffer.BlockCopy(cipher, 0, combined, aes.IV.Length, cipher.Length);
            return Convert.ToBase64String(combined);
        }

        private static bool TryDecrypt(string base64, out string plaintext)
        {
            plaintext = string.Empty;
            try
            {
                byte[] combined = Convert.FromBase64String(base64.Trim());
                using Aes aes = Aes.Create();
                aes.Key = DeriveKey();
                var iv = new byte[aes.IV.Length];
                if (combined.Length <= iv.Length) return false;
                Buffer.BlockCopy(combined, 0, iv, 0, iv.Length);
                aes.IV = iv;
                using ICryptoTransform decryptor = aes.CreateDecryptor();
                byte[] plain = decryptor.TransformFinalBlock(combined, iv.Length, combined.Length - iv.Length);
                plaintext = Encoding.UTF8.GetString(plain);
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is ArgumentException)
            {
                return false;
            }
        }

        private static byte[] DeriveKey()
        {
            using SHA256 sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(AppSecret + "|" + SystemInfo.deviceUniqueIdentifier));
        }
    }
}
