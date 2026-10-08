using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's PhysBone Collider: a sphere, capsule or plane that PhysBones can't pass through (or,
    /// with Inside Bounds, can't leave). Add it to a PhysBone's Colliders list. ParelVR also puts these
    /// on every avatar's hands and fingers automatically, so people can touch each other's hair and tails.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/PhysBone Collider")]
    public sealed class ParelPhysBoneCollider : MonoBehaviour
    {
        public enum ShapeType
        {
            Sphere = 0,
            Capsule = 1,
            Plane = 2,
        }

        [Tooltip("Transform the shape follows (empty = this object).")]
        public Transform rootTransform;
        public ShapeType shapeType = ShapeType.Sphere;
        [Tooltip("Keep bones inside the shape instead of outside it (sphere / capsule).")]
        public bool insideBounds;
        public float radius = 0.05f;
        [Tooltip("Capsule: total height, including the rounded ends. Along the shape's local Y axis.")]
        public float height = 0.2f;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        [Tooltip("Treat PhysBone bones as spheres at their tips instead of capsules along their length.")]
        public bool bonesAsSpheres;

        /// <summary>Set by the game on the auto-created hand / finger colliders.</summary>
        [System.NonSerialized] public Transform OwnerRootOverride;

        internal Vector3 WorldA;
        internal Vector3 WorldB;
        internal Vector3 WorldNormal;
        internal float WorldRadius;

        public Transform Root => rootTransform != null ? rootTransform : transform;

        /// <summary>The avatar root this collider belongs to (null for world colliders).</summary>
        public Transform OwnerRoot
        {
            get
            {
                if (OwnerRootOverride != null) return OwnerRootOverride;
                ParelAvatarDescriptor descriptor = GetComponentInParent<ParelAvatarDescriptor>();
                return descriptor != null ? descriptor.transform : null;
            }
        }

        internal void UpdateWorld()
        {
            Transform root = Root;
            Vector3 scale = root.lossyScale;
            float s = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            Vector3 center = root.TransformPoint(position);
            Quaternion worldRotation = root.rotation * rotation;
            Vector3 up = worldRotation * Vector3.up;

            WorldRadius = Mathf.Max(0f, radius * s);
            WorldNormal = up;
            if (shapeType == ShapeType.Capsule)
            {
                float half = Mathf.Max(0f, height * s * 0.5f - WorldRadius);
                WorldA = center - up * half;
                WorldB = center + up * half;
            }
            else
            {
                WorldA = center;
                WorldB = center;
            }
        }

        /// <summary>Pushes a sphere (point + radius) out of (or into) the shape. True if it moved.</summary>
        internal bool Collide(ref Vector3 point, float pointRadius)
        {
            switch (shapeType)
            {
                case ShapeType.Plane:
                {
                    float distance = Vector3.Dot(point - WorldA, WorldNormal);
                    if (distance >= pointRadius) return false;
                    point += WorldNormal * (pointRadius - distance);
                    return true;
                }
                default:
                {
                    Vector3 closest = shapeType == ShapeType.Capsule ? ParelDynamicsMath.ClosestPointOnSegment(WorldA, WorldB, point) : WorldA;
                    Vector3 delta = point - closest;
                    float distance = delta.magnitude;
                    if (!insideBounds)
                    {
                        float min = WorldRadius + pointRadius;
                        if (distance >= min) return false;
                        Vector3 direction = distance > 1e-6f ? delta / distance : WorldNormal;
                        point = closest + direction * min;
                        return true;
                    }
                    else
                    {
                        float max = Mathf.Max(0f, WorldRadius - pointRadius);
                        if (distance <= max) return false;
                        point = closest + delta / distance * max;
                        return true;
                    }
                }
            }
        }

        private void OnEnable() => ParelPhysBoneSystem.RegisterCollider(this);
        private void OnDisable() => ParelPhysBoneSystem.UnregisterCollider(this);

        private void OnDrawGizmosSelected()
        {
            UpdateWorld();
            Gizmos.color = insideBounds ? new Color(1f, 0.6f, 0.2f, 0.8f) : new Color(0.3f, 0.9f, 1f, 0.8f);
            switch (shapeType)
            {
                case ShapeType.Plane:
                    Gizmos.DrawLine(WorldA, WorldA + WorldNormal * 0.1f);
                    Gizmos.DrawWireSphere(WorldA, 0.01f);
                    break;
                case ShapeType.Capsule:
                    Gizmos.DrawWireSphere(WorldA, WorldRadius);
                    Gizmos.DrawWireSphere(WorldB, WorldRadius);
                    Gizmos.DrawLine(WorldA, WorldB);
                    break;
                default:
                    Gizmos.DrawWireSphere(WorldA, WorldRadius);
                    break;
            }
        }
    }

    internal static class ParelDynamicsMath
    {
        public static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq < 1e-12f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSq);
            return a + ab * t;
        }

        /// <summary>Closest points between segments [a0,a1] and [b0,b1].</summary>
        public static void ClosestPointsBetweenSegments(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, out Vector3 onA, out Vector3 onB)
        {
            Vector3 d1 = a1 - a0;
            Vector3 d2 = b1 - b0;
            Vector3 r = a0 - b0;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            float s;
            float t;

            if (a <= 1e-12f && e <= 1e-12f)
            {
                onA = a0;
                onB = b0;
                return;
            }
            if (a <= 1e-12f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-12f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denominator = a * e - b * b;
                    s = denominator > 1e-12f ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            onA = a0 + d1 * s;
            onB = b0 + d2 * t;
        }

        /// <summary>Is <paramref name="owner"/> allowed to interact under <paramref name="permission"/>?</summary>
        public static bool Allows(ParelDynamicsPermission permission, Transform owner, Transform other)
        {
            switch (permission)
            {
                case ParelDynamicsPermission.Everyone: return true;
                case ParelDynamicsPermission.SelfOnly: return owner != null && owner == other;
                case ParelDynamicsPermission.OthersOnly: return owner == null || owner != other;
                default: return false;
            }
        }
    }
}
