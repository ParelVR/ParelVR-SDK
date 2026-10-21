using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>Shape, tags and switches shared by Contact Senders and Receivers.</summary>
    public abstract class ParelContactBase : MonoBehaviour
    {
        public enum ShapeType
        {
            Sphere = 0,
            Capsule = 1,
            Box = 2,
        }

        /// <summary>Tags past this many are ignored.</summary>
        public const int MaxTags = 16;
        /// <summary>Largest radius after scaling, in metres.</summary>
        public const float MaxRadius = 3f;
        /// <summary>Largest capsule height or box side after scaling, in metres.</summary>
        public const float MaxExtent = 6f;

        [Tooltip("Transform the shape follows (empty = this object).")]
        public Transform rootTransform;
        public ShapeType shapeType = ShapeType.Sphere;
        public float radius = 0.05f;
        [Tooltip("Capsule: total height including the rounded ends, along local Y.")]
        public float height = 0.2f;
        [Tooltip("Box: width, height and depth.")]
        public Vector3 size = new Vector3(0.1f, 0.1f, 0.1f);
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        [Tooltip("A sender and receiver touch only if they share at least one tag (up to 16, case-sensitive).")]
        public List<string> collisionTags = new List<string>();
        [Tooltip("Only exists on the wearer's own client, and does not count toward the performance rank.")]
        public bool localOnly;
        [Tooltip("Avatar parameter that switches this contact on: it exists only while the parameter is true or not 0. " +
                 "Empty = always on. Saves animating the component on and off.")]
        public string activeWhen = string.Empty;

        /// <summary>Set by the game on auto-created avatar contacts.</summary>
        [System.NonSerialized] public Transform OwnerRootOverride;

        internal Vector3 WorldA;
        internal Vector3 WorldB;
        internal float WorldRadius;
        internal Vector3 WorldCenter;
        internal Quaternion WorldRotation;
        internal Vector3 WorldHalfSize;
        internal Vector3 Velocity;
        private Vector3 _lastCenter;
        private bool _hasLastCenter;
        private Transform _ownerRoot;
        private bool _ownerResolved;

        public Transform Root => rootTransform != null ? rootTransform : transform;

        /// <summary>The avatar this belongs to (null for world contacts).</summary>
        public Transform OwnerRoot
        {
            get
            {
                if (OwnerRootOverride != null) return OwnerRootOverride;
                if (!_ownerResolved)
                {
                    _ownerResolved = true;
                    ParelAvatarDescriptor descriptor = GetComponentInParent<ParelAvatarDescriptor>();
                    _ownerRoot = descriptor != null ? descriptor.transform : null;
                }
                return _ownerRoot;
            }
        }

        /// <summary>True for contacts that are part of a world rather than an avatar.</summary>
        public bool IsWorldContact => OwnerRoot == null;

        internal void UpdateWorld(float deltaTime)
        {
            Transform root = Root;
            Vector3 scale = root.lossyScale;
            float s = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            WorldCenter = root.TransformPoint(position);
            WorldRotation = root.rotation * rotation;
            WorldRadius = Mathf.Clamp(radius * s, 0f, MaxRadius);
            WorldHalfSize = Vector3.zero;
            if (shapeType == ShapeType.Capsule)
            {
                Vector3 up = WorldRotation * Vector3.up;
                float half = Mathf.Max(0f, Mathf.Min(height * s, MaxExtent) * 0.5f - WorldRadius);
                WorldA = WorldCenter - up * half;
                WorldB = WorldCenter + up * half;
            }
            else
            {
                WorldA = WorldCenter;
                WorldB = WorldCenter;
                if (shapeType == ShapeType.Box)
                {
                    WorldRadius = 0f;
                    WorldHalfSize = new Vector3(
                        Mathf.Min(Mathf.Abs(size.x * scale.x), MaxExtent),
                        Mathf.Min(Mathf.Abs(size.y * scale.y), MaxExtent),
                        Mathf.Min(Mathf.Abs(size.z * scale.z), MaxExtent)) * 0.5f;
                }
            }

            Velocity = _hasLastCenter && deltaTime > 0f ? (WorldCenter - _lastCenter) / deltaTime : Vector3.zero;
            _lastCenter = WorldCenter;
            _hasLastCenter = true;
        }

        /// <summary>The point of this shape nearest to <paramref name="p"/>; <paramref name="p"/> itself when it is inside.</summary>
        internal Vector3 ClosestPoint(Vector3 p)
        {
            if (shapeType == ShapeType.Box)
            {
                Vector3 local = Quaternion.Inverse(WorldRotation) * (p - WorldCenter);
                local = new Vector3(
                    Mathf.Clamp(local.x, -WorldHalfSize.x, WorldHalfSize.x),
                    Mathf.Clamp(local.y, -WorldHalfSize.y, WorldHalfSize.y),
                    Mathf.Clamp(local.z, -WorldHalfSize.z, WorldHalfSize.z));
                return WorldCenter + WorldRotation * local;
            }
            Vector3 core = ParelDynamicsMath.ClosestPointOnSegment(WorldA, WorldB, p);
            Vector3 offset = p - core;
            float distance = offset.magnitude;
            return distance <= WorldRadius ? p : core + offset * (WorldRadius / distance);
        }

        /// <summary>
        /// Nearest points of two shapes, by projecting back and forth (both are convex, so it settles quickly).
        /// Overlapping shapes end on the same point.
        /// </summary>
        internal static float Distance(ParelContactBase a, ParelContactBase b, out Vector3 onA, out Vector3 onB)
        {
            if (a.shapeType != ShapeType.Box && b.shapeType != ShapeType.Box)
            {
                ParelDynamicsMath.ClosestPointsBetweenSegments(a.WorldA, a.WorldB, b.WorldA, b.WorldB, out Vector3 coreA, out Vector3 coreB);
                Vector3 between = coreB - coreA;
                float centres = between.magnitude;
                Vector3 direction = centres > 1e-6f ? between / centres : Vector3.up;
                onA = coreA + direction * Mathf.Min(a.WorldRadius, centres);
                onB = coreB - direction * Mathf.Min(b.WorldRadius, centres);
                return Mathf.Max(0f, centres - a.WorldRadius - b.WorldRadius);
            }
            onA = a.ClosestPoint(b.WorldCenter);
            onB = b.ClosestPoint(onA);
            for (int i = 0; i < 6; i++)
            {
                Vector3 nextA = a.ClosestPoint(onB);
                Vector3 nextB = b.ClosestPoint(nextA);
                bool settled = (nextA - onA).sqrMagnitude < 1e-10f && (nextB - onB).sqrMagnitude < 1e-10f;
                onA = nextA;
                onB = nextB;
                if (settled) break;
            }
            return Vector3.Distance(onA, onB);
        }

        internal bool SharesTagWith(ParelContactBase other)
        {
            if (collisionTags == null || other.collisionTags == null) return false;
            for (int i = 0; i < collisionTags.Count && i < MaxTags; i++)
            {
                string tag = collisionTags[i];
                if (string.IsNullOrEmpty(tag)) continue;
                for (int j = 0; j < other.collisionTags.Count && j < MaxTags; j++)
                {
                    if (string.Equals(tag, other.collisionTags[j], System.StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        protected virtual void OnDrawGizmosSelected()
        {
            UpdateWorld(0f);
            Gizmos.color = this is ParelContactReceiver ? new Color(0.4f, 1f, 0.5f, 0.85f) : new Color(1f, 0.45f, 0.8f, 0.85f);
            if (shapeType == ShapeType.Box)
            {
                Gizmos.matrix = Matrix4x4.TRS(WorldCenter, WorldRotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, WorldHalfSize * 2f);
                Gizmos.matrix = Matrix4x4.identity;
                return;
            }
            Gizmos.DrawWireSphere(WorldA, WorldRadius);
            if (shapeType == ShapeType.Capsule)
            {
                Gizmos.DrawWireSphere(WorldB, WorldRadius);
                Gizmos.DrawLine(WorldA, WorldB);
            }
        }
    }
}
