using ParelVR.SDK.Port;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Tools
{
    /// <summary>
    /// Turns the PhysBones (and PhysBone colliders) of an avatar made for another platform into Motion Bones. Motion
    /// Bones use the same names and value ranges, so a chain moves the same after converting. Done by Auto Port, so it
    /// works with or without that platform's SDK in the project (without it the settings are read from the saved
    /// scene / prefab), and the originals are removed rather than switched off.
    /// </summary>
    public static class ParelPhysBoneConverter
    {
        [MenuItem("ParelVR SDK/Avatar Tools/Convert PhysBones to Motion Bones", priority = 120)]
        private static void ConvertSelected()
        {
            GameObject root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog("Convert PhysBones", "Select the avatar (its root object) in the Hierarchy first.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Convert PhysBones",
                    $"Replace every PhysBone and PhysBone collider under '{root.name}' with Motion Bones (same settings) and remove the originals?",
                    "Convert", "Cancel")) return;
            Convert(root, true, out int bones, out int colliders, out int found);
            EditorUtility.DisplayDialog("Convert PhysBones",
                found == 0 ? "No PhysBones were found." : $"Converted {bones} bone chain(s) and {colliders} collider(s).", "OK");
        }

        [MenuItem("ParelVR SDK/Avatar Tools/Convert PhysBones to Motion Bones", true)]
        private static bool ConvertSelectedValid() => Selection.activeGameObject != null;

        /// <summary>
        /// Ports the avatar (PhysBones and everything else from another platform on it). The originals are always
        /// removed; <paramref name="removeOriginals"/> is kept for callers written against the older version.
        /// </summary>
        public static void Convert(GameObject root, bool removeOriginals, out int boneCount, out int colliderCount, out int found)
        {
            ParelPortReport report = ParelContentPorter.Port(root);
            report.Ported.TryGetValue("PhysBone", out boneCount);
            report.Ported.TryGetValue("PhysBone collider", out colliderCount);
            found = boneCount + colliderCount;
            if (report.Notes.Count > 0 || report.PortedCount > found || report.RemovedCount > 0) Debug.Log("[ParelVR Auto Port] " + report);
        }
    }
}
