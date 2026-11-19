using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// 3D Audio Raycast Zone: a sound that fills an area instead of coming from one point (a river, a waterfall, rain on
    /// a roof, a crowd, the hum of a room). Outside the zone the sound comes from the zone's nearest point to the
    /// listener, so a river sounds like it runs along beside you and a room's hum comes out of its doorway. Inside the
    /// zone it is all around you. It plays through the headset's 3D audio, walls between the listener and the zone
    /// muffle it and turn it down, it loses its highs far away, and it echoes as much as the place it is in does.
    /// The zone is a box, a sphere, or the colliders on this object and its children (use trigger colliders so
    /// players can walk through them; several boxes make a long river or an L-shaped room).
    /// Adding the component makes a child "Zone Sound" with the AudioSource: put your clip on that.
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/3D Audio Raycast Zone")]
    [DisallowMultipleComponent]
    public sealed class ParelRaycastAudioZone : ParelRaycastAudio
    {
        public enum ZoneShape
        {
            Box,
            Sphere,
            Colliders
        }

        [Header("Zone")]
        [Tooltip("The sound. It must be on a child object (the zone moves it to the nearest point); adding the component " +
                 "makes one called Zone Sound.")]
        public AudioSource sound;
        public ZoneShape shape = ZoneShape.Box;
        [Tooltip("The middle of the box or sphere, from this object.")]
        public Vector3 center;
        [Tooltip("The box's size (scaled by this object).")]
        public Vector3 size = new Vector3(6f, 3f, 6f);
        [Tooltip("The sphere's radius (scaled by this object).")]
        [Min(0.01f)] public float radius = 4f;
        [Tooltip("Meters in from the edge it takes for the sound to go from coming from the edge to all around you.")]
        [Min(0f)] public float insideFade = 1.5f;
        [Tooltip("Deep inside: 0 = all around you, 1 = still from the nearest edge (box and sphere).")]
        [Range(0f, 1f)] public float insideDirection;

        public override AudioSource Sound
        {
            get
            {
                if (sound == null)
                {
                    foreach (AudioSource s in GetComponentsInChildren<AudioSource>(true))
                        if (s.transform != transform) { sound = s; break; }
                    if (sound == null) sound = GetComponent<AudioSource>();
                }

                return sound;
            }
        }

        /// <summary>0 outside the zone, 1 well inside it.</summary>
        public float Inside => _inside;

        private Collider[] _colliders = new Collider[0];
        private Vector3 _nearest, _edge;
        private bool _in, _placed, _fixedSound;
        private float _inside;

        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshColliders();
            AudioSource s = Sound;
            if (s == null) return;
            s.dopplerLevel = 0f;
            _placed = false;
            _inside = 0f;
            _fixedSound = s.transform == transform;
            if (_fixedSound)
                Debug.LogWarning($"[ParelVR] 3D Audio Raycast Zone '{name}': its AudioSource is on the zone object itself, so it " +
                                 "can't move to the nearest point. Put the AudioSource on a child object.", this);
        }

        /// <summary>Collects the zone's colliders again (after adding or removing some at runtime).</summary>
        public void RefreshColliders()
        {
            _colliders = GetComponentsInChildren<Collider>();
        }

        protected override void Place(Vector3 ear, float dt)
        {
            AudioSource s = Sound;
            if (s == null) return;

            float depth = Nearest(ear, out _nearest, out _edge, out _in);
            float to = !_in ? 0f : insideFade <= 0.001f || shape == ZoneShape.Colliders ? 1f : Mathf.Clamp01(depth / insideFade);
            _inside = _placed ? Mathf.MoveTowards(_inside, to, dt * 3f) : to;

            // Outside: the nearest point. Inside: towards the nearest edge, but no farther than the sound's Min Distance,
            // so it stays at full volume.
            Vector3 at = _nearest;
            if (_in)
            {
                Vector3 toEdge = _edge - ear;
                float d = toEdge.magnitude;
                at = d > 0.001f ? ear + toEdge / d * Mathf.Min(d, Mathf.Max(0.05f, s.minDistance)) : ear + Vector3.forward * 0.05f;
            }

            if (!_fixedSound)
            {
                Transform t = s.transform;
                t.position = _placed ? Vector3.Lerp(t.position, at, Mathf.Clamp01(dt * 12f)) : at;
            }

            _placed = true;
            float direction = shape == ZoneShape.Colliders ? 0f : insideDirection;
            s.spatialBlend = Mathf.Lerp(1f, direction, _inside);
        }

        protected override int RayTargets(Vector3 ear, Vector3[] targets)
        {
            // Inside the zone nothing stands between.
            if (_in) return 0;

            targets[0] = _nearest;
            float spread = Mathf.Clamp(Vector3.Distance(ear, _nearest) * 0.25f, 0.3f, 2f);
            Around(ear, _nearest, spread, targets, 1);
            for (int i = 1; i < 5; i++) Nearest(targets[i], out targets[i], out _, out _);
            return 5;
        }

        // The zone's nearest point to p, the nearest edge when p is inside, and how far inside p is (meters).
        private float Nearest(Vector3 p, out Vector3 nearest, out Vector3 edge, out bool inside)
        {
            switch (shape)
            {
                case ZoneShape.Sphere:
                {
                    Vector3 c = transform.TransformPoint(center);
                    Vector3 sc = transform.lossyScale;
                    float r = radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
                    Vector3 off = p - c;
                    float d = off.magnitude;
                    Vector3 dir = d > 0.0001f ? off / d : transform.forward;
                    edge = c + dir * r;
                    inside = d <= r;
                    nearest = inside ? p : edge;
                    return inside ? r - d : 0f;
                }

                case ZoneShape.Colliders:
                {
                    nearest = edge = p;
                    inside = false;
                    float best = float.MaxValue;
                    for (int i = 0; i < _colliders.Length; i++)
                    {
                        Collider c = _colliders[i];
                        if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                        bool exact = !(c is MeshCollider mesh) || mesh.convex;
                        Vector3 q = exact ? c.ClosestPoint(p) : c.bounds.ClosestPoint(p);
                        float d = (q - p).sqrMagnitude;
                        if (d < best)
                        {
                            best = d;
                            nearest = q;
                        }
                    }

                    if (best == float.MaxValue)
                    {
                        nearest = edge = transform.position;
                        return 0f;
                    }

                    inside = best < 0.000001f;
                    edge = nearest;
                    return 0f;
                }

                default:
                {
                    Vector3 local = transform.InverseTransformPoint(p) - center;
                    Vector3 half = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
                    var clamped = new Vector3(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y),
                        Mathf.Clamp(local.z, -half.z, half.z));
                    inside = clamped == local;
                    if (!inside)
                    {
                        nearest = edge = transform.TransformPoint(center + clamped);
                        return 0f;
                    }

                    // the nearest face, in meters
                    Vector3 sc = transform.lossyScale;
                    float dx = (half.x - Mathf.Abs(local.x)) * Mathf.Abs(sc.x);
                    float dy = (half.y - Mathf.Abs(local.y)) * Mathf.Abs(sc.y);
                    float dz = (half.z - Mathf.Abs(local.z)) * Mathf.Abs(sc.z);
                    Vector3 face = local;
                    float depth;
                    if (dx <= dy && dx <= dz) { face.x = local.x < 0f ? -half.x : half.x; depth = dx; }
                    else if (dy <= dz) { face.y = local.y < 0f ? -half.y : half.y; depth = dy; }
                    else { face.z = local.z < 0f ? -half.z : half.z; depth = dz; }
                    nearest = p;
                    edge = transform.TransformPoint(center + face);
                    return depth;
                }
            }
        }

#if UNITY_EDITOR
        private void Reset()
        {
            // The sound goes on a child so the zone can move it to the nearest point.
            foreach (AudioSource s in GetComponentsInChildren<AudioSource>(true))
                if (s.transform != transform) { sound = s; return; }

            var child = new GameObject("Zone Sound");
            UnityEditor.Undo.RegisterCreatedObjectUndo(child, "Add 3D Audio Raycast Zone");
            child.transform.SetParent(transform, false);
            child.transform.localPosition = center;
            AudioSource made = child.AddComponent<AudioSource>();
            AudioSource own = GetComponent<AudioSource>();
            if (own != null)
            {
                // an AudioSource already on this object moves into the child
                UnityEditor.EditorUtility.CopySerialized(own, made);
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (own != null) UnityEditor.Undo.DestroyObjectImmediate(own);
                };
                Debug.Log($"[ParelVR] 3D Audio Raycast Zone: moved '{name}'s AudioSource onto its child Zone Sound, so the zone " +
                          "can move the sound to the nearest point.", child);
            }
            else
            {
                made.playOnAwake = true;
                made.loop = true;
                made.minDistance = 2f;
                made.maxDistance = 30f;
                made.rolloffMode = AudioRolloffMode.Logarithmic;
            }

            made.spatialBlend = 1f;
            made.dopplerLevel = 0f;
            sound = made;
        }

        private void OnDrawGizmos()
        {
            DrawZone(new Color(0.35f, 0.8f, 1f, 0.55f), false);
        }

        private void OnDrawGizmosSelected()
        {
            DrawZone(new Color(0.35f, 0.8f, 1f, 0.12f), true);
            DrawHearing();
        }

        private void DrawZone(Color color, bool filled)
        {
            Gizmos.color = color;
            switch (shape)
            {
                case ZoneShape.Sphere:
                {
                    Vector3 sc = transform.lossyScale;
                    float r = radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
                    Vector3 c = transform.TransformPoint(center);
                    if (filled) Gizmos.DrawSphere(c, r);
                    else Gizmos.DrawWireSphere(c, r);
                    break;
                }

                case ZoneShape.Colliders:
                    foreach (Collider c in GetComponentsInChildren<Collider>())
                    {
                        Bounds b = c.bounds;
                        if (filled) Gizmos.DrawCube(b.center, b.size);
                        else Gizmos.DrawWireCube(b.center, b.size);
                    }

                    break;

                default:
                    Gizmos.matrix = transform.localToWorldMatrix;
                    if (filled) Gizmos.DrawCube(center, size);
                    else Gizmos.DrawWireCube(center, size);
                    Gizmos.matrix = Matrix4x4.identity;
                    break;
            }
        }
#endif
    }
}
