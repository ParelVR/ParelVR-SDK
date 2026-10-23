using System;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// What the game gives the world portals: where the local player's head is, where they stand, how to move
    /// them, how to take them to another world, and the portal look it draws. Filled in by the game at start-up.
    /// </summary>
    public static class ParelVRPortalHooks
    {
        /// <summary>The local player's head (eyes) in world space.</summary>
        public static Func<Vector3> LocalHead;
        /// <summary>The local player's standing position and facing.</summary>
        public static Func<Pose> LocalPlayer;
        /// <summary>Moves the local player (feet position, facing).</summary>
        public static Action<Vector3, Quaternion> TeleportLocalPlayer;
        /// <summary>Takes the local player to a world (by its world ID).</summary>
        public static Action<string> JoinWorld;
        /// <summary>Puts the game's portal doorway on a transform (height in metres); returns it, or null.</summary>
        public static Func<Transform, float, GameObject> CreateDoorway;
        /// <summary>A moment of portal light over the view while the player passes through (strength 0..1).</summary>
        public static Action<float> Flash;

        internal static Vector3? Head()
        {
            if (LocalHead != null) return LocalHead();
            Camera main = Camera.main;
            return main != null ? main.transform.position : (Vector3?)null;
        }
    }

    /// <summary>
    /// The doorway a player walks into. Walking through it (from the side its blue line points to) takes them to
    /// its <see cref="Exit"/>, keeping where they stand in the doorway and the way they walk, as if the two were
    /// one opening. Size is the doorway's opening, centred on this object, its foot at this object's position.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Local Portal In")]
    public sealed class ParelVRLocalPortalIn : MonoBehaviour
    {
        [Tooltip("Where walking through this doorway takes the player.")]
        [SerializeField] private ParelVRLocalPortalOut exit;
        [Tooltip("Width and height of the opening, in metres.")]
        [SerializeField] private Vector2 size = new Vector2(1.2f, 2.3f);
        [Tooltip("Draw the ParelVR portal doorway here (off: the world's own models show where it is).")]
        [SerializeField] private bool showDoorway = true;
        [Tooltip("Either side can be walked into (off: only from the side the blue line points to).")]
        [SerializeField] private bool twoWay;
        [SerializeField] private Color gizmoColor = new Color(0.62f, 0.38f, 1f, 0.95f);

        public ParelVRLocalPortalOut Exit { get => exit; set => exit = value; }
        public Vector2 Size => size;

        private Vector3 lastHead;
        private bool hasLast;
        private float guardUntil;
        private GameObject doorway;

        private void OnEnable()
        {
            hasLast = false;
            if (showDoorway && doorway == null && ParelVRPortalHooks.CreateDoorway != null)
                doorway = ParelVRPortalHooks.CreateDoorway(transform, size.y);
            if (doorway != null) doorway.SetActive(true);
        }

        private void OnDisable()
        {
            if (doorway != null) doorway.SetActive(false);
        }

        private void OnDestroy()
        {
            if (doorway != null) Destroy(doorway);
        }

        private void LateUpdate()
        {
            Vector3? head = ParelVRPortalHooks.Head();
            if (exit == null || head == null) { hasLast = false; return; }
            Vector3 local = transform.InverseTransformPoint(head.Value);
            bool had = hasLast;
            Vector3 before = lastHead;
            lastHead = local;
            hasLast = true;
            if (!had || Time.unscaledTime < guardUntil || (local - before).sqrMagnitude > 1f) return;
            if (Mathf.Sign(local.z) == Mathf.Sign(before.z) || Mathf.Abs(before.z) < 1e-5f) return;
            if (!twoWay && before.z < 0f) return;

            float t = before.z / (before.z - local.z);
            Vector3 at = Vector3.Lerp(before, local, t);
            if (Mathf.Abs(at.x) > size.x * 0.5f || at.y < -0.4f || at.y > size.y + 0.3f) return;
            Through(before.z > 0f);
        }

        private void Through(bool fromFront)
        {
            if (ParelVRPortalHooks.TeleportLocalPlayer == null || ParelVRPortalHooks.LocalPlayer == null)
            {
                Debug.LogWarning("[ParelVR] Local Portal In: the game has not connected portals (are you in the game?).", this);
                return;
            }

            // In and out are one doorway: what is in front of this one comes out in front of the exit.
            Quaternion flip = Quaternion.Euler(0f, fromFront ? 180f : 0f, 0f);
            Quaternion turn = exit.transform.rotation * flip * Quaternion.Inverse(transform.rotation);
            Pose player = ParelVRPortalHooks.LocalPlayer();
            Vector3 offset = transform.InverseTransformPoint(player.position);
            offset.z = 0f;   // out on the exit's plane, a step beyond it
            Vector3 position = exit.transform.TransformPoint(flip * offset) + exit.transform.forward * 0.35f;
            Vector3 forward = turn * player.rotation * Vector3.forward;
            forward.y = 0f;
            Quaternion facing = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : exit.transform.rotation;

            guardUntil = Time.unscaledTime + 0.75f;
            exit.Arrived();
            ParelVRPortalHooks.Flash?.Invoke(1f);
            ParelVRPortalHooks.TeleportLocalPlayer(position, facing);
            hasLast = false;
        }

        private void OnDrawGizmos()
        {
            ParelVRPortalGizmo.Draw(transform, size, gizmoColor, "In");
            if (exit == null) return;
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.35f);
            Gizmos.DrawLine(transform.position + Vector3.up * size.y * 0.5f, exit.transform.position + Vector3.up * size.y * 0.5f);
        }
    }

    internal static class ParelVRPortalGizmo
    {
        /// <summary>The opening's outline, a ring at its foot and the facing line, like the spawn point's.</summary>
        internal static void Draw(Transform t, Vector2 size, Color color, string kind)
        {
            Gizmos.color = color;
            Matrix4x4 was = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            float hw = size.x * 0.5f;
            Vector3 a = new Vector3(-hw, 0f, 0f), b = new Vector3(hw, 0f, 0f), c = new Vector3(hw, size.y, 0f), d = new Vector3(-hw, size.y, 0f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
            Gizmos.color = new Color(color.r, color.g, color.b, color.a * 0.18f);
            Gizmos.DrawCube(new Vector3(0f, size.y * 0.5f, 0f), new Vector3(size.x, size.y, 0.01f));
            Gizmos.matrix = was;
            Gizmos.color = color;
            Gizmos.DrawWireSphere(t.position, 0.4f);
            Gizmos.DrawLine(t.position, t.position + t.forward * 1.2f);
#if UNITY_EDITOR
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.Label(t.position + Vector3.up * (size.y + 0.15f), "Portal " + kind);
#endif
        }
    }
}
