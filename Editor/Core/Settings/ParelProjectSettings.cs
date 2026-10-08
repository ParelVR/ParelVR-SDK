using System;
using System.IO;
using UnityEngine;

namespace ParelVR.SDK.Core.Settings
{
    /// <summary>
    /// Persists which ParelProjectType this project is set to.
    /// Persisted to Library/ParelVR/project-settings.json.
    /// </summary>
    public static class ParelProjectSettings
    {
        private const string LibraryFolder = "ParelVR";
        private const ParelProjectType DefaultProjectType = ParelProjectType.World;

        public static ParelProjectType ProjectType { get; private set; } = DefaultProjectType;

        private static string SettingsFilePath =>
            Path.Combine(Application.dataPath, "..", "Library", LibraryFolder, "project-settings.json");

        static ParelProjectSettings()
        {
            MigrateLegacy();
            Restore();
        }

        internal static void ReloadForTests() => Restore();

        [Serializable]
        private sealed class StoredSettings
        {
            public string projectType;
        }

        private static void MigrateLegacy()
        {
            try
            {
                string legacy = Path.Combine(Application.dataPath, "..", "Library", "BuildKit", "project-settings.json");
                string current = SettingsFilePath;
                if (File.Exists(legacy) && !File.Exists(current))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(current));
                    File.Copy(legacy, current);
                }
            }
            catch { /* best-effort */ }
        }

        private static void Restore()
        {
            try
            {
                string path = SettingsFilePath;
                if (!File.Exists(path))
                {
                    ProjectType = DefaultProjectType;
                    return;
                }

                string json = File.ReadAllText(path);
                var stored = JsonUtility.FromJson<StoredSettings>(json);
                if (stored == null || string.IsNullOrEmpty(stored.projectType))
                {
                    ProjectType = DefaultProjectType;
                    return;
                }

                if (Enum.TryParse(stored.projectType, out ParelProjectType parsed))
                {
                    ProjectType = parsed;
                }
                else
                {
                    Debug.LogWarning($"[ParelVR SDK] Unrecognized project type '{stored.projectType}' -- defaulting to {DefaultProjectType}.");
                    ProjectType = DefaultProjectType;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not read project-settings.json -- defaulting to {DefaultProjectType}. {ex.Message}");
                ProjectType = DefaultProjectType;
            }
        }

        public static void Save(ParelProjectType projectType)
        {
            ProjectType = projectType;

            try
            {
                string path = SettingsFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var stored = new StoredSettings { projectType = projectType.ToString() };
                File.WriteAllText(path, JsonUtility.ToJson(stored));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Could not persist project-settings.json: {ex.Message}");
            }
        }
    }
}
