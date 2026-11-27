using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Where a <see cref="ParelVRLocalPortalIn"/> lets the player out. They come out in front of it, facing the
    /// way its blue line points (turned the way they walked in). Several In doorways may share one Out. Like the In,
    /// it can stand in a wall, lie in the floor (blue line up) or hang in the ceiling. When an In leading here is
    /// two-way, this Out is a way in too: walking (or stepping) into it takes the player back out of that In.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Local Portal Out")]
    public sealed class ParelVRLocalPortalOut : ParelVRLocalPortalEnd
    {
        [Tooltip("Width and height of the doorway drawn here, in metres. Its foot is at this object's position, its height along this object's up.")]
        [SerializeField] private Vector2 size = new Vector2(1.2f, 2.3f);
        [Tooltip("Draw the ParelVR portal doorway here: for a moment as somebody comes out, or all the time when the pair is two-way.")]
        [SerializeField] private bool showDoorway = true;
        [SerializeField] private Color gizmoColor = new Color(0.25f, 0.82f, 0.71f, 0.92f);

        public override Vector2 Size => size;

        private readonly List<ParelVRLocalPortalIn> leadingHere = new List<ParelVRLocalPortalIn>();
        private ParelVRLocalPortalIn cameFrom;
        private GameObject doorway;
        private float hideAt = -1f;

        /// <summary>
        /// The two-way Local Portal In that going into this Out takes the player back to (the one they last came out
        /// of, else the first), or null when no two-way In leads here.
        /// </summary>
        public ParelVRLocalPortalIn WayBack
        {
            get
            {
                if (TwoWayHere(cameFrom)) return cameFrom;
                foreach (ParelVRLocalPortalIn way in leadingHere)
                    if (TwoWayHere(way)) return way;
                return null;
            }
        }

        /// <summary>The Local Portal In somebody coming out here came through: the way back, else the last or first leading here.</summary>
        public ParelVRLocalPortalIn CameFrom
        {
            get
            {
                ParelVRLocalPortalIn back = WayBack;
                if (back != null) return back;
                if (cameFrom != null && cameFrom.Exit == this) return cameFrom;
                foreach (ParelVRLocalPortalIn way in leadingHere)
                    if (way != null && way.Exit == this) return way;
                return null;
            }
        }

        private bool TwoWayHere(ParelVRLocalPortalIn way) => way != null && way.TwoWay && way.isActiveAndEnabled && way.Exit == this;

        public override ParelVRLocalPortalEnd LeadsTo => WayBack;
        // Two-way pairs take the player in from either side of either doorway.
        protected override bool BothSides => true;
        protected override bool TakesObjects
        {
            get
            {
                ParelVRLocalPortalIn back = WayBack;
                return back != null && back.ObjectsPass;
            }
        }

        protected override GameObject Doorway => doorway;

        internal void Remember(ParelVRLocalPortalIn way)
        {
            if (way != null && !leadingHere.Contains(way)) leadingHere.Add(way);
        }

        internal void Forget(ParelVRLocalPortalIn way) => leadingHere.Remove(way);

        protected override void ShowArrival(ParelVRLocalPortalEnd from)
        {
            if (from is ParelVRLocalPortalIn way) cameFrom = way;
            if (!showDoorway || !MakeDoorway()) return;
            doorway.SetActive(true);
            hideAt = Time.unscaledTime + 1.6f;
        }

        private bool MakeDoorway()
        {
            if (doorway == null && ParelVRPortalHooks.CreateDoorway != null) doorway = ParelVRPortalHooks.CreateDoorway(transform, size.y);
            return doorway != null;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            hideAt = -1f;
        }

        private void Update()
        {
            // Two-way: the doorway stays, a way in like the In's (the game opens it as the player comes near).
            if (showDoorway && WayBack != null)
            {
                hideAt = -1f;
                if (MakeDoorway() && !doorway.activeSelf) doorway.SetActive(true);
                return;
            }

            if (doorway == null || !doorway.activeSelf || hideAt >= 0f && Time.unscaledTime < hideAt) return;
            hideAt = -1f;
            doorway.SetActive(false);
        }

        private void OnDisable()
        {
            if (doorway != null) doorway.SetActive(false);
        }

        private void OnDestroy()
        {
            if (doorway != null) Destroy(doorway);
        }

        private void OnDrawGizmos()
        {
            ParelVRPortalGizmo.Draw(transform, size, gizmoColor, Application.isPlaying && WayBack != null ? "Out (two-way)" : "Out");
        }
    }
}
