using System;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>What the game does for a Headset (the SDK only places it and says where it leads).</summary>
    public static class ParelVRHeadsetHooks
    {
        /// <summary>
        /// Builds the game's headset for this component: the model, the live view through its lenses, picking it up
        /// and putting it on. Returns it, or null when it can't be built yet (it is asked again a second later).
        /// </summary>
        public static Func<ParelVRHeadset, GameObject> Create;
    }

    /// <summary>
    /// A ParelVR VR headset that sits in the world and leads somewhere: to one of this world's sub rooms (another scene
    /// in Build Profiles, the same as VoltScene.Switch) or to another world (by its World ID). Pick it up and its lenses
    /// show where it leads, live; put it on (bring it to your face in VR, click then right click on desktop) and you are
    /// there. Everyone in the room sees it moved and worn, and it floats back to its place a while after it is left.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Headset")]
    public sealed class ParelVRHeadset : MonoBehaviour
    {
        public enum Destination
        {
            [Tooltip("A scene of this world (in Build Profiles): players stay in the same instance.")]
            SubRoom = 0,
            [Tooltip("Another world, by its World ID.")]
            World = 1
        }

        [Tooltip("Where putting it on takes the player.")]
        [SerializeField] private Destination leadsTo = Destination.SubRoom;
        [Tooltip("The sub room it leads to: the name of a scene in Build Profiles (\"MainScene\" goes back to the start).")]
        [SerializeField] private string subRoom = "";
        [Tooltip("The ID of the world it leads to (shown on the world's page and in the uploader).")]
        [SerializeField] private string worldId = "";
        [Tooltip("The name on its card. Empty: the sub room's name, or the world's own name.")]
        [SerializeField] private string title = "";
        [Tooltip("Hovers in the air where it is placed. Off: it rests on whatever is under it (a table, a shelf).")]
        [SerializeField] private bool hover = true;
        [Tooltip("Seconds after it is put down somewhere else (or dropped by someone who put it on) before it goes back to its place. 0 = it stays where it is left.")]
        [SerializeField, Min(0f)] private float returnAfter = 30f;
        [SerializeField] private Color gizmoColor = new Color(0.62f, 0.38f, 1f, 0.95f);

        public Destination LeadsTo { get => leadsTo; set => leadsTo = value; }
        public string SubRoom { get => subRoom; set => subRoom = value != null ? value.Trim() : ""; }
        public string WorldId { get => worldId; set => worldId = value != null ? value.Trim() : ""; }
        public string Title { get => title; set => title = value ?? ""; }
        public bool Hover { get => hover; set => hover = value; }
        public float ReturnAfter { get => returnAfter; set => returnAfter = Mathf.Max(0f, value); }

        /// <summary>The sub room or World ID it leads to, as typed (empty: nowhere yet).</summary>
        public string Target => leadsTo == Destination.SubRoom ? subRoom : worldId;

        private GameObject made;
        private float nextTry;
        private bool told;

        private void OnEnable()
        {
            Make();
            if (made != null) made.SetActive(true);
        }

        private void OnDisable()
        {
            if (made != null) made.SetActive(false);
        }

        private void OnDestroy()
        {
            if (made != null) Destroy(made);
        }

        private void Update()
        {
            if (made != null || Time.unscaledTime < nextTry) return;
            Make();
        }

        private void Make()
        {
            if (made != null) return;
            nextTry = Time.unscaledTime + 1f;
            if (ParelVRHeadsetHooks.Create == null)
            {
                if (!told && Application.isPlaying)
                {
                    told = true;
                    Debug.Log("[ParelVR] Headset: in ParelVR a headset stands here that leads to " +
                              (string.IsNullOrWhiteSpace(Target) ? "(nowhere yet: set " + (leadsTo == Destination.SubRoom ? "Sub Room" : "World Id") + ")" :
                                  (leadsTo == Destination.SubRoom ? "the sub room " : "the world ") + Target.Trim()) + " (only in the game).", this);
                }
                return;
            }

            made = ParelVRHeadsetHooks.Create(this);
        }

        private void OnValidate()
        {
            subRoom = subRoom != null ? subRoom.Trim() : "";
            worldId = worldId != null ? worldId.Trim() : "";
            returnAfter = Mathf.Max(0f, returnAfter);
        }

        // The headset's outline at its real size: origin between the eyes, the visor's front along the blue arrow.
        private void OnDrawGizmos()
        {
            Color was = Gizmos.color;
            Matrix4x4 matrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.color = gizmoColor;
            Gizmos.DrawWireCube(new Vector3(0f, -0.005f, 0.045f), new Vector3(0.19f, 0.11f, 0.09f));
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, gizmoColor.a * 0.25f);
            Gizmos.DrawCube(new Vector3(0f, -0.005f, 0.045f), new Vector3(0.19f, 0.11f, 0.09f));
            // the strap round the back of the head
            Gizmos.color = gizmoColor;
            const int Sides = 24;
            Vector3 last = new Vector3(0.095f, 0f, 0.02f);
            for (int i = 1; i <= Sides; i++)
            {
                float a = Mathf.PI * i / Sides;
                Vector3 next = new Vector3(Mathf.Cos(a) * 0.095f, 0f, 0.02f - Mathf.Sin(a) * 0.17f);
                Gizmos.DrawLine(last, next);
                last = next;
            }
            Gizmos.DrawLine(new Vector3(0f, 0f, 0.09f), new Vector3(0f, 0f, 0.3f));
            Gizmos.matrix = matrix;
            Gizmos.color = was;
#if UNITY_EDITOR
            UnityEditor.Handles.color = gizmoColor;
            string to = string.IsNullOrWhiteSpace(Target) ? "(nowhere yet)" : leadsTo == Destination.SubRoom ? "sub room " + Target.Trim() : "world " + Target.Trim();
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.16f, "Headset to " + to);
#endif
        }
    }
}
