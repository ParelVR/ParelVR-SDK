using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>Shape + tags shared by Contact Senders and Receivers.</summary>
    public abstract class ParelContactBase : MonoBehaviour
    {
        public enum ShapeType
        {
            Sphere = 0,
            Capsule = 1,
        }

        [Tooltip("Transform the shape follows (empty = this object).")]
        public Transform rootTransform;
        public ShapeType shapeType = ShapeType.Sphere;
        public float radius = 0.05f;
        [Tooltip("Capsule: total height including the rounded ends, along local Y.")]
        public float height = 0.2f;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        [Tooltip("A sender and receiver touch only if they share at least one tag.")]
        public List<string> collisionTags = new List<string>();
        [Tooltip("Only exists on the wearer's own client.")]
        public bool localOnly;

        /// <summary>Set by the game on auto-created avatar contacts.</summary>
        [System.NonSerialized] public Transform OwnerRootOverride;

        internal Vector3 WorldA;
        internal Vector3 WorldB;
        internal float WorldRadius;
        internal Vector3 WorldCenter;
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

        internal void UpdateWorld(float deltaTime)
        {
            Transform root = Root;
            Vector3 scale = root.lossyScale;
            float s = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            WorldCenter = root.TransformPoint(position);
            WorldRadius = Mathf.Max(0f, radius * s);
            if (shapeType == ShapeType.Capsule)
            {
                Vector3 up = root.rotation * rotation * Vector3.up;
                float half = Mathf.Max(0f, height * s * 0.5f - WorldRadius);
                WorldA = WorldCenter - up * half;
                WorldB = WorldCenter + up * half;
            }
            else
            {
                WorldA = WorldCenter;
                WorldB = WorldCenter;
            }

            Velocity = _hasLastCenter && deltaTime > 0f ? (WorldCenter - _lastCenter) / deltaTime : Vector3.zero;
            _lastCenter = WorldCenter;
            _hasLastCenter = true;
        }

        internal bool SharesTagWith(ParelContactBase other)
        {
            if (collisionTags == null || other.collisionTags == null) return false;
            for (int i = 0; i < collisionTags.Count; i++)
            {
                string tag = collisionTags[i];
                if (string.IsNullOrEmpty(tag)) continue;
                for (int j = 0; j < other.collisionTags.Count; j++)
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
            Gizmos.DrawWireSphere(WorldA, WorldRadius);
            if (shapeType == ShapeType.Capsule)
            {
                Gizmos.DrawWireSphere(WorldB, WorldRadius);
                Gizmos.DrawLine(WorldA, WorldB);
            }
        }
    }
}
