using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace ParelVR.SDK.Build
{
    /// <summary>
    /// The scenes of a world: every enabled scene in the Build Profiles scene list goes into the one world bundle.
    /// "MainScene" is where players arrive; the others are subrooms that a script moves the local player into with
    /// VoltScene.Switch, without a download and without leaving the instance.
    /// </summary>
    public static class WorldScenes
    {
        public const string MainSceneName = "MainScene";
        public const string NoMainScene = "You need a \"MainScene\". Fix this by renaming 1 of your scenes to MainScene.";

        public static string NotInBuildProfiles(string scene) => "You're calling \"" + scene + "\", but \"" + scene + "\" isn't in Build Profiles.";

        public struct SwitchCall
        {
            public string Scene;
            public string File;
            public int Line;
        }

        /// <summary>Set by the Volt editor: every VoltScene.Switch call in the world's scripts that names its scene in quotes.</summary>
        public static Func<IEnumerable<SwitchCall>> FindSwitchCalls;

        /// <summary>Asset paths of the enabled scenes in the active Build Profile's scene list, in its order.</summary>
        public static List<string> Paths()
        {
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>())
            {
                if (scene == null || !scene.enabled || string.IsNullOrEmpty(scene.path) || !File.Exists(scene.path)) continue;
                if (!paths.Contains(scene.path)) paths.Add(scene.path);
            }
            return paths;
        }

        public static string NameOf(string path) => Path.GetFileNameWithoutExtension(path ?? string.Empty);

        /// <summary>The path of "MainScene" among <paramref name="paths"/>, or null.</summary>
        public static string MainPath(IEnumerable<string> paths) => paths.FirstOrDefault(p => NameOf(p) == MainSceneName);

        /// <summary>MainScene first, then the subrooms in Build Profiles order.</summary>
        public static List<string> BuildOrder()
        {
            List<string> paths = Paths();
            string main = MainPath(paths);
            if (main != null)
            {
                paths.Remove(main);
                paths.Insert(0, main);
            }
            return paths;
        }

        /// <summary>What stops the world from building, in the words the creator sees. <paramref name="openScenePath"/> is the scene being published.</summary>
        public static List<string> Problems(string openScenePath)
        {
            var problems = new List<string>();
            List<string> paths = Paths();
            string main = MainPath(paths);
            if (main == null) problems.Add(NoMainScene);

            if (!string.IsNullOrEmpty(openScenePath) && !paths.Contains(openScenePath))
                problems.Add("\"" + NameOf(openScenePath) + "\" isn't in Build Profiles. Add it to the scene list in File > Build Profiles.");
            else if (main != null && !string.IsNullOrEmpty(openScenePath) && openScenePath != main)
                problems.Add("Open \"" + MainSceneName + "\" to build and publish: it is the scene players arrive in.");

            foreach (IGrouping<string, string> same in paths.GroupBy(NameOf, StringComparer.OrdinalIgnoreCase))
                if (same.Count() > 1) problems.Add("More than one scene in Build Profiles is called \"" + same.Key + "\". Every scene of a world needs its own name.");

            if (FindSwitchCalls != null)
            {
                var names = new HashSet<string>(paths.Select(NameOf), StringComparer.OrdinalIgnoreCase);
                var told = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SwitchCall call in FindSwitchCalls())
                    if (!names.Contains(call.Scene) && told.Add(call.Scene + "|" + call.File + "|" + call.Line))
                        problems.Add(NotInBuildProfiles(call.Scene) + " (" + call.File + ", line " + call.Line + ")");
            }
            return problems;
        }
    }
}
