using UnityEngine;
using UnityEngine.Serialization;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// 3D Audio Raycast Zone: every sound inside it gets 3D Audio Raycast. Put it round a room, a building or the whole
    /// world and every AudioSource whose object is inside (also ones that are spawned or move in later) plays through
    /// the headset's 3D audio, is muffled by the walls between you and it by what they are made of, finds its way round
    /// corners and through doorways, reflects off the walls, floor and ceiling round you, fills the room you are in
    /// with its echo and loses its highs far away. A sound that leaves the zone goes back to how it was.
    /// Players' voices, the menus, sounds with a 3D Audio Raycast Ignore, and sounds with their own 3D Audio Raycast
    /// Single Object (which uses its own settings) are left alone.
    /// The zone is a box, a sphere, or the colliders on this object and its children (trigger colliders, so players
    /// can walk through; several boxes make an L-shaped building or a long street).
    /// Optionally it also has an Area Sound that fills the zone instead of coming from one point (a river, rain on a
    /// roof, a room's hum): outside the zone it comes from the zone's nearest point, inside it is all around you.
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
        [Tooltip("Every sound inside the zone gets 3D Audio Raycast, also ones that are spawned or move in later.")]
        public bool takeEverySound = true;
        [Tooltip("Sounds set to 2D (Spatial Blend 0) inside the zone are made 3D too, coming from their object. " +
                 "Off = 2D sounds stay exactly as they are (or put a 3D Audio Raycast Ignore on background music).")]
        public bool include2DSounds = true;
        [Tooltip("How big each sound is (meters), for the muffling rays and how big it sounds up close. 0 = from the " +
                 "size of the object it is on.")]
        [Min(0f)] public float soundSize;
        public ZoneShape shape = ZoneShape.Box;
        [Tooltip("The middle of the box or sphere, from this object.")]
        public Vector3 center;
        [Tooltip("The box's size (scaled by this object).")]
        public Vector3 size = new Vector3(10f, 4f, 10f);
        [Tooltip("The sphere's radius (scaled by this object).")]
        [Min(0.01f)] public float radius = 8f;

        [Header("Area Sound (optional)")]
        [Tooltip("A sound that fills the whole zone instead of coming from one point (a river, rain on a roof, a room's " +
                 "hum). Put it on a child object: outside the zone it plays from the zone's nearest point, inside it is " +
                 "all around you. Leave empty for none.")]
        [FormerlySerializedAs("sound")]
        public AudioSource areaSound;
        [Tooltip("Meters in from the edge it takes for the Area Sound to go from coming from the edge to all around you.")]
        [Min(0f)] public float insideFade = 1.5f;
        [Tooltip("Deep inside: 0 = the Area Sound is all around you, 1 = still from the nearest edge (box and sphere).")]
        [Range(0f, 1f)] public float insideDirection;

        public override float Occlusion => _area != null ? _area.Occlusion : 0f;

        /// <summary>0 outside the zone, 1 well inside it.</summary>
        public float Inside => _inside;

        private Collider[] _colliders = new Collider[0];
        private ParelRaycastAudioVoice _area;
        private Vector3 _nearest, _edge;
        private bool _in, _placed, _fixedSound, _warned;
        private float _inside, _nextTry;

        protected override void OnEnable()
        {
            RefreshColliders();
            base.OnEnable();
        }

        /// <summary>Collects the zone's colliders again (after adding or removing some at runtime).</summary>
        public void RefreshColliders()
        {
            Collider[] all = GetComponentsInChildren<Collider>();
            int triggers = 0;
            for (int i = 0; i < all.Length; i++)
                if (all[i].isTrigger) triggers++;
            if (triggers == 0 || triggers == all.Length)
            {
                _colliders = all;
                return;
            }

            // with both, the triggers are the zone and the rest is what's in it
            _colliders = new Collider[triggers];
            int n = 0;
            for (int i = 0; i < all.Length; i++)
                if (all[i].isTrigger) _colliders[n++] = all[i];
        }

        internal override void Begin()
        {
            _area = null;
            _placed = false;
            _inside = 0f;
            if (areaSound == null) return;
            _fixedSound = areaSound.transform == transform;
            if (_fixedSound && !_warned)
            {
                _warned = true;
                Debug.LogWarning($"[ParelVR] 3D Audio Raycast Zone '{name}': its Area Sound is on the zone object itself, so it " +
                                 "can't move to the nearest point. Put the AudioSource on a child object.", this);
            }

            areaSound.dopplerLevel = 0f;
            _area = ParelRaycastAudioEngine.Attach(this, areaSound, 0f, true);
        }

        /// <summary>Whether a sound is one the zone should take (the engine asks once it is inside).</summary>
        internal bool Accepts(AudioSource s)
        {
            if (s == null || s == areaSound) return false;
            GameObject go = s.gameObject;
            if (go.scene != gameObject.scene) return false; // the game's own sounds live in its own scenes
            int layer = go.layer;
            if (layer >= 5 && layer <= 10) return false; // UI, players, handheld camera, menus, pedestals
            if (!include2DSounds && s.spatialBlend <= 0.001f) return false;
            if (s.GetComponent<ParelRaycastAudioObject>() != null) return false;
            if (s.GetComponentInParent<ParelRaycastAudioIgnore>(true) != null) return false;
            if (SkipSound != null)
            {
                try
                {
                    if (SkipSound(s)) return false;
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }

            return true;
        }

        /// <summary>How big a sound in the zone is.</summary>
        internal float SizeFor(AudioSource s)
        {
            if (soundSize > 0f) return soundSize;
            Renderer r = s.GetComponent<Renderer>();
            if (r == null) return 0.2f;
            return Mathf.Clamp(r.bounds.extents.magnitude * 0.6f, 0.1f, 2f);
        }

        /// <summary>Whether <paramref name="p"/> is inside the zone, or within <paramref name="margin"/> meters of it.</summary>
        internal bool Contains(Vector3 p, float margin)
        {
            switch (shape)
            {
                case ZoneShape.Sphere:
                {
                    Vector3 sc = transform.lossyScale;
                    float r = radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)));
                    return (p - transform.TransformPoint(center)).sqrMagnitude <= (r + margin) * (r + margin);
                }

                case ZoneShape.Colliders:
                    for (int i = 0; i < _colliders.Length; i++)
                    {
                        Collider c = _colliders[i];
                        if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                        bool exact = !(c is MeshCollider mesh) || mesh.convex;
                        if (exact)
                        {
                            if ((c.ClosestPoint(p) - p).sqrMagnitude <= margin * margin + 0.000001f) return true;
                        }
                        else
                        {
                            Bounds b = c.bounds;
                            b.Expand(margin * 2f);
                            if (b.Contains(p)) return true;
                        }
                    }

                    return false;

                default:
                {
                    Vector3 local = transform.InverseTransformPoint(p) - center;
                    Vector3 sc = transform.lossyScale;
                    Vector3 half = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
                    half.x += margin / Mathf.Max(0.0001f, Mathf.Abs(sc.x));
                    half.y += margin / Mathf.Max(0.0001f, Mathf.Abs(sc.y));
                    half.z += margin / Mathf.Max(0.0001f, Mathf.Abs(sc.z));
                    return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
                }
            }
        }

        internal override void Prepare(Vector3 ear, float dt)
        {
            if (_area == null)
            {
                if (areaSound != null && Time.unscaledTime >= _nextTry)
                {
                    _nextTry = Time.unscaledTime + 1f;
                    Begin();
                }

                return;
            }

            AudioSource s = _area.Source;
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
            // big up close: the volumetric size grows with how far round you the zone is
            _area.Radius = Mathf.Lerp(0.5f, 3f, _inside);
        }

        internal override int RayTargets(ParelRaycastAudioVoice voice, Vector3 ear, Vector3[] targets)
        {
            if (voice != _area) return base.RayTargets(voice, ear, targets);

            // Inside the zone nothing stands between.
            if (_in) return 0;
            targets[0] = _nearest;
            float spread = Mathf.Clamp(Vector3.Distance(ear, _nearest) * 0.25f, 0.3f, 2f);
            Ring(ear, _nearest, spread, targets, 1, 6);
            for (int i = 1; i < 7; i++) Nearest(targets[i], out targets[i], out _, out _);
            return 7;
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
        private void OnValidate()
        {
            if (isActiveAndEnabled) RefreshColliders();
        }

        private void OnDrawGizmos()
        {
            DrawZone(new Color(0.35f, 0.8f, 1f, 0.55f), false);
        }

        private void OnDrawGizmosSelected()
        {
            DrawZone(new Color(0.35f, 0.8f, 1f, 0.12f), true);
            foreach (ParelRaycastAudioVoice v in ParelRaycastAudioEngine.AllVoices)
                if (v != null && v.Owner == this) DrawHearing(v);
        }

        /// <summary>Play Mode, selected: the line to each sound (green clear, red blocked), its way round (yellow) and
        /// its reflections (faint blue).</summary>
        internal static void DrawHearing(ParelRaycastAudioVoice v)
        {
            if (!Application.isPlaying || v == null || v.Source == null) return;
            Transform ear = ParelRaycastAudioEngine.Ear();
            if (ear == null) return;
            Vector3 e = ear.position, s = v.Source.transform.position;
            Gizmos.color = Color.Lerp(new Color(0.3f, 1f, 0.5f), new Color(1f, 0.35f, 0.3f), v.Occlusion);
            Gizmos.DrawLine(e, s);
            Gizmos.DrawWireSphere(s, Mathf.Max(0.08f, v.Radius));
            if (v.HasPath)
            {
                Gizmos.color = new Color(1f, 0.85f, 0.2f);
                Gizmos.DrawLine(e, v.PathPoint);
                Gizmos.DrawLine(v.PathPoint, v.PathPoint2);
                if (v.PathBends == 2) Gizmos.DrawLine(v.PathPoint2, s);
                Gizmos.DrawWireSphere(v.PathPoint, 0.1f);
            }

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.35f);
            for (int i = 0; i < v.ReflectionCount; i++)
                Gizmos.DrawRay(e, v.ReflectionDir[i] * Mathf.Min(3f, v.ReflectionExtra[i] + 0.5f));
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
