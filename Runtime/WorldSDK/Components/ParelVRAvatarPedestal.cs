using System;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// What the game gives Avatar Pedestals: the pedestal's look in the world (the portal ring, the avatar rising out
    /// of it, the Wear prompt). Filled in by the game at start-up; returns the object it built under the pedestal.
    /// </summary>
    public static class ParelVRAvatarPedestalHooks
    {
        /// <summary>Builds the game's pedestal for this component (a child of it), or returns null.</summary>
        public static Func<ParelVRAvatarPedestal, GameObject> CreateView;
    }

    /// <summary>
    /// Shows one avatar in your world, the full avatar as it looks on the Avatars page in the menu, and lets players
    /// wear it. When a player comes within <see cref="ActivationRadius"/> a small portal ring sparks up on the
    /// pedestal, spirals open and the avatar rises up out of it; walking away sends it back down and the ring closes.
    /// Put this on the top of your pedestal model: the avatar stands on this object's position, facing its blue line.
    /// Any avatar can be shown by its Avatar ID (shown on the avatar's page and in the uploader). Who may wear it
    /// follows the avatar's own visibility; an avatar whose creator has blacklisted you is not shown in your worlds.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Avatar Pedestal")]
    public sealed class ParelVRAvatarPedestal : MonoBehaviour
    {
        [Tooltip("The ID of the avatar to show (avtr_…), from the avatar's page on the website or the uploader.")]
        [SerializeField] private string avatarId = "";
        [Tooltip("Players closer than this (metres, measured along the floor) open the pedestal. It closes again when they walk away.")]
        [SerializeField, Min(0.5f)] private float activationRadius = 3f;
        [Tooltip("Radius of the portal ring the avatar rises out of (metres). It grows by itself for a wider avatar.")]
        [SerializeField, Range(0.25f, 2f)] private float ringRadius = 0.55f;
        [Tooltip("How tall the avatar stands on the pedestal (metres). 0 = its real size.")]
        [SerializeField, Min(0f)] private float displayHeight = 0f;
        [Tooltip("The avatar turns slowly, as on the pedestal in the menu (degrees a second). 0 = it stands still facing the blue line.")]
        [SerializeField, Range(-90f, 90f)] private float spinSpeed = 24f;
        [Tooltip("Show the avatar's name and let players wear it by pressing on it (off: it's only on show).")]
        [SerializeField] private bool allowWearing = true;
        [Tooltip("Stays open for everyone, all the time, instead of opening when someone comes near.")]
        [SerializeField] private bool alwaysOpen;
        [SerializeField] private Color gizmoColor = new Color(1f, 0.55f, 0.15f, 0.95f);

        /// <summary>The avatar shown. Changing it at run time swaps the avatar (the ring closes and opens again).</summary>
        public string AvatarId { get => avatarId; set => avatarId = value != null ? value.Trim() : ""; }
        public float ActivationRadius { get => activationRadius; set => activationRadius = Mathf.Max(0.5f, value); }
        public float RingRadius { get => ringRadius; set => ringRadius = Mathf.Clamp(value, 0.25f, 2f); }
        public float DisplayHeight { get => displayHeight; set => displayHeight = Mathf.Max(0f, value); }
        public float SpinSpeed { get => spinSpeed; set => spinSpeed = value; }
        public bool AllowWearing { get => allowWearing; set => allowWearing = value; }
        public bool AlwaysOpen { get => alwaysOpen; set => alwaysOpen = value; }

        /// <summary>True while the local player is within the activation radius (or the pedestal is always open).</summary>
        public bool PlayerNear { get; private set; }

        /// <summary>The local player came near (true) or walked away (false).</summary>
        public event Action<bool> PlayerNearChanged;

        // walking away has to go a little further than coming near, so standing on the line doesn't flicker it
        private const float LeaveMargin = 0.4f;
        private GameObject view;
        private bool warned;

        private void OnEnable()
        {
            if (view == null && ParelVRAvatarPedestalHooks.CreateView != null) view = ParelVRAvatarPedestalHooks.CreateView(this);
            if (view != null) view.SetActive(true);
        }

        private void OnDisable()
        {
            SetNear(false);
            if (view != null) view.SetActive(false);
        }

        private void OnDestroy()
        {
            if (view != null) Destroy(view);
        }

        private void Update()
        {
            if (alwaysOpen) { SetNear(true); return; }
            Vector3? head = ParelVRPortalHooks.Head();
            if (head == null) { SetNear(false); return; }
            Vector3 d = head.Value - transform.position;
            float along = new Vector2(d.x, d.z).magnitude;
            // a player on another floor above or below doesn't count
            bool sameLevel = d.y > -1.5f && d.y < 4f;
            bool near = sameLevel && along <= activationRadius + (PlayerNear ? LeaveMargin : 0f);
            SetNear(near);
            if (near && view == null && !warned && ParelVRAvatarPedestalHooks.CreateView == null)
            {
                warned = true;
                Debug.Log("[ParelVR] Avatar Pedestal: in ParelVR the avatar " + (string.IsNullOrWhiteSpace(avatarId) ? "(no Avatar ID set)" : avatarId)
                          + " would rise out of its portal ring now (only in the game).", this);
            }
        }

        private void SetNear(bool near)
        {
            if (near == PlayerNear) return;
            PlayerNear = near;
            PlayerNearChanged?.Invoke(near);
        }

        private void OnValidate()
        {
            avatarId = avatarId != null ? avatarId.Trim() : "";
            activationRadius = Mathf.Max(0.5f, activationRadius);
        }

        private void OnDrawGizmos()
        {
            Matrix4x4 was = Gizmos.matrix;
            Color wasColor = Gizmos.color;
            Vector3 p = transform.position;
            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

            // the activation radius along the floor
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.35f);
            Circle(p + Vector3.up * 0.02f, activationRadius, 64);
            // the portal ring the avatar rises out of
            Gizmos.color = gizmoColor;
            Circle(p + Vector3.up * 0.01f, ringRadius, 40);
            Circle(p + Vector3.up * 0.012f, ringRadius * 0.97f, 40);
            // roughly where the avatar stands, facing the blue line
            float h = displayHeight > 0f ? displayHeight : 1.6f;
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.55f);
            Gizmos.matrix = Matrix4x4.TRS(p, yaw, Vector3.one);
            Gizmos.DrawWireCube(new Vector3(0f, h * 0.5f, 0f), new Vector3(h * 0.32f, h, h * 0.16f));
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.9f);
            Gizmos.DrawLine(new Vector3(0f, h * 0.55f, 0f), new Vector3(0f, h * 0.55f, Mathf.Max(0.35f, ringRadius)));
            Gizmos.matrix = was;
            Gizmos.color = wasColor;
#if UNITY_EDITOR
            UnityEditor.Handles.color = gizmoColor;
            UnityEditor.Handles.Label(p + Vector3.up * (h + 0.25f), "Avatar Pedestal\n" + (string.IsNullOrWhiteSpace(avatarId) ? "(no Avatar ID)" : avatarId));
#endif
        }

        private static void Circle(Vector3 centre, float r, int sides)
        {
            Vector3 last = centre + new Vector3(r, 0f, 0f);
            for (int i = 1; i <= sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                Vector3 next = centre + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(last, next);
                last = next;
            }
        }
    }
}
