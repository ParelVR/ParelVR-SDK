using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// What the two 3D Audio Raycast components share: the sound plays through the headset's 3D audio (the same as
    /// players' voices), rays from the listener to the sound find the walls, floors and props in between, which
    /// muffle it and turn it down, far sounds lose their highs as they do in the air, and rays round the sound measure
    /// the room it is in so closed and big rooms echo. The AudioSource's own Min/Max Distance and rolloff still decide
    /// how it fades with distance, and its volume can still be changed (World Menu sliders, Volt): the muffling is laid
    /// on top of whatever volume it has.
    /// </summary>
    public abstract class ParelRaycastAudio : MonoBehaviour
    {
        // Ignore Raycast (2), UI (5), the local and remote players' avatars (6, 7), the handheld camera (8), the
        // menus (9) and avatar pedestals (10).
        public const int DefaultOccluderMask = ~((1 << 2) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10));

        [Header("3D audio")]
        [Tooltip("Play through the headset's 3D audio, the same as players' voices. Off = Unity's plain left/right panning.")]
        public bool headset3DAudio = true;

        [Header("Raycast muffling")]
        [Tooltip("Walls, floors and props between the listener and the sound muffle it and turn it down.")]
        public bool muffleThroughWalls = true;
        [Tooltip("What blocks the sound. Players, UI and Ignore Raycast are left out. Trigger colliders never block it.")]
        public LayerMask occluders = DefaultOccluderMask;
        [Tooltip("How much quieter it is fully behind a wall: 0 = not at all, 1 = silent.")]
        [Range(0f, 1f)] public float wallVolumeDrop = 0.55f;
        [Tooltip("How muffled it is fully behind a wall: the highest pitch that still gets through (Hz). Lower = more muffled.")]
        [Range(200f, 22000f)] public float wallCutoff = 800f;
        [Tooltip("Seconds it takes to muffle fully, or clear, when a wall comes between or goes away.")]
        [Range(0.05f, 2f)] public float smoothing = 0.4f;
        [Tooltip("Far sounds lose their highs, the way they do in the air (up to the AudioSource's Max Distance).")]
        public bool airAbsorption = true;

        [Header("Room echo")]
        [Tooltip("Rays round the sound measure the room it is in: closed rooms echo, big ones echo longer, outdoors stays dry.")]
        public bool roomEcho = true;
        [Tooltip("How strong the echo is.")]
        [Range(0f, 1f)] public float echoAmount = 1f;

        /// <summary>0 = nothing between the listener and the sound, 1 = fully behind walls.</summary>
        public float Occlusion => _occlusion;

        /// <summary>The volume before muffling. Setting the AudioSource's volume directly works too.</summary>
        public float Volume
        {
            get => _baseVolume;
            set => _baseVolume = Mathf.Clamp01(value);
        }

        /// <summary>The AudioSource this drives.</summary>
        public abstract AudioSource Sound { get; }

        private const int RayCount = 5;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];
        private static AudioListener _listener;
        private static float _nextListenerLook;

        private AudioSource _source;
        private AudioLowPassFilter _muffle;
        private AudioReverbFilter _echo;
        private float _baseVolume = 1f, _lastVolume = -1f, _lastFactor = 1f;
        private float _occlusion, _occlusionTo, _nextRays, _nextRoom, _enclosed, _roomSize = 30f;
        private readonly Vector3[] _targets = new Vector3[RayCount];

        protected virtual void OnEnable()
        {
            _source = Sound;
            if (_source == null) return;

            _baseVolume = _source.volume;
            _lastVolume = -1f;
            _lastFactor = 1f;
            _occlusion = _occlusionTo = 0f;
            // spread the rays of many sounds over different frames
            _nextRays = Time.unscaledTime + Random.value * 0.1f;
            _nextRoom = Time.unscaledTime + Random.value * 0.5f;

            _source.spatialBlend = 1f;
            if (headset3DAudio)
            {
                _source.spatialize = true;
                _source.spatializePostEffects = true;
            }

            _muffle = _source.GetComponent<AudioLowPassFilter>();
            if (_muffle == null) _muffle = _source.gameObject.AddComponent<AudioLowPassFilter>();
            _muffle.enabled = true;
            _muffle.cutoffFrequency = 22000f;
            _muffle.lowpassResonanceQ = 1f;

            _echo = _source.GetComponent<AudioReverbFilter>();
            if (roomEcho)
            {
                if (_echo == null) _echo = _source.gameObject.AddComponent<AudioReverbFilter>();
                _echo.enabled = true;
                _echo.reverbPreset = AudioReverbPreset.User;
                _echo.dryLevel = 0f;
                _echo.room = -10000f;
                _echo.reverbLevel = -10000f;
            }
            else if (_echo != null) _echo.enabled = false;
        }

        protected virtual void OnDisable()
        {
            if (_source == null) return;
            if (_lastVolume >= 0f && Mathf.Abs(_source.volume - _lastVolume) < 0.0005f) _source.volume = _baseVolume;
            if (_muffle != null) _muffle.enabled = false;
            if (_echo != null) _echo.enabled = false;
        }

        private void LateUpdate()
        {
            if (_source == null) return;
            Transform ear = Ear();
            if (ear == null) return;

            float dt = Time.unscaledDeltaTime;
            Vector3 earAt = ear.position;
            Place(earAt, dt);
            Vector3 soundAt = _source.transform.position;
            float distance = Vector3.Distance(earAt, soundAt);

            // Someone else (a World Menu slider, a Volt script) set the volume: that is the new volume to muffle.
            if (_lastVolume >= 0f && Mathf.Abs(_source.volume - _lastVolume) > 0.0005f)
                _baseVolume = _lastFactor > 0.001f ? Mathf.Clamp01(_source.volume / _lastFactor) : _source.volume;

            // What stands between: a few rays from the ear to the sound, ten times a second while it is near.
            if (Time.unscaledTime >= _nextRays)
            {
                bool near = distance <= Mathf.Max(_source.maxDistance, 1f) * 1.5f;
                _nextRays = Time.unscaledTime + (near ? 0.1f : 0.5f);
                _occlusionTo = muffleThroughWalls && _source.isPlaying ? Blocked(earAt) : 0f;
            }

            float rate = dt / Mathf.Max(0.05f, smoothing);
            _occlusion = Mathf.MoveTowards(_occlusion, _occlusionTo, rate);

            float factor = 1f - _occlusion * wallVolumeDrop;
            _lastFactor = factor;
            _source.volume = _lastVolume = Mathf.Clamp01(_baseVolume * factor);

            float cutoff = 22000f;
            if (airAbsorption)
            {
                float span = Mathf.Max(0.01f, _source.maxDistance - _source.minDistance);
                float far = Mathf.Clamp01((distance - _source.minDistance) / span);
                cutoff = Mathf.Lerp(22000f, 5000f, far * far);
            }

            cutoff = Mathf.Lerp(cutoff, Mathf.Min(cutoff, wallCutoff), _occlusion);
            if (_muffle != null) _muffle.cutoffFrequency = cutoff;

            if (roomEcho && _echo != null)
            {
                if (Time.unscaledTime >= _nextRoom)
                {
                    _nextRoom = Time.unscaledTime + 1.5f + Random.value * 0.5f;
                    MeasureRoom(soundAt);
                }

                float closed = _enclosed * echoAmount;
                _echo.room = Mathf.Lerp(-10000f, Mathf.Lerp(-1800f, -300f, _enclosed), closed > 0.15f ? 1f : closed / 0.15f);
                _echo.reverbLevel = Mathf.Lerp(-10000f, Mathf.Lerp(-2600f, -500f, _enclosed), echoAmount);
                _echo.decayTime = Mathf.Lerp(0.35f, 3.2f, Mathf.Clamp01(_roomSize / 18f));
                _echo.reflectionsDelay = Mathf.Lerp(0.004f, 0.06f, Mathf.Clamp01(_roomSize / 20f));
            }
        }

        /// <summary>Moves the sound for this frame (a zone puts it at its nearest point to the listener).</summary>
        protected virtual void Place(Vector3 ear, float dt) { }

        /// <summary>Fills the points the rays go to; the first is the middle and counts double. Returns how many.</summary>
        protected abstract int RayTargets(Vector3 ear, Vector3[] targets);

        // The share of the sound the listener cannot see.
        private float Blocked(Vector3 ear)
        {
            int count = Mathf.Min(RayTargets(ear, _targets), RayCount);
            if (count <= 0) return 0f;

            int blocked = 0, total = 0;
            for (int i = 0; i < count; i++)
            {
                int weight = i == 0 ? 2 : 1;
                total += weight;
                Vector3 dir = _targets[i] - ear;
                float length = dir.magnitude - 0.05f;
                if (length <= 0.05f) continue;
                if (Hit(ear, dir / (length + 0.05f), length)) blocked += weight;
            }

            return total > 0 ? Mathf.Clamp01((float)blocked / total) : 0f;
        }

        // Anything in the way that is not part of this sound's own object.
        private bool Hit(Vector3 from, Vector3 dir, float length)
        {
            int n = Physics.RaycastNonAlloc(from, dir, Hits, length, occluders, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider c = Hits[i].collider;
                if (c != null && !c.transform.IsChildOf(transform)) return true;
            }

            return false;
        }

        // Rays out from the sound in eight directions and up: how far the walls are and how many there are.
        private void MeasureRoom(Vector3 from)
        {
            const float Reach = 25f;
            int hits = 0;
            float total = 0f;
            float turn = Random.value * 45f;
            for (int i = 0; i < 9; i++)
            {
                Vector3 dir = i == 8 ? Vector3.up : Quaternion.Euler(0f, i * 45f + turn, 0f) * Vector3.forward;
                float d = Reach;
                int n = Physics.RaycastNonAlloc(from, dir, Hits, Reach, occluders, QueryTriggerInteraction.Ignore);
                for (int h = 0; h < n; h++)
                {
                    Collider c = Hits[h].collider;
                    if (c == null || c.transform.IsChildOf(transform)) continue;
                    d = Mathf.Min(d, Hits[h].distance);
                }

                if (d < Reach) hits++;
                total += d;
            }

            _enclosed = hits / 9f;
            _roomSize = total / 9f;
        }

        /// <summary>The listener: the local player's ears.</summary>
        protected static Transform Ear()
        {
            if ((_listener == null || !_listener.isActiveAndEnabled) && Time.unscaledTime >= _nextListenerLook)
            {
                _nextListenerLook = Time.unscaledTime + 0.5f;
                _listener = FindFirstObjectByType<AudioListener>();
            }

            if (_listener != null && _listener.isActiveAndEnabled) return _listener.transform;
            Camera main = Camera.main;
            return main != null ? main.transform : null;
        }

        /// <summary>Four points round <paramref name="centre"/>, square to the line from the ear, <paramref name="size"/> out.</summary>
        protected static void Around(Vector3 ear, Vector3 centre, float size, Vector3[] targets, int start)
        {
            Vector3 look = centre - ear;
            if (look.sqrMagnitude < 0.0001f) look = Vector3.forward;
            Vector3 side = Vector3.Cross(look, Vector3.up);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(look, Vector3.right);
            side = side.normalized * size;
            Vector3 up = Vector3.Cross(side, look).normalized * size;
            targets[start] = centre + up;
            targets[start + 1] = centre - up;
            targets[start + 2] = centre + side;
            targets[start + 3] = centre - side;
        }

#if UNITY_EDITOR
        protected void DrawHearing()
        {
            if (!Application.isPlaying || _source == null) return;
            Transform ear = Ear();
            if (ear == null) return;
            Gizmos.color = Color.Lerp(new Color(0.3f, 1f, 0.5f), new Color(1f, 0.35f, 0.3f), _occlusion);
            Gizmos.DrawLine(ear.position, _source.transform.position);
            Gizmos.DrawWireSphere(_source.transform.position, 0.12f);
        }
#endif
    }
}
