using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Where a <see cref="ParelVRLocalPortalIn"/> lets the player out. They come out in front of it, facing the
    /// way its blue line points (turned the way they walked in). Several In doorways may share one Out.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Local Portal Out")]
    public sealed class ParelVRLocalPortalOut : MonoBehaviour
    {
        [Tooltip("Width and height of the doorway drawn here, in metres.")]
        [SerializeField] private Vector2 size = new Vector2(1.2f, 2.3f);
        [Tooltip("Draw the ParelVR portal doorway here for a moment as somebody comes out.")]
        [SerializeField] private bool showDoorway = true;
        [SerializeField] private Color gizmoColor = new Color(0.25f, 0.82f, 0.71f, 0.92f);

        public Vector2 Size => size;

        private GameObject doorway;
        private float hideAt = -1f;

        /// <summary>Somebody is coming out here now: the doorway shows for a moment behind them.</summary>
        internal void Arrived()
        {
            if (!showDoorway) return;
            if (doorway == null && ParelVRPortalHooks.CreateDoorway != null) doorway = ParelVRPortalHooks.CreateDoorway(transform, size.y);
            if (doorway == null) return;
            doorway.SetActive(true);
            hideAt = Time.unscaledTime + 1.6f;
        }

        private void Update()
        {
            if (hideAt < 0f || Time.unscaledTime < hideAt) return;
            hideAt = -1f;
            if (doorway != null) doorway.SetActive(false);
        }

        private void OnDestroy()
        {
            if (doorway != null) Destroy(doorway);
        }

        private void OnDrawGizmos()
        {
            ParelVRPortalGizmo.Draw(transform, size, gizmoColor, "Out");
        }
    }
}
