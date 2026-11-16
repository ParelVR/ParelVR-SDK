using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Validation;
using ParelVR.WorldSDK;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Worlds
{
    /// <summary>
    /// A world has one World Menu, across MainScene and every subroom: adding a second is refused (with the way to the
    /// first), and the world check reports any extra one (copied objects, merged scenes) with an Auto Fix that keeps the
    /// first. Also: ParelVR SDK > World > Create World Menu, and the menu's own checks (ids, page links, choices).
    /// </summary>
    [InitializeOnLoad]
    public static class ParelWorldMenuEditor
    {
        static ParelWorldMenuEditor()
        {
            ObjectFactory.componentWasAdded -= OnAdded;
            ObjectFactory.componentWasAdded += OnAdded;
        }

        private static void OnAdded(Component component)
        {
            if (!(component is ParelWorldMenu added)) return;
            ParelWorldMenu other = Loaded().FirstOrDefault(m => m != added);
            string closed = other == null ? ClosedScenesWithMenu(added.gameObject.scene.path).FirstOrDefault() : null;
            if (other == null && closed == null) return;
            // Removed once Unity has finished adding it (it can't be removed from inside the add).
            EditorApplication.delayCall += () =>
            {
                if (added != null) Undo.DestroyObjectImmediate(added);
                EditorUtility.DisplayDialog("World Menu",
                    "A world has one World Menu. This world already has one" +
                    (other != null ? " on '" + other.gameObject.name + "' (selected now)." : " in " + WorldScenesName(closed) + ".") +
                    "\n\nAdd pages to that menu instead.", "OK");
                if (other != null) Selection.activeObject = other.gameObject;
            };
        }

        [MenuItem("ParelVR SDK/World/Create World Menu", priority = 60)]
        private static void Create()
        {
            ParelWorldMenu existing = Loaded().FirstOrDefault();
            if (existing != null)
            {
                Selection.activeObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                EditorUtility.DisplayDialog("World Menu", "This world already has its World Menu (selected). A world has one; add pages to it.", "OK");
                return;
            }
            string closed = ClosedScenesWithMenu(SceneManager.GetActiveScene().path).FirstOrDefault();
            if (closed != null)
            {
                EditorUtility.DisplayDialog("World Menu", "This world already has its World Menu, in " + WorldScenesName(closed) + ". A world has one.", "OK");
                return;
            }
            var go = new GameObject("World Menu");
            var menu = go.AddComponent<ParelWorldMenu>();
            menu.menuName = string.IsNullOrEmpty(SceneManager.GetActiveScene().name) ? "World" : SceneManager.GetActiveScene().name;
            menu.pages = new List<ParelWorldMenuPage>
            {
                new ParelWorldMenuPage
                {
                    id = "home", title = "Home",
                    controls = new List<ParelWorldMenuControl>
                    {
                        new ParelWorldMenuControl { id = "intro", type = ParelWorldMenuControlType.Label, label = "Welcome! These settings are only for you." },
                        new ParelWorldMenuControl { id = "music", type = ParelWorldMenuControlType.Slider, label = "Music", description = "Volume of the world's music", min = 0f, max = 1f, startValue = 0.6f },
                        new ParelWorldMenuControl { id = "mirror", type = ParelWorldMenuControlType.Toggle, label = "Mirror", description = "Show the mirror" },
                        new ParelWorldMenuControl { id = "time", type = ParelWorldMenuControlType.Options, label = "Time of day", options = new[] { "Morning", "Day", "Night" }, startOption = 1 },
                        new ParelWorldMenuControl { id = "more", type = ParelWorldMenuControlType.PageLink, label = "More settings", page = "more" },
                    }
                },
                new ParelWorldMenuPage
                {
                    id = "more", title = "More",
                    controls = new List<ParelWorldMenuControl>
                    {
                        new ParelWorldMenuControl { id = "respawn", type = ParelWorldMenuControlType.Button, label = "Respawn", description = "Back to the start" },
                    }
                }
            };
            Undo.RegisterCreatedObjectUndo(go, "Create World Menu");
            Selection.activeObject = go;
        }

        // ── finding the world's menus ────────────────────────────────────────

        private static IEnumerable<ParelWorldMenu> Loaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (ParelWorldMenu m in root.GetComponentsInChildren<ParelWorldMenu>(true))
                        yield return m;
            }
        }

        /// <summary>The world's scenes that are not open but have a World Menu, read from the saved scene files.</summary>
        private static IEnumerable<string> ClosedScenesWithMenu(string mainScene)
        {
            string guid = ScriptGuid();
            if (guid == null) yield break;
            var open = new HashSet<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++) open.Add(SceneManager.GetSceneAt(i).path);
            List<string> scenes;
            try { scenes = Build.WorldScenes.BuildOrder(); }
            catch (Exception) { yield break; }
            foreach (string path in scenes)
            {
                if (string.IsNullOrEmpty(path) || open.Contains(path) || !File.Exists(path)) continue;
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception) { continue; }
                if (text.IndexOf("guid: " + guid, StringComparison.Ordinal) >= 0) yield return path;
            }
        }

        private static int CountIn(string path, string guid)
        {
            try
            {
                string text = File.ReadAllText(path);
                int n = 0, at = 0;
                while ((at = text.IndexOf("m_Script: {fileID: 11500000, guid: " + guid, at, StringComparison.Ordinal)) >= 0) { n++; at++; }
                return n;
            }
            catch (Exception) { return 0; }
        }

        private static string ScriptGuid()
        {
            foreach (string g in AssetDatabase.FindAssets("ParelWorldMenu t:MonoScript"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(g));
                if (script != null && script.GetClass() == typeof(ParelWorldMenu)) return g;
            }
            return null;
        }

        private static string WorldScenesName(string path) => "the scene " + Build.WorldScenes.NameOf(path);

        // ── the world check ──────────────────────────────────────────────────

        public sealed class Validator : IValidator
        {
            public string Name => "World Menu";
            public Core.Settings.ParelProjectType RequiredMode => Core.Settings.ParelProjectType.World;

            public Task<ValidationReport> ValidateAsync()
            {
                var report = new ValidationReport();
                List<ParelWorldMenu> menus = Loaded().ToList();
                string guid = ScriptGuid();
                var closed = new List<string>();
                int closedCount = 0;
                if (guid != null)
                    foreach (string path in ClosedScenesWithMenu(SceneManager.GetActiveScene().path))
                    {
                        closed.Add(path);
                        closedCount += Math.Max(1, CountIn(path, guid));
                    }
                int total = menus.Count + closedCount;
                if (total > 1)
                {
                    string where = string.Join(", ", menus.Select(m => "'" + m.gameObject.name + "'").Concat(closed.Select(Build.WorldScenes.NameOf)));
                    if (menus.Count > 1)
                        report.AddError("Your world has " + total + " World Menus (" + where + "). A world has exactly one: the extra ones are removed by Auto Fix (the first one stays).", () =>
                        {
                            foreach (ParelWorldMenu extra in Loaded().Skip(1).ToList()) Undo.DestroyObjectImmediate(extra);
                        }, menus[1]);
                    else
                        report.AddError("Your world has " + total + " World Menus (" + where + "). A world has exactly one: remove all but one (open the other scene to do it).");
                }
                foreach (ParelWorldMenu menu in menus.Take(1)) Check(menu, report);
                return Task.FromResult(report);
            }
        }

        private static void Check(ParelWorldMenu menu, ValidationReport report)
        {
            if (string.IsNullOrWhiteSpace(menu.menuName)) report.AddWarning("The World Menu has no name; players see \"World\".", null, menu);
            if (menu.pages == null || menu.pages.Count(p => p != null) == 0)
            {
                report.AddError("The World Menu has no pages. Add at least one with its controls.", null, menu);
                return;
            }
            if (menu.pages.Count > ParelWorldMenu.MaxPages) report.AddWarning("The World Menu has " + menu.pages.Count + " pages; only the first " + ParelWorldMenu.MaxPages + " are listed.", null, menu);
            if (menu.icon != null && (menu.icon.width > 512 || menu.icon.height > 512))
                report.AddInfo("The World Menu's icon is " + menu.icon.width + "x" + menu.icon.height + "; it shows at 44 pixels, so 128x128 is plenty.", null, menu.icon);

            var pageIds = new HashSet<string>();
            var controlIds = new HashSet<string>();
            foreach (ParelWorldMenuPage page in menu.pages)
            {
                if (page == null) continue;
                if (string.IsNullOrWhiteSpace(page.id)) report.AddError("A page of the World Menu (\"" + page.title + "\") has no id.", null, menu);
                else if (!pageIds.Add(page.id)) report.AddError("Two pages of the World Menu have the id \"" + page.id + "\"; each needs its own.", null, menu);
                if (page.controls == null) continue;
                if (page.controls.Count > ParelWorldMenu.MaxControlsPerPage)
                    report.AddWarning("The page \"" + page.title + "\" has " + page.controls.Count + " controls; only the first " + ParelWorldMenu.MaxControlsPerPage + " show.", null, menu);
                foreach (ParelWorldMenuControl c in page.controls)
                {
                    if (c == null) continue;
                    string name = "\"" + (string.IsNullOrEmpty(c.label) ? c.id : c.label) + "\" on the page \"" + page.title + "\"";
                    if (string.IsNullOrWhiteSpace(c.id)) report.AddError("The control " + name + " has no id; scripts name controls by it.", null, menu);
                    else if (!controlIds.Add(c.id)) report.AddError("Two controls of the World Menu have the id \"" + c.id + "\"; each needs its own.", null, menu);
                    switch (c.type)
                    {
                        case ParelWorldMenuControlType.PageLink:
                            if (menu.FindPage(c.page) == null) report.AddError("The page link " + name + " opens \"" + c.page + "\", and no page has that id.", null, menu);
                            break;
                        case ParelWorldMenuControlType.Options:
                            if (c.options == null || c.options.Length == 0) report.AddError("The option switcher " + name + " has no choices.", null, menu);
                            else if (c.objectPerOption != null && c.objectPerOption.Length > 0 && c.objectPerOption.Length != c.options.Length)
                                report.AddWarning("The option switcher " + name + " has " + c.options.Length + " choices and " + c.objectPerOption.Length + " objects; give each choice its object.", null, menu);
                            break;
                        case ParelWorldMenuControlType.Slider:
                            if (Mathf.Approximately(c.min, c.max)) report.AddWarning("The slider " + name + " has the same minimum and maximum, so it can't move.", null, menu);
                            break;
                    }
                }
            }
        }
    }
}
