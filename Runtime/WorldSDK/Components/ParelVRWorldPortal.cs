using System;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// A doorway to another world: walking through it takes the player to the world with this World ID (its
    /// default public instance, or a new one). The ID is the one shown on the world's page and in the uploader.
    /// When a player comes within <see cref="ActivationRadius"/> the portal sparks up, spirals open and the world it
    /// leads to loads behind it, so it can be seen through live and walked into; walking away closes it again.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/World Portal")]
    public sealed class ParelVRWorldPortal : MonoBehaviour
    {
        [Tooltip("The ID of the world this portal leads to.")]
        [SerializeField] private string worldId = "";
        [Tooltip("Width and height of the opening, in metres.")]
        [SerializeField] private Vector2 size = new Vector2(1.2f, 2.3f);
        [Tooltip("Players closer than this (metres, measured along the floor) open the portal and load the world behind it. It closes again when they walk away.")]
        [SerializeField, Min(1f)] private float activationRadius = 6f;
        [Tooltip("Stays open all the time instead of opening when someone comes near (the world behind it stays loaded).")]
        [SerializeField] private bool alwaysOpen;
        [Tooltip("Draw the ParelVR portal doorway here (off: the world's own models show where it is).")]
        [SerializeField] private bool showDoorway = true;
        [SerializeField] private Color gizmoColor = new Color(0.98f, 0.62f, 0.25f, 0.95f);

        public string WorldId { get => worldId; set => worldId = value != null ? value.Trim() : ""; }
        public Vector2 Size => size;
        public float ActivationRadius { get => activationRadius; set => activationRadius = Mathf.Max(1f, value); }
        public bool AlwaysOpen { get => alwaysOpen; set => alwaysOpen = value; }
        public bool ShowDoorway => showDoorway;

        /// <summary>True while the local player is within the activation radius (or the portal is always open).</summary>
        public bool PlayerNear { get; private set; }

        /// <summary>The local player came near (true) or walked away (false).</summary>
        public event Action<bool> PlayerNearChanged;

        // walking away has to go a little further than coming near, so standing on the line doesn't flicker it
        private const float LeaveMargin = 0.75f;

        private Vector3 lastHead;
        private bool hasLast, used, warned;
        private GameObject doorway;
        // the game built the whole portal (live view, walking in): it notices the player walking through itself
        private bool gameHandles;

        private void OnEnable()
        {
            hasLast = false;
            used = false;
            if (doorway == null)
            {
                if (ParelVRPortalHooks.CreateWorldPortal != null)
                {
                    doorway = ParelVRPortalHooks.CreateWorldPortal(this);
                    gameHandles = doorway != null;
                }
                else if (showDoorway && ParelVRPortalHooks.CreateDoorway != null)
                    doorway = ParelVRPortalHooks.CreateDoorway(transform, size.y);
            }

            if (doorway != null) doorway.SetActive(true);
        }

        private void OnDisable()
        {
            SetNear(false);
            if (doorway != null) doorway.SetActive(false);
        }

        private void OnDestroy()
        {
            if (doorway != null) Destroy(doorway);
        }

        private void Update()
        {
            if (alwaysOpen) { SetNear(true); return; }
            Vector3? head = ParelVRPortalHooks.Head();
            if (head == null) { SetNear(false); return; }
            Vector3 d = head.Value - transform.position;
            float along = new Vector2(d.x, d.z).magnitude;
            // a player on another floor above or below doesn't count
            bool sameLevel = d.y > -1.5f && d.y < size.y + 2.5f;
            bool near = sameLevel && along <= activationRadius + (PlayerNear ? LeaveMargin : 0f);
            SetNear(near);
            if (near && !gameHandles && !warned && ParelVRPortalHooks.CreateWorldPortal == null)
            {
                warned = true;
                Debug.Log("[ParelVR] World Portal: in ParelVR the portal to " + (string.IsNullOrWhiteSpace(worldId) ? "(no World ID set)" : worldId)
                          + " would spiral open now and show that world through it (only in the game).", this);
            }
        }

        private void SetNear(bool near)
        {
            if (near == PlayerNear) return;
            PlayerNear = near;
            PlayerNearChanged?.Invoke(near);
        }

        private void LateUpdate()
        {
            if (gameHandles) return;
            Vector3? head = ParelVRPortalHooks.Head();
            if (used || string.IsNullOrWhiteSpace(worldId) || head == null) { hasLast = false; return; }
            Vector3 local = transform.InverseTransformPoint(head.Value);
            bool had = hasLast;
            Vector3 before = lastHead;
            lastHead = local;
            hasLast = true;
            if (!had || (local - before).sqrMagnitude > 1f) return;
            if (Mathf.Sign(local.z) == Mathf.Sign(before.z) || Mathf.Abs(before.z) < 1e-5f) return;
            float t = before.z / (before.z - local.z);
            Vector3 at = Vector3.Lerp(before, local, t);
            if (Mathf.Abs(at.x) > size.x * 0.5f || at.y < -0.4f || at.y > size.y + 0.3f) return;

            if (ParelVRPortalHooks.JoinWorld == null)
            {
                Debug.Log("[ParelVR] World Portal: walking through would take you to world " + worldId + " (only in the game).", this);
                return;
            }
            used = true;
            ParelVRPortalHooks.Flash?.Invoke(1f);
            ParelVRPortalHooks.JoinWorld(worldId.Trim());
        }

        private void OnValidate()
        {
            worldId = worldId != null ? worldId.Trim() : "";
            activationRadius = Mathf.Max(1f, activationRadius);
        }

        private void OnDrawGizmos()
        {
            ParelVRPortalGizmo.Draw(transform, size, gizmoColor, string.IsNullOrWhiteSpace(worldId) ? "to (no World ID)" : "to " + worldId);
            if (alwaysOpen) return;
            // the activation radius along the floor
            Color was = Gizmos.color;
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.35f);
            Vector3 centre = transform.position + Vector3.up * 0.02f;
            Vector3 last = centre + new Vector3(activationRadius, 0f, 0f);
            const int Sides = 64;
            for (int i = 1; i <= Sides; i++)
            {
                float a = i * Mathf.PI * 2f / Sides;
                Vector3 next = centre + new Vector3(Mathf.Cos(a) * activationRadius, 0f, Mathf.Sin(a) * activationRadius);
                Gizmos.DrawLine(last, next);
                last = next;
            }
            Gizmos.color = was;
        }
    }
}
