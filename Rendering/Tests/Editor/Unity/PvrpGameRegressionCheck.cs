using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// Regression check for the game: every material used by the game's prefabs (menus, nameplates, UI, avatars...) and
    /// by the scenes in Build Settings and the Addressables groups goes through the decision PVRP makes when content loads, for PC and Quest.
    /// Anything PVRP would change is listed with the reason, so it can be checked that only materials that are already
    /// broken (magenta / invisible today) are touched. Read-only. Menu: ParelVR → Rendering → Tests → Game Regression Check.
    /// Result: Logs/PVRP/GameRegression.txt.
    /// </summary>
    public static class PvrpGameRegressionCheck
    {
        public const string ResultPath = "Logs/PVRP/GameRegression.txt";
        private const int UnloadEvery = 25;

        /// <summary>Everything the game loads through Addressables (default avatars, framework prefabs...), read from the group files.</summary>
        private static IEnumerable<string> AddressableEntries()
        {
            const string groups = "Assets/AddressableAssetsData/AssetGroups";
            if (!Directory.Exists(groups)) yield break;
            foreach (string file in Directory.GetFiles(groups, "*.asset"))
                foreach (string line in File.ReadLines(file))
                {
                    int at = line.IndexOf("m_GUID: ", StringComparison.Ordinal);
                    if (at < 0) continue;
                    string path = AssetDatabase.GUIDToAssetPath(line.Substring(at + 8).Trim());
                    if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path)) yield return path;
                }
        }

        [MenuItem("ParelVR/Rendering/Tests/Game Regression Check")]
        public static void Run()
        {
            ParelRenderManager.Initialize();
            var materialPaths = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal); // material → users
            void Use(string material, string user)
            {
                if (!materialPaths.TryGetValue(material, out HashSet<string> users)) materialPaths[material] = users = new HashSet<string>();
                if (users.Count < 3) users.Add(user);
            }

            string[] prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ParelVR" }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToArray();
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path)
                .Concat(AddressableEntries()).Distinct().ToArray();
            int i = 0;
            try
            {
                foreach (string owner in prefabs.Concat(scenes))
                {
                    if (i++ % 20 == 0 && EditorUtility.DisplayCancelableProgressBar("PVRP game regression check", owner, (float)i / (prefabs.Length + scenes.Length))) break;
                    foreach (string dep in AssetDatabase.GetDependencies(owner, true))
                        if (dep.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) Use(dep, Path.GetFileNameWithoutExtension(owner));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"PVRP game regression check — {prefabs.Length} prefabs under Assets/ParelVR, {scenes.Length} Build Settings scenes + Addressables entries, {materialPaths.Count} materials, {DateTime.Now:yyyy-MM-dd HH:mm}");
                foreach (TargetPlatform target in new[] { TargetPlatform.Pc, TargetPlatform.Quest })
                {
                    RepairPolicy policy = RuntimeRepair.DefaultPolicy(target);
                    int keep = 0, alreadyBroken = 0, foreign = 0, hide = 0, other = 0, n = 0;
                    var changed = new StringBuilder();
                    foreach (KeyValuePair<string, HashSet<string>> pair in materialPaths)
                    {
                        var m = AssetDatabase.LoadAssetAtPath<Material>(pair.Key);
                        if (m == null) continue;
                        bool broken = ParelRenderValidator.IsBroken(m) || (m.shader != null && ShaderUtil.ShaderHasError(m.shader));
                        ShaderClassification c = ParelRenderManager.Classify(m, target);
                        RepairAction action = RuntimeRepair.Decide(c, broken, policy);
                        if (action == RepairAction.Keep) keep++;
                        else
                        {
                            bool isForeign = c.Pipeline == RenderPipelineKind.Universal || c.Pipeline == RenderPipelineKind.HighDefinition;
                            string why = broken ? "already broken (" + (m.shader == null ? "no shader" : m.shader.name) + ")"
                                : isForeign ? "URP/HDRP shader (" + m.shader.name + "): renders magenta in the Built-in pipeline today"
                                : "WORKING MATERIAL WOULD CHANGE (" + m.shader.name + ")";
                            if (action == RepairAction.Hide) hide++;
                            else if (broken) alreadyBroken++;
                            else if (isForeign) foreign++;
                            else other++;
                            changed.AppendLine($"    {action,-7} {pair.Key}  — {why}; used by {string.Join(", ", pair.Value)}");
                        }
                        if (++n % UnloadEvery == 0) EditorUtility.UnloadUnusedAssetsImmediate();
                    }
                    sb.AppendLine();
                    sb.AppendLine($"[{target}] kept as they are: {keep} | converted because already broken: {alreadyBroken} | converted URP/HDRP: {foreign} | hidden (nothing fits): {hide} | working materials that would change: {other}");
                    sb.Append(changed);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
                File.WriteAllText(ResultPath, sb.ToString());
                Debug.Log(RenderingLog.Prefix + sb.ToString().Split('\n')[0] + " → " + Path.GetFullPath(ResultPath));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.UnloadUnusedAssetsImmediate();
            }
        }
    }
}
