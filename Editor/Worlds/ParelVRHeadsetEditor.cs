using System;
using System.Collections.Generic;
using System.Linq;
using ParelVR.WorldSDK;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Worlds
{
    /// <summary>
    /// The Headset's inspector: Sub Room is picked from the scenes in Build Profiles (the ones that upload with the
    /// world), World Id is typed, and what is wrong is said under it in the same words as the world check.
    /// </summary>
    [CustomEditor(typeof(ParelVRHeadset))]
    [CanEditMultipleObjects]
    public sealed class ParelVRHeadsetEditor : Editor
    {
        private SerializedProperty leadsTo, subRoom, worldId, title, hover, returnAfter, gizmoColor;

        private void OnEnable()
        {
            leadsTo = serializedObject.FindProperty("leadsTo");
            subRoom = serializedObject.FindProperty("subRoom");
            worldId = serializedObject.FindProperty("worldId");
            title = serializedObject.FindProperty("title");
            hover = serializedObject.FindProperty("hover");
            returnAfter = serializedObject.FindProperty("returnAfter");
            gizmoColor = serializedObject.FindProperty("gizmoColor");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(leadsTo, new GUIContent("Leads To"));
            bool toRoom = leadsTo.hasMultipleDifferentValues || leadsTo.enumValueIndex == (int)ParelVRHeadset.Destination.SubRoom;
            if (toRoom) RoomField();
            else EditorGUILayout.PropertyField(worldId, new GUIContent("World Id"));
            EditorGUILayout.PropertyField(title, new GUIContent("Title"));
            EditorGUILayout.PropertyField(hover, new GUIContent("Hover"));
            EditorGUILayout.PropertyField(returnAfter, new GUIContent("Return After (s)"));
            EditorGUILayout.PropertyField(gizmoColor, new GUIContent("Gizmo Color"));
            serializedObject.ApplyModifiedProperties();

            if (targets.Length == 1 && target is ParelVRHeadset headset)
            {
                string problem = Problem(headset, global::ParelVR.SDK.Build.WorldScenes.Paths());
                if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
                else EditorGUILayout.HelpBox(headset.LeadsTo == ParelVRHeadset.Destination.SubRoom
                    ? "Putting it on moves the player into " + headset.SubRoom + " (the same instance, nothing to download). Its lenses show that room live."
                    : "Putting it on takes the player to the world " + headset.WorldId + " (its open instance, or a new one). Its lenses show that world live.", MessageType.Info);
                EditorGUILayout.HelpBox("The blue arrow is the way the visor faces: the lenses look back the other way. Players pick it up and look through it, then put it on.", MessageType.None);
            }
        }

        private void RoomField()
        {
            List<string> names = global::ParelVR.SDK.Build.WorldScenes.Paths().Select(global::ParelVR.SDK.Build.WorldScenes.NameOf).ToList();
            string here = targets.Length == 1 && target is Component c ? c.gameObject.scene.name : null;
            var choices = names.Where(n => !string.Equals(n, here, StringComparison.OrdinalIgnoreCase)).ToList();
            if (choices.Count == 0 || subRoom.hasMultipleDifferentValues)
            {
                EditorGUILayout.PropertyField(subRoom, new GUIContent("Sub Room"));
                if (choices.Count == 0)
                    EditorGUILayout.HelpBox("Add the scene it leads to in File > Build Profiles: every scene there uploads with the world as a sub room.", MessageType.None);
                return;
            }

            string current = subRoom.stringValue ?? "";
            int index = choices.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
            var shown = new List<string>(choices);
            if (index < 0)
            {
                // a name typed earlier (or a scene taken out of Build Profiles) stays shown until something is picked
                shown.Insert(0, current.Length == 0 ? "(pick a sub room)" : current + " (not in Build Profiles)");
                index = 0;
            }

            int picked = EditorGUILayout.Popup(new GUIContent("Sub Room", "A scene in Build Profiles."), index, shown.ToArray());
            if (picked != index)
                subRoom.stringValue = shown.Count != choices.Count ? (picked == 0 ? current : choices[picked - 1]) : choices[picked];
        }

        /// <summary>What keeps a Headset from leading anywhere, in the words the creator sees; null when it is fine.</summary>
        public static string Problem(ParelVRHeadset headset, IEnumerable<string> buildScenes)
        {
            if (headset == null) return null;
            string name = "Headset '" + headset.gameObject.name + "'";
            if (headset.LeadsTo == ParelVRHeadset.Destination.World)
                return string.IsNullOrWhiteSpace(headset.WorldId) ? name + " leads to another world but has no World Id. Paste the ID from the world's page." : null;

            string room = (headset.SubRoom ?? "").Trim();
            if (room.Length == 0) return name + " doesn't lead anywhere. Pick the sub room (a scene in Build Profiles) it takes players to.";
            var names = new HashSet<string>(buildScenes.Select(global::ParelVR.SDK.Build.WorldScenes.NameOf), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(room)) return name + " leads to \"" + room + "\", but \"" + room + "\" isn't in Build Profiles.";
            if (string.Equals(room, headset.gameObject.scene.name, StringComparison.OrdinalIgnoreCase))
                return name + " leads to \"" + room + "\", the scene it is in. Pick another sub room.";
            return null;
        }
    }
}
