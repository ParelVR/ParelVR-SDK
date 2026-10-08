using System;
using System.IO;
using UnityEngine;
using ParelVR.SDK.Core.Settings;

namespace ParelVR.SDK.Core.Settings
{
    /// <summary>
    /// Creator preferences. Persisted to Library/ParelVR/preferences.json.
    /// </summary>
    public static class ParelPreferences
    {
        private const string LibraryFolder = "ParelVR";

        public static bool AutoPortContentEnabled { get; private set; }

        /// <summary>When a component is added that needs other components to work, add those too. On unless switched off.</summary>
        public static bool AutoAddReferencedScripts { get; private set; } = true;

        public static event Action OnPreferencesChanged;

        private static string PreferencesFilePath =>
            Path.Combine(Application.dataPath, "..", "Library", LibraryFolder, "preferences.json");

        static ParelPreferences()
        {
            Restore();
        }

        [Serializable]
        private sealed class StoredPreferences
        {
            public bool autoPortContentEnabled;
            public bool autoAddReferencedScriptsOff; // stored as "off" so a file written before this setting existed reads as on
            public string environment;
        }

        private static void Restore()
        {
            try
            {
                string path = PreferencesFilePath;
                if (!File.Exists(path))
                {
                    AutoPortContentEnabled = false;
                    AutoAddReferencedScripts = true;
                    ParelEnvironment.Current = ParelEnvironmentType.Production;
                    return;
                }

                string json = File.ReadAllText(path);
                var stored = JsonUtility.FromJson<StoredPreferences>(json);
                AutoPortContentEnabled = stored != null && stored.autoPortContentEnabled;
                AutoAddReferencedScripts = stored == null || !stored.autoAddReferencedScriptsOff;

                if (stored != null && !string.IsNullOrEmpty(stored.environment)
                    && Enum.TryParse(stored.environment, out ParelEnvironmentType env))
                {
                    ParelEnvironment.Current = env;
                }
                else
                {
                    ParelEnvironment.Current = ParelEnvironmentType.Production;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not read preferences.json: {ex.Message}");
                AutoPortContentEnabled = false;
                ParelEnvironment.Current = ParelEnvironmentType.Production;
            }
        }

        public static void SetAutoPortContentEnabled(bool enabled)
        {
            if (AutoPortContentEnabled == enabled) return;
            AutoPortContentEnabled = enabled;
            Save();
            OnPreferencesChanged?.Invoke();
        }

        public static void SetAutoAddReferencedScripts(bool enabled)
        {
            if (AutoAddReferencedScripts == enabled) return;
            AutoAddReferencedScripts = enabled;
            Save();
            OnPreferencesChanged?.Invoke();
        }

        public static void SetEnvironment(ParelEnvironmentType env)
        {
            if (ParelEnvironment.Current == env) return;
            ParelEnvironment.Current = env;
            Save();
            OnPreferencesChanged?.Invoke();
        }

        private static void Save()
        {
            try
            {
                string path = PreferencesFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var stored = new StoredPreferences
                {
                    autoPortContentEnabled = AutoPortContentEnabled,
                    autoAddReferencedScriptsOff = !AutoAddReferencedScripts,
                    environment = ParelEnvironment.Current.ToString()
                };
                File.WriteAllText(path, JsonUtility.ToJson(stored));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not persist preferences.json: {ex.Message}");
            }
        }

        /// <summary>Internal, for tests only (via InternalsVisibleTo).</summary>
        internal static void ReloadForTests() => Restore();
    }
}
