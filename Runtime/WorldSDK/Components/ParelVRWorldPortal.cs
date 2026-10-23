using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// A doorway to another world: walking through it takes the player to the world with this World ID (its
    /// default public instance, or a new one). The ID is the one shown on the world's page and in the uploader.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/World Portal")]
    public sealed class ParelVRWorldPortal : MonoBehaviour
    {
        [Tooltip("The ID of the world this portal leads to.")]
        [SerializeField] private string worldId = "";
        [Tooltip("Width and height of the opening, in metres.")]
        [SerializeField] private Vector2 size = new Vector2(1.2f, 2.3f);
        [Tooltip("Draw the ParelVR portal doorway here (off: the world's own models show where it is).")]
        [SerializeField] private bool showDoorway = true;
        [SerializeField] private Color gizmoColor = new Color(0.98f, 0.62f, 0.25f, 0.95f);

        public string WorldId { get => worldId; set => worldId = value != null ? value.Trim() : ""; }
        public Vector2 Size => size;

        private Vector3 lastHead;
        private bool hasLast, used;
        private GameObject doorway;

        private void OnEnable()
        {
            hasLast = false;
            used = false;
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

        private void OnDrawGizmos()
        {
            ParelVRPortalGizmo.Draw(transform, size, gizmoColor, string.IsNullOrWhiteSpace(worldId) ? "to (no World ID)" : "to " + worldId);
        }
    }
}
