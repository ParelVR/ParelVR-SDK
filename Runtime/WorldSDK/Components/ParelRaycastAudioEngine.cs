using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Runs 3D Audio Raycast for the whole world (one hidden object, made when the first component turns on, gone when
    /// the last turns off). Every frame, within a ray budget, it works out for each sound:
    /// <list type="bullet">
    /// <item>the direct path: a spread of rays from the ears to the sound, stepping through every surface on the way
    /// and multiplying in what each lets through by band (its 3D Audio Raycast Material);</item>
    /// <item>the way round when that is blocked: points the listener can see (along 32 probe rays round the head) that
    /// can see the sound, or two of them joined (one seen from each end), refined towards the corner it bends round;</item>
    /// <item>first reflections: the sound mirrored in the walls, floor and ceiling found round the listener, each
    /// checked with rays that both legs are open and that there is a surface where it bounces;</item>
    /// <item>air absorption over the distance.</item>
    /// </list>
    /// The same probe rays measure the listener's room (its volume, surface, how much its materials soak up and how
    /// open it is to the sky) and set the shared echo tail, an eight-line feedback delay network whose decay is the
    /// room's reverberation time (Sabine), shorter for the highs.
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(1000)]
    public sealed class ParelRaycastAudioEngine : MonoBehaviour
    {
        private const int ProbeCount = 32;
        private const float ProbeReach = 45f;
        private const float PlaneReach = 35f;
        private const int MaxCandidates = ProbeCount * 4;
        private static readonly float[] AirDbPerMeter = { 0.001f, 0.006f, 0.06f };
        private static readonly float[] BendDb = { 2.5f, 6f, 11f };
        private static readonly float[] AirSabine = { 0f, 0.0005f, 0.006f };

        private static ParelRaycastAudioEngine _instance;
        private static readonly List<ParelRaycastAudio> Owners = new List<ParelRaycastAudio>();
        private static readonly List<ParelRaycastAudioZone> Zones = new List<ParelRaycastAudioZone>();
        private static readonly List<ParelRaycastAudioVoice> Voices = new List<ParelRaycastAudioVoice>();
        private static readonly Dictionary<AudioSource, ParelRaycastAudioVoice> BySource = new Dictionary<AudioSource, ParelRaycastAudioVoice>();
        private static readonly Dictionary<Collider, ParelAcoustic> Materials = new Dictionary<Collider, ParelAcoustic>();
        private static readonly Dictionary<AudioSource, float> Skipped = new Dictionary<AudioSource, float>();
        private static readonly Dictionary<AudioSource, AnimationCurve> Curves = new Dictionary<AudioSource, AnimationCurve>();
        private static readonly Vector3[] Fibonacci = MakeFibonacci(ProbeCount);
        private static readonly Vector3[] Axes = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.up, Vector3.down };
        private static readonly object RingLock = new object();
        private readonly object _busSync = new object();
        private static AudioListener _listener;
        private static float _nextListenerLook;

        internal static bool HasSpatializer, MetaSpatializer;
        internal static bool BusLive => _instance != null && _instance._busLive;
        internal static List<ParelRaycastAudioVoice> AllVoices => Voices;

        private AudioSource _bus;
        private volatile bool _busLive;
        private int _sampleRate;
        private float _nextScan, _nextListener, _nextSort, _nextHousekeeping;
        private int _rays, _roundRobin, _housekeepingCount;
        private int _mask = ParelRaycastAudio.DefaultOccluderMask;
        private readonly Vector3[] _targets = new Vector3[8];

        // listener probes and the points along them the way round is looked for at
        private readonly Vector3[] _candidates = new Vector3[MaxCandidates];
        private readonly int[] _candidateProbe = new int[MaxCandidates];
        private int _candidateCount;
        private readonly Vector3[] _planeP = new Vector3[6], _planeN = new Vector3[6];
        private readonly float[] _planeA = new float[6];
        private readonly bool[] _planeOk = new bool[6];
        private readonly int[] _bestC = new int[6];
        private readonly float[] _bestCCost = new float[6];

        // the room round the listener, smoothed
        private float _open = 1f, _free = 3f, _aL = 0.1f, _aM = 0.2f, _aH = 0.3f;
        private bool _roomSeen;

        // ---- the echo tail (audio thread) ----
        private static readonly int[] LineBase = { 1427, 1601, 1867, 2053, 2251, 2399, 2617, 2797 };
        private static readonly int[] DiffuserBase = { 347, 113 };
        private float[] _ring, _busIn;
        private int _ringMask;
        private long _busRead;
        private float[][] _lines;
        private int[] _lineLen, _linePos;
        private readonly float[] _lineLp = new float[8], _lineG = new float[8], _lineA = new float[8], _u = new float[8];
        private float[][] _diff;
        private int[] _diffPos;
        private float _busFs = 48000f;
        private volatile float _rtLow = 0.6f, _rtHigh = 0.4f, _wet;
        private float _wetNow;
        private volatile bool _busReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            Owners.Clear();
            Zones.Clear();
            Voices.Clear();
            BySource.Clear();
            Materials.Clear();
            Skipped.Clear();
            Curves.Clear();
            _listener = null;
            _nextListenerLook = 0f;
        }

        // ------------------------------------------------------------------ registry

        internal static void Register(ParelRaycastAudio owner)
        {
            if (!Application.isPlaying || owner == null || Owners.Contains(owner)) return;
            Owners.Add(owner);
            if (owner is ParelRaycastAudioZone zone) Zones.Add(zone);
            Ensure();
            _instance.RefreshMask();
            _instance._nextScan = 0f;
            owner.Begin();
        }

        internal static void Unregister(ParelRaycastAudio owner)
        {
            if (owner == null) return;
            Owners.Remove(owner);
            if (owner is ParelRaycastAudioZone zone) Zones.Remove(zone);
            for (int i = Voices.Count - 1; i >= 0; i--)
                if (Voices[i] == null || Voices[i].Owner == owner) Release(Voices[i]);
            if (_instance != null)
            {
                if (Owners.Count == 0)
                {
                    // forget it now: a world switch can turn the next zone on before the end of this frame
                    GameObject dying = _instance.gameObject;
                    _instance = null;
                    Destroy(dying);
                }
                else _instance.RefreshMask();
            }
        }

        internal static void ForgetMaterials() => Materials.Clear();

        /// <summary>Puts a voice on the sound for this owner, or hands one back it already has. Null = someone else drives it.</summary>
        internal static ParelRaycastAudioVoice Attach(ParelRaycastAudio owner, AudioSource source, float radius, bool area)
        {
            if (!Application.isPlaying || source == null || owner == null) return null;
            if (_instance != null && source == _instance._bus) return null;
            if (BySource.TryGetValue(source, out ParelRaycastAudioVoice have) && have != null)
            {
                if (have.Owner == owner) return have;
                // a Single Object on the sound beats a zone round it
                if (!(owner is ParelRaycastAudioObject) || !(have.Owner is ParelRaycastAudioZone)) return null;
                Release(have);
            }

            // one voice per object: a second AudioSource on the same object stays as it is
            ParelRaycastAudioVoice other = source.GetComponent<ParelRaycastAudioVoice>();
            if (other != null && other.Source != source && other.Source != null) return null;

            ParelRaycastAudioVoice voice = other != null ? other : source.gameObject.AddComponent<ParelRaycastAudioVoice>();
            voice.Begin(owner, source, radius, area);
            BySource[source] = voice;
            if (!Voices.Contains(voice)) Voices.Add(voice);
            return voice;
        }

        internal static void Release(ParelRaycastAudioVoice voice)
        {
            Voices.Remove(voice);
            if (voice == null) return;
            if (voice.Source != null) BySource.Remove(voice.Source);
            voice.End();
            Destroy(voice);
        }

        private static void Ensure()
        {
            if (_instance != null) return;
            RefreshPlugin();
            var go = new GameObject("[ParelVR 3D Audio Raycast]") { hideFlags = HideFlags.NotEditable };
            DontDestroyOnLoad(go);
            // the AudioSource first, so the echo tail filter below it plays through it
            AudioSource bus = go.AddComponent<AudioSource>();
            _instance = go.AddComponent<ParelRaycastAudioEngine>();
            _instance._bus = bus;
        }

        private void RefreshMask()
        {
            int mask = 0;
            for (int i = 0; i < Owners.Count; i++)
                if (Owners[i] != null) mask |= Owners[i].occluders.value;
            // players, UI and menus never count as the room, whatever a component lets block its sound
            mask &= ParelRaycastAudio.DefaultOccluderMask;
            _mask = mask != 0 ? mask : ParelRaycastAudio.DefaultOccluderMask;
        }

        private static void RefreshPlugin()
        {
            string plugin = AudioSettings.GetSpatializerPluginName();
            HasSpatializer = !string.IsNullOrEmpty(plugin);
            MetaSpatializer = HasSpatializer && plugin.IndexOf("Meta", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Start()
        {
            _sampleRate = AudioSettings.outputSampleRate;
            StartBus();
        }

        private void OnDestroy()
        {
            _busLive = false;
            _busReady = false;
            if (_instance == this) _instance = null;
        }

        // ------------------------------------------------------------------ frame

        private void LateUpdate()
        {
            if (_instance != this) return;
            for (int i = Voices.Count - 1; i >= 0; i--)
            {
                ParelRaycastAudioVoice v = Voices[i];
                if (v == null || v.Source == null || v.Owner == null) Release(v);
            }

            float now = Time.unscaledTime;
            if (now >= _nextHousekeeping)
            {
                _nextHousekeeping = now + 1f;
                Housekeeping();
            }

            Transform ear = Ear();
            if (ear == null) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            Vector3 earAt = ear.position;
            _rays = 0;
            _searches = Application.isMobilePlatform ? 1 : 3;

            for (int i = 0; i < Owners.Count; i++)
                if (Owners[i] != null) Owners[i].Prepare(earAt, dt);

            if (Zones.Count > 0 && now >= _nextScan)
            {
                _nextScan = now + 1f;
                ScanZones();
            }

            if (now >= _nextListener)
            {
                _nextListener = now + 0.1f;
                ProbeListener(earAt);
            }

            if (now >= _nextSort)
            {
                _nextSort = now + 0.5f;
                ChooseReflectionVoices(earAt);
            }

            int count = Voices.Count;
            int budget = Application.isMobilePlatform ? 260 : 600;
            for (int n = 0; n < count && _rays < budget; n++)
            {
                _roundRobin = (_roundRobin + 1) % count;
                ParelRaycastAudioVoice v = Voices[_roundRobin];
                if (v == null || now < v.NextRays) continue;
                Trace(v, earAt, now);
            }

            Vector3 right = ear.right;
            for (int i = 0; i < count; i++)
            {
                ParelRaycastAudioVoice v = Voices[i];
                if (v == null || v.Source == null) continue;
                v.Distance = Vector3.Distance(earAt, v.Source.transform.position);
                v.Publish(dt, earAt, right, _sampleRate);
            }

            PublishRoom(dt);
        }

        private void Housekeeping()
        {
            RefreshPlugin();
            if (++_housekeepingCount % 5 == 0) Curves.Clear();

            int rate = AudioSettings.outputSampleRate;
            if (rate != _sampleRate)
            {
                _sampleRate = rate;
                for (int i = 0; i < Voices.Count; i++)
                    if (Voices[i] != null) Voices[i].Setup(rate);
                StartBus();
            }

            if (Materials.Count > 4096) Materials.Clear();
            if (Skipped.Count > 4096) Skipped.Clear();

            // the echo tail plays on the same mixer channel as the world's sounds (so World Volume turns it down too)
            if (_bus != null)
            {
                for (int i = 0; i < Voices.Count; i++)
                {
                    ParelRaycastAudioVoice v = Voices[i];
                    if (v == null || v.Source == null || v.Source.outputAudioMixerGroup == null) continue;
                    if (_bus.outputAudioMixerGroup != v.Source.outputAudioMixerGroup)
                        _bus.outputAudioMixerGroup = v.Source.outputAudioMixerGroup;
                    break;
                }

                if (_busLive && !_bus.isPlaying) _bus.Play();
            }
        }

        // ------------------------------------------------------------------ zones

        private void ScanZones()
        {
#if UNITY_6000_4_OR_NEWER
            AudioSource[] all = FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude);
#else
            AudioSource[] all = FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#endif
            float now = Time.unscaledTime;
            for (int i = 0; i < all.Length; i++)
            {
                AudioSource s = all[i];
                if (s == null || s == _bus) continue;
                BySource.TryGetValue(s, out ParelRaycastAudioVoice v);
                if (v != null && (v.IsArea || !(v.Owner is ParelRaycastAudioZone))) continue;

                ParelRaycastAudioZone holder = v != null ? (ParelRaycastAudioZone)v.Owner : null;
                Vector3 p = s.transform.position;
                ParelRaycastAudioZone best = null;
                // a sound stays with its zone until it is well out of it, so it doesn't flicker on the edge
                if (holder != null && holder.isActiveAndEnabled && holder.takeEverySound && holder.Contains(p, 0.5f)) best = holder;
                else
                    for (int z = 0; z < Zones.Count; z++)
                    {
                        ParelRaycastAudioZone zone = Zones[z];
                        if (zone == null || !zone.isActiveAndEnabled || !zone.takeEverySound || !zone.Contains(p, 0f)) continue;
                        best = zone;
                        break;
                    }

                if (best != null && v == null)
                {
                    if (Skipped.TryGetValue(s, out float until) && now < until) continue;
                    if (!best.Accepts(s))
                    {
                        Skipped[s] = now + 5f;
                        continue;
                    }
                }

                if (v != null && v.Owner != best)
                {
                    Release(v);
                    v = null;
                }

                if (best != null && v == null) Attach(best, s, best.SizeFor(s), false);
            }
        }

        // ------------------------------------------------------------------ listener

        private void ProbeListener(Vector3 ear)
        {
            Quaternion turn = Random.rotationUniform;
            int hits = 0;
            float rSum = 0f, aL = 0f, aM = 0f, aH = 0f;
            _candidateCount = 0;
            _probeO = ear;
            for (int i = 0; i < ProbeCount; i++)
            {
                Vector3 dir = turn * Fibonacci[i];
                float free = ProbeReach;
                _rays++;
                if (Physics.Raycast(ear, dir, out RaycastHit hit, ProbeReach, _mask, QueryTriggerInteraction.Ignore))
                {
                    free = hit.distance;
                    hits++;
                    rSum += Mathf.Max(hit.distance, 0.3f);
                    ParelAcoustic m = MaterialOf(hit.collider);
                    aL += m.aL;
                    aM += m.aM;
                    aH += m.aH;
                }

                _probeD[i] = dir;
                _probeL[i] = free;

                // points along the probe the way round is looked for at
                float last = -1f;
                AddCandidate(ear, dir, i, Mathf.Min(1.2f, free - 0.3f), ref last);
                AddCandidate(ear, dir, i, Mathf.Min(3f, free - 0.3f), ref last);
                AddCandidate(ear, dir, i, free * 0.5f, ref last);
                AddCandidate(ear, dir, i, free - 0.35f, ref last);
            }

            for (int k = 0; k < 6; k++)
            {
                _rays++;
                _planeOk[k] = Physics.Raycast(ear, Axes[k], out RaycastHit hit, PlaneReach, _mask, QueryTriggerInteraction.Ignore);
                if (!_planeOk[k]) continue;
                _planeP[k] = hit.point;
                _planeN[k] = hit.normal;
                _planeA[k] = MaterialOf(hit.collider).aM;
            }

            float open = 1f - hits / (float)ProbeCount;
            float meanFree = _free;
            if (hits > 0)
            {
                meanFree = rSum / hits;
                aL /= hits;
                aM /= hits;
                aH /= hits;
            }
            else
            {
                aL = _aL;
                aM = _aM;
                aH = _aH;
            }

            float k2 = _roomSeen ? 0.15f : 1f;
            _roomSeen = true;
            _open += (open - _open) * k2;
            _free += (meanFree - _free) * k2;
            _aL += (aL - _aL) * k2;
            _aM += (aM - _aM) * k2;
            _aH += (aH - _aH) * k2;
        }

        private void AddCandidate(Vector3 ear, Vector3 dir, int probe, float t, ref float last)
        {
            if (t < 0.6f || _candidateCount >= MaxCandidates || (last >= 0f && t - last < 0.6f)) return;
            last = t;
            _candidates[_candidateCount] = ear + dir * t;
            _candidateProbe[_candidateCount] = probe;
            _candidateCount++;
        }

        // Sabine: RT60 = 0.161 V / (S a + 4 m V). The probes' mean distance to the walls stands in for the room's mean
        // free path (4 V / S), and the sky counts as a surface that soaks up everything.
        private void PublishRoom(float dt)
        {
            float closed = Mathf.Clamp01(1f - _open);
            float mfp = Mathf.Max(_free, 0.5f);
            float lowA = closed * _aL + _open, highA = closed * _aH + _open, midA = closed * _aM + _open;
            float rtLow = 0.161f / (4f * Mathf.Lerp(lowA, midA, 0.5f) / mfp + 4f * AirSabine[1]);
            float rtHigh = 0.161f / (4f * highA / mfp + 4f * AirSabine[2]);
            float shut = Mathf.Pow(closed, 1.5f);
            rtLow = Mathf.Clamp(rtLow * shut, 0.1f, 8f);
            rtHigh = Mathf.Clamp(Mathf.Min(rtHigh * shut, rtLow), 0.08f, 8f);

            float k = 1f - Mathf.Exp(-dt / 0.5f);
            _rtLow += (rtLow - _rtLow) * k;
            _rtHigh += (rtHigh - _rtHigh) * k;
            float wet = closed * closed * closed * Mathf.Clamp(Mathf.Sqrt(_rtLow / 0.8f), 0.6f, 1.5f);
            _wet += (wet - _wet) * k;
        }

        private void ChooseReflectionVoices(Vector3 ear)
        {
            int allowed = Application.isMobilePlatform ? 8 : 20;
            // the nearest playing sounds get real reflections; the rest still get walls, paths and the echo tail
            for (int i = 0; i < Voices.Count; i++)
            {
                ParelRaycastAudioVoice v = Voices[i];
                if (v == null) continue;
                v.ReflectionsAllowed = false;
            }

            for (int pick = 0; pick < allowed; pick++)
            {
                ParelRaycastAudioVoice best = null;
                float bestD = 40f;
                for (int i = 0; i < Voices.Count; i++)
                {
                    ParelRaycastAudioVoice v = Voices[i];
                    if (v == null || v.ReflectionsAllowed || v.Source == null || !v.Source.isPlaying) continue;
                    float d = Vector3.Distance(ear, v.Source.transform.position);
                    if (d >= bestD) continue;
                    bestD = d;
                    best = v;
                }

                if (best == null) break;
                best.ReflectionsAllowed = true;
            }
        }

        // ------------------------------------------------------------------ one sound

        private void Trace(ParelRaycastAudioVoice v, Vector3 ear, float now)
        {
            AudioSource s = v.Source;
            ParelRaycastAudio owner = v.Owner;
            Vector3 at = s.transform.position;
            float d = Vector3.Distance(ear, at);
            bool playing = s.isPlaying;
            v.NextRays = now + (!playing ? 0.3f : d < 12f ? 0.07f : d < 30f ? 0.14f : 0.35f) + Random.value * 0.02f;
            if (!playing) return;
            int mask = owner.occluders.value;

            // 1. straight through: a spread of rays over the sound's size, each stepping through every surface
            float tL = 1f, tM = 1f, tH = 1f;
            int n = owner.muffleThroughWalls ? Mathf.Min(owner.RayTargets(v, ear, _targets), _targets.Length) : 0;
            if (n > 0)
            {
                float eL = 0f, eM = 0f, eH = 0f, sum = 0f;
                for (int i = 0; i < n; i++)
                {
                    float w = i == 0 ? 2f : 1f;
                    Transmit(ear, _targets[i], v, owner, mask, out float a, out float b, out float c);
                    eL += w * a * a;
                    eM += w * b * b;
                    eH += w * c * c;
                    sum += w;
                }

                tL = Mathf.Sqrt(eL / sum);
                tM = Mathf.Sqrt(eM / sum);
                tH = Mathf.Sqrt(eH / sum);
            }

            v.RawDirect[0] = tL * Air(owner, 0, d);
            v.RawDirect[1] = tM * Air(owner, 1, d);
            v.RawDirect[2] = tH * Air(owner, 2, d);

            // 2. the way round
            v.HasPath = false;
            if (n > 0 && owner.aroundCorners && tM < 0.8f && d > 0.6f && FindPath(v, ear, at, d, mask, out float length, out float bend))
            {
                float roll = Mathf.Max(Rolloff(s, d), 0.0001f);
                float spread = Mathf.Clamp01(Rolloff(s, length) / roll);
                float extra = length - d;
                for (int b = 0; b < 3; b++)
                {
                    float g = spread * Mathf.Pow(10f, -bend / 90f * BendDb[b] / 20f) * Air(owner, b, length);
                    if (extra < 0.6f) v.RawDirect[b] = Mathf.Min(1f, Mathf.Sqrt(v.RawDirect[b] * v.RawDirect[b] + g * g));
                    else v.RawPath[b] = g;
                }

                if (extra >= 0.6f)
                {
                    v.HasPath = true;
                    v.PathExtra = extra;
                    v.PathDir = (v.PathPoint - ear).normalized;
                }
            }

            // 3. first reflections
            if (!owner.roomEcho || !v.ReflectionsAllowed) v.ReflectionCount = 0;
            else if (now >= v.NextReflections)
            {
                v.NextReflections = now + (d < 15f ? 0.2f : 0.4f);
                Reflections(v, ear, at, d, owner, mask);
            }
        }

        private void Transmit(Vector3 ear, Vector3 target, ParelRaycastAudioVoice v, ParelRaycastAudio owner, int mask,
            out float tL, out float tM, out float tH)
        {
            tL = tM = tH = 1f;
            Vector3 dir = target - ear;
            float len = dir.magnitude;
            if (len < 0.1f) return;
            dir /= len;
            // a hit right at the sound is its own body
            float remaining = len - Mathf.Min(0.08f + v.Radius * 0.25f, len * 0.5f);
            Vector3 from = ear;
            float ws = owner.wallStrength;
            for (int k = 0; k < 6 && remaining > 0.01f; k++)
            {
                _rays++;
                if (!Physics.Raycast(from, dir, out RaycastHit hit, remaining, mask, QueryTriggerInteraction.Ignore)) break;
                if (!v.IsOwn(hit.collider))
                {
                    ParelAcoustic m = MaterialOf(hit.collider);
                    tL *= Mathf.Pow(m.tL, ws);
                    tM *= Mathf.Pow(m.tM, ws);
                    tH *= Mathf.Pow(m.tH, ws);
                    if (tL < 0.0005f && tM < 0.0005f) break;
                }

                float step = hit.distance + 0.02f;
                from += dir * step;
                remaining -= step;
            }
        }

        // Nothing but the sound's own body between a and b (b near the sound: stop short of its body).
        private bool Clear(Vector3 a, Vector3 b, ParelRaycastAudioVoice v, int mask, bool toSound)
        {
            Vector3 dir = b - a;
            float len = dir.magnitude;
            if (len < 0.05f) return true;
            dir /= len;
            float remaining = toSound ? len - Mathf.Min(0.08f + v.Radius * 0.25f, len * 0.5f) : len;
            Vector3 from = a;
            for (int k = 0; k < 3 && remaining > 0.01f; k++)
            {
                _rays++;
                if (!Physics.Raycast(from, dir, out RaycastHit hit, remaining, mask, QueryTriggerInteraction.Ignore)) return true;
                if (!v.IsOwn(hit.collider)) return false;
                float step = hit.distance + 0.02f;
                from += dir * step;
                remaining -= step;
            }

            return true;
        }

        private bool FindPath(ParelRaycastAudioVoice v, Vector3 ear, Vector3 at, float d, int mask, out float length, out float bend)
        {
            length = float.MaxValue;
            bend = 0f;
            int kind = 0;
            Vector3 c1 = at, c2 = at;

            // the last way round, if it still works
            if (v.LastKind == 1 && Clear(ear, v.LastC, v, mask, false) && Clear(v.LastC, at, v, mask, true))
            {
                kind = 1;
                c1 = v.LastC;
                length = Vector3.Distance(ear, c1) + Vector3.Distance(c1, at);
            }
            else if (v.LastKind == 2 && Clear(ear, v.LastC, v, mask, false) && Clear(v.LastC, v.LastQ, v, mask, false) &&
                     Clear(v.LastQ, at, v, mask, true))
            {
                kind = 2;
                c1 = v.LastC;
                c2 = v.LastQ;
                length = Vector3.Distance(ear, c1) + Vector3.Distance(c1, c2) + Vector3.Distance(c2, at);
            }

            // a fresh search (a few sounds per frame) when there is no way round yet, or the one there is looks long
            if ((kind == 0 || length > d * 1.15f) && _searches > 0)
            {
                _searches--;
                Search(v, ear, at, d, mask, ref kind, ref length, ref c1, ref c2);
            }

            if (kind == 0)
            {
                v.LastKind = 0;
                return false;
            }

            v.LastKind = kind;
            v.LastC = c1;
            v.LastQ = c2;
            v.PathPoint = c1;
            v.PathPoint2 = kind == 2 ? c2 : at;
            v.PathBends = kind;
            bend = Vector3.Angle(c1 - ear, v.PathPoint2 - c1);
            if (kind == 2) bend += Vector3.Angle(c2 - c1, at - c2);
            return true;
        }

        private void Search(ParelRaycastAudioVoice v, Vector3 ear, Vector3 at, float d, int mask, ref int kind, ref float length,
            ref Vector3 c1, ref Vector3 c2)
        {
            Vector3 toSound = (at - ear) / Mathf.Max(d, 0.001f);
            int set = v.ProbeSet = 1 - v.ProbeSet;

            // fresh rays: from the listener into a cone towards the sound, and from the sound, half all round and half
            // towards the listener (the last set of each is kept, so two searches worth of rays meet)
            for (int i = 0; i < AimedProbes; i++)
            {
                int k = set * AimedProbes + i;
                Vector3 dir = Cone(toSound, 55f);
                v.AimO[k] = ear;
                v.AimD[k] = dir;
                _rays++;
                v.AimL[k] = Physics.Raycast(ear, dir, out RaycastHit hit, ProbeReach, mask, QueryTriggerInteraction.Ignore) ? hit.distance : ProbeReach;
            }

            for (int i = 0; i < SourceProbes; i++)
            {
                int k = set * SourceProbes + i;
                Vector3 dir = i < SourceProbes / 2 ? Random.onUnitSphere : Cone(-toSound, 55f);
                v.SrcO[k] = at;
                v.SrcD[k] = dir;
                v.SrcL[k] = Free(at, dir, ProbeReach * 0.7f, v, mask);
            }

            // 1. one bend where a ray from the listener and a ray from the sound (nearly) meet: the opening between them
            int nm = 0;
            int listenerRays = ProbeCount + 2 * AimedProbes;
            for (int a = 0; a < listenerRays; a++)
            {
                Vector3 o1, d1;
                float l1;
                if (a < ProbeCount)
                {
                    o1 = _probeO;
                    d1 = _probeD[a];
                    l1 = _probeL[a];
                }
                else
                {
                    int k = a - ProbeCount;
                    o1 = v.AimO[k];
                    d1 = v.AimD[k];
                    l1 = v.AimL[k];
                }

                if (l1 < 0.8f) continue;
                for (int b = 0; b < 2 * SourceProbes; b++)
                {
                    float l2 = v.SrcL[b];
                    if (l2 < 0.8f || !Meet(o1, d1, l1, v.SrcO[b], v.SrcD[b], l2, out Vector3 m)) continue;
                    float cost = Vector3.Distance(ear, m) + Vector3.Distance(m, at);
                    if (cost < length) nm = Insert(m, cost, nm);
                }
            }

            for (int i = 0; i < nm; i++)
            {
                if (_meetCost[i] >= length) break;
                Vector3 m = _meet[i];
                if (!Clear(ear, m, v, mask, false) || !Clear(m, at, v, mask, true)) continue;
                m = Pull(ear, m, at, v, mask);
                float l = Vector3.Distance(ear, m) + Vector3.Distance(m, at);
                if (l < length)
                {
                    kind = 1;
                    length = l;
                    c1 = m;
                }

                break;
            }

            // 2. one bend at a point along a listener probe the sound can see (round a pillar, past a doorframe)
            int nc = PickBest(_candidates, _candidateCount, ear, at, _bestC, _bestCCost);
            for (int i = 0; i < nc && i < 3; i++)
            {
                if (_bestCCost[i] >= length) break;
                Vector3 c = _candidates[_bestC[i]];
                if (!Clear(c, at, v, mask, true)) continue;
                c = Pull(ear, c, at, v, mask);
                float l = Vector3.Distance(ear, c) + Vector3.Distance(c, at);
                if (l < length)
                {
                    kind = 1;
                    length = l;
                    c1 = c;
                }

                break;
            }

            // 3. two bends (round two corners, along a hall): the far ends of long listener rays and long sound rays
            // that see each other
            if (kind != 0 && length <= d * 1.6f) return;
            int nl = 0, ns = 0;
            for (int i = 0; i < LongEnds; i++) _longLLen[i] = _longSLen[i] = 0f;
            for (int a = 0; a < listenerRays; a++)
            {
                bool shared = a < ProbeCount;
                float l1 = shared ? _probeL[a] : v.AimL[a - ProbeCount];
                if (l1 < 2.5f) continue;
                Vector3 end = shared ? _probeO + _probeD[a] * (l1 - 0.35f) : v.AimO[a - ProbeCount] + v.AimD[a - ProbeCount] * (l1 - 0.35f);
                nl = Longest(_longL, _longLLen, nl, end, l1);
            }

            for (int b = 0; b < 2 * SourceProbes; b++)
            {
                float l2 = v.SrcL[b];
                if (l2 < 2.5f) continue;
                ns = Longest(_longS, _longSLen, ns, v.SrcO[b] + v.SrcD[b] * (l2 - 0.35f), l2);
            }

            _tried = 0;
            for (int test = 0; test < 5; test++)
            {
                float best = length;
                int bi = -1, bj = -1;
                for (int i = 0; i < nl; i++)
                {
                    float lc = Vector3.Distance(ear, _longL[i]);
                    for (int j = 0; j < ns; j++)
                    {
                        if ((_tried & (1UL << (i * LongEnds + j))) != 0) continue;
                        float cost = lc + Vector3.Distance(_longL[i], _longS[j]) + Vector3.Distance(_longS[j], at);
                        if (cost >= best) continue;
                        best = cost;
                        bi = i;
                        bj = j;
                    }
                }

                if (bi < 0) break;
                _tried |= 1UL << (bi * LongEnds + bj);
                Vector3 pc = _longL[bi], pq = _longS[bj];
                // the rays may be from a moment ago: check every leg now
                if (!Clear(pc, pq, v, mask, false) || !Clear(ear, pc, v, mask, false) || !Clear(pq, at, v, mask, true)) continue;
                kind = 2;
                length = best;
                c1 = pc;
                c2 = pq;
                break;
            }
        }

        private const int AimedProbes = 12;
        private const int SourceProbes = 24;
        private const int LongEnds = 6;
        private readonly Vector3[] _probeD = new Vector3[ProbeCount];
        private readonly float[] _probeL = new float[ProbeCount];
        private Vector3 _probeO;
        private readonly Vector3[] _meet = new Vector3[4];
        private readonly float[] _meetCost = new float[4];
        private readonly Vector3[] _longL = new Vector3[LongEnds], _longS = new Vector3[LongEnds];
        private readonly float[] _longLLen = new float[LongEnds], _longSLen = new float[LongEnds];
        private int _searches;
        private ulong _tried;

        // The nearest points of two rays; where they pass within a hand width of each other is the opening between them.
        private static bool Meet(Vector3 o1, Vector3 d1, float l1, Vector3 o2, Vector3 d2, float l2, out Vector3 m)
        {
            Vector3 w = o1 - o2;
            float b = Vector3.Dot(d1, d2), dd = Vector3.Dot(d1, w), e = Vector3.Dot(d2, w);
            float denom = 1f - b * b;
            float s = denom > 0.0001f ? (b * e - dd) / denom : 0f;
            s = Mathf.Clamp(s, 0f, l1);
            float t = Mathf.Clamp(e + s * b, 0f, l2);
            s = Mathf.Clamp(t * b - dd, 0f, l1);
            Vector3 p = o1 + d1 * s, q = o2 + d2 * t;
            m = (p + q) * 0.5f;
            return s > 0.3f && t > 0.3f && s < l1 - 0.1f && t < l2 - 0.1f && (p - q).sqrMagnitude < 0.36f;
        }

        private int Insert(Vector3 m, float cost, int n)
        {
            int k = _meet.Length;
            if (n == k && cost >= _meetCost[k - 1]) return n;
            int slot = n < k ? n++ : k - 1;
            while (slot > 0 && _meetCost[slot - 1] > cost)
            {
                _meet[slot] = _meet[slot - 1];
                _meetCost[slot] = _meetCost[slot - 1];
                slot--;
            }

            _meet[slot] = m;
            _meetCost[slot] = cost;
            return n;
        }

        private static int Longest(Vector3[] ends, float[] lengths, int n, Vector3 end, float length)
        {
            int k = ends.Length;
            if (n == k && length <= lengths[k - 1]) return n;
            int slot = n < k ? n++ : k - 1;
            while (slot > 0 && lengths[slot - 1] < length)
            {
                ends[slot] = ends[slot - 1];
                lengths[slot] = lengths[slot - 1];
                slot--;
            }

            ends[slot] = end;
            lengths[slot] = length;
            return n;
        }

        // Pulls a bend point towards the straight line from the listener to the sound for as long as both legs stay
        // open: it ends up at the corner (the door edge, the wall end) the sound really bends round.
        private Vector3 Pull(Vector3 ear, Vector3 c, Vector3 at, ParelRaycastAudioVoice v, int mask)
        {
            Vector3 line = at - ear;
            float t = Mathf.Clamp01(Vector3.Dot(c - ear, line) / Mathf.Max(line.sqrMagnitude, 0.0001f));
            Vector3 toLine = ear + line * t - c;
            if (toLine.sqrMagnitude < 0.01f) return c;
            float lo = 0f, hi = 1f;
            for (int step = 0; step < 4; step++)
            {
                float mid = (lo + hi) * 0.5f;
                Vector3 p = c + toLine * mid;
                if (Clear(ear, p, v, mask, false) && Clear(p, at, v, mask, true)) lo = mid;
                else hi = mid;
            }

            // a hand width back out from the corner, so the next check doesn't graze it
            return c + toLine * Mathf.Max(0f, lo - 0.1f / Mathf.Max(toLine.magnitude, 0.1f));
        }

        // How far a ray from the sound gets before something that is not the sound itself.
        private float Free(Vector3 from, Vector3 dir, float reach, ParelRaycastAudioVoice v, int mask)
        {
            Vector3 p = from;
            float travelled = 0f;
            for (int k = 0; k < 3; k++)
            {
                _rays++;
                if (!Physics.Raycast(p, dir, out RaycastHit hit, reach - travelled, mask, QueryTriggerInteraction.Ignore)) return reach;
                travelled += hit.distance;
                if (!v.IsOwn(hit.collider)) return travelled;
                travelled += 0.02f;
                p = from + dir * travelled;
            }

            return travelled;
        }

        private static Vector3 Cone(Vector3 axis, float degrees)
        {
            float z = Random.Range(Mathf.Cos(degrees * Mathf.Deg2Rad), 1f);
            float phi = Random.value * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return Quaternion.FromToRotation(Vector3.forward, axis) * new Vector3(r * Mathf.Cos(phi), r * Mathf.Sin(phi), z);
        }

        // The few points with the shortest ear -> point -> sound, cheapest first.
        private static int PickBest(Vector3[] points, int count, Vector3 ear, Vector3 at, int[] best, float[] cost)
        {
            int k = best.Length, have = 0;
            for (int i = 0; i < k; i++) best[i] = -1;
            for (int i = 0; i < count; i++)
            {
                float c = Vector3.Distance(ear, points[i]) + Vector3.Distance(points[i], at);
                if (have == k && c >= cost[k - 1]) continue;
                int slot = have < k ? have++ : k - 1;
                while (slot > 0 && cost[slot - 1] > c)
                {
                    cost[slot] = cost[slot - 1];
                    best[slot] = best[slot - 1];
                    slot--;
                }

                cost[slot] = c;
                best[slot] = i;
            }

            return have;
        }

        // The sound mirrored in each wall, floor and ceiling found round the listener (first-order image sources).
        private void Reflections(ParelRaycastAudioVoice v, Vector3 ear, Vector3 at, float d, ParelRaycastAudio owner, int mask)
        {
            int n = 0;
            float roll = Mathf.Max(Rolloff(v.Source, d), 0.0001f);
            for (int k = 0; k < 6 && n < ParelRaycastAudioVoice.MaxReflections; k++)
            {
                if (!_planeOk[k]) continue;
                Vector3 p = _planeP[k], nrm = _planeN[k];
                float dl = Vector3.Dot(ear - p, nrm);
                float ds = Vector3.Dot(at - p, nrm);
                if (dl < 0.05f || ds < 0.05f) continue;
                Vector3 image = at - 2f * ds * nrm;
                float il = Vector3.Distance(image, ear);
                float extra = il - d;
                if (extra < 0.25f) continue;
                Vector3 bounce = ear + (image - ear) * (dl / (dl + ds));
                Vector3 off = bounce + nrm * 0.05f;
                if (owner.muffleThroughWalls)
                {
                    // both legs open, and a real surface where it bounces (not just the plane carried on into the air)
                    if (!Clear(ear, off, v, mask, false) || !Clear(at, off, v, mask, false)) continue;
                    _rays++;
                    if (!Physics.Raycast(off, -nrm, 0.15f, mask, QueryTriggerInteraction.Ignore)) continue;
                }

                float g = Mathf.Sqrt(1f - _planeA[k]) * Mathf.Clamp01(Rolloff(v.Source, il) / roll) * 0.8f * owner.echoAmount;
                g *= Air(owner, 1, il);
                if (g < 0.003f) continue;
                v.ReflectionExtra[n] = extra;
                v.ReflectionGain[n] = g;
                v.ReflectionDir[n] = (image - ear) / il;
                n++;
            }

            v.ReflectionCount = n;
        }

        private static float Air(ParelRaycastAudio owner, int band, float distance) =>
            owner.airAbsorption ? Mathf.Pow(10f, -AirDbPerMeter[band] * distance / 20f) : 1f;

        /// <summary>How loud the AudioSource's own distance fade makes it at <paramref name="d"/> meters.</summary>
        internal static float Rolloff(AudioSource s, float d)
        {
            float min = Mathf.Max(0.01f, s.minDistance), max = Mathf.Max(min + 0.01f, s.maxDistance);
            switch (s.rolloffMode)
            {
                case AudioRolloffMode.Linear:
                    return Mathf.Clamp01(1f - (d - min) / (max - min));
                case AudioRolloffMode.Custom:
                    // GetCustomCurve makes a copy each call: keep it a few seconds
                    if (!Curves.TryGetValue(s, out AnimationCurve curve))
                        Curves[s] = curve = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    return curve != null && curve.length > 0 ? Mathf.Clamp01(curve.Evaluate(d / max)) : 1f;
                default:
                    return min / Mathf.Clamp(d, min, max);
            }
        }

        internal static ParelAcoustic MaterialOf(Collider c)
        {
            if (c == null) return ParelAcoustic.Generic;
            if (Materials.TryGetValue(c, out ParelAcoustic m)) return m;
            ParelRaycastAudioMaterial set = c.GetComponentInParent<ParelRaycastAudioMaterial>();
            if (set != null) m = set.Values;
            else if (!(c.sharedMaterial != null && ParelAcoustic.Guess(c.sharedMaterial.name, out m)))
            {
                Renderer r = c.GetComponent<Renderer>();
                if (!(r != null && r.sharedMaterial != null && ParelAcoustic.Guess(r.sharedMaterial.name, out m)) &&
                    !ParelAcoustic.Guess(c.gameObject.name, out m))
                    m = ParelAcoustic.Generic;
            }

            Materials[c] = m;
            return m;
        }

        /// <summary>The listener: the local player's ears.</summary>
        internal static Transform Ear()
        {
            if ((_listener == null || !_listener.isActiveAndEnabled) && Time.unscaledTime >= _nextListenerLook)
            {
                _nextListenerLook = Time.unscaledTime + 0.5f;
                _listener = FindAnyObjectByType<AudioListener>();
            }

            if (_listener != null && _listener.isActiveAndEnabled) return _listener.transform;
            Camera main = Camera.main;
            return main != null ? main.transform : null;
        }

        private static Vector3[] MakeFibonacci(int n)
        {
            var dirs = new Vector3[n];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++)
            {
                float y = 1f - (i + 0.5f) / n * 2f;
                float r = Mathf.Sqrt(1f - y * y);
                float a = golden * i;
                dirs[i] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            }

            return dirs;
        }

        // ------------------------------------------------------------------ echo tail

        private void StartBus()
        {
            if (_bus == null) return;
            int fs = AudioSettings.outputSampleRate;
            if (fs <= 0) return;
            _busReady = false;
            lock (_busSync) AllocateBus(fs);

            if (_bus.clip != null) Destroy(_bus.clip);
            _bus.clip = AudioClip.Create("ParelVR 3D Audio Room", fs, 2, fs, false);
            _bus.loop = true;
            _bus.playOnAwake = false;
            _bus.spatialBlend = 0f;
            _bus.spatialize = false;
            _bus.priority = 0;
            _bus.dopplerLevel = 0f;
            _bus.bypassReverbZones = true;
            _bus.volume = 1f;
            _busReady = true;
            _bus.Play();
            _busLive = true;
        }

        private void AllocateBus(int fs)
        {
            _busFs = fs;
            float scale = fs / 48000f;
            int ring = Mathf.NextPowerOfTwo(Mathf.Max(16384, fs / 2));
            _busIn = new float[8192];
            _lines = new float[8][];
            _lineLen = new int[8];
            _linePos = new int[8];
            for (int i = 0; i < 8; i++)
            {
                _lineLen[i] = Mathf.Max(64, Mathf.RoundToInt(LineBase[i] * scale));
                _lines[i] = new float[_lineLen[i]];
                _lineLp[i] = 0f;
            }

            _diff = new float[2][];
            _diffPos = new int[2];
            for (int i = 0; i < 2; i++) _diff[i] = new float[Mathf.Max(16, Mathf.RoundToInt(DiffuserBase[i] * scale))];
            lock (RingLock)
            {
                _ring = new float[ring];
                _ringMask = ring - 1;
                _busRead = 0;
            }
        }

        /// <summary>A voice adds its share for the echo tail (audio thread). Order between the voices and the tail
        /// doesn't matter: each voice keeps its own place in the ring, at most two blocks ahead of the tail.</summary>
        internal static void AddSend(ParelRaycastAudioVoice v, float[] buffer, int frames)
        {
            ParelRaycastAudioEngine e = _instance;
            if (e == null || !e._busReady || e._ring == null) return;
            lock (RingLock)
            {
                long read = e._busRead;
                if (!v.HasCursor || v.Cursor < read)
                {
                    v.Cursor = read;
                    v.HasCursor = true;
                }
                else if (v.Cursor > read + 2L * frames) return; // the tail stopped: don't pile up

                float[] ring = e._ring;
                int mask = e._ringMask;
                long c = v.Cursor;
                for (int f = 0; f < frames; f++) ring[(int)((c + f) & mask)] += buffer[f];
                v.Cursor = c + frames;
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_busReady || channels <= 0) return;
            lock (_busSync) Tail(data, channels);
        }

        private void Tail(float[] data, int channels)
        {
            int frames = data.Length / channels;
            if (frames > _busIn.Length) return;
            float[] input = _busIn;
            lock (RingLock)
            {
                long read = _busRead;
                float[] ring = _ring;
                int mask = _ringMask;
                for (int f = 0; f < frames; f++)
                {
                    int i = (int)((read + f) & mask);
                    input[f] = ring[i];
                    ring[i] = 0f;
                }

                _busRead = read + frames;
            }

            // decay per line from the room's reverberation time; the highs die sooner (one-pole in each loop)
            float rtLow = Mathf.Max(0.08f, _rtLow), rtHigh = Mathf.Max(0.06f, Mathf.Min(_rtHigh, rtLow));
            float fs = _busFs, gAvg = 0f;
            for (int i = 0; i < 8; i++)
            {
                float g = Mathf.Pow(10f, -3f * _lineLen[i] / (rtLow * fs));
                float gh = Mathf.Pow(10f, -3f * _lineLen[i] / (rtHigh * fs));
                float r = Mathf.Clamp(gh / g, 0.05f, 1f);
                _lineG[i] = g;
                _lineA[i] = (1f - r) / (1f + r);
                gAvg += g;
            }

            gAvg /= 8f;
            float inGain = Mathf.Sqrt(Mathf.Max(0.02f, 1f - gAvg * gAvg)) * 0.7f;
            float wetFrom = _wetNow, wetTo = _wet, wetStep = (wetTo - wetFrom) / frames;
            float wet = wetFrom;
            float[] d0 = _diff[0], d1 = _diff[1];
            int p0 = _diffPos[0], p1 = _diffPos[1];
            float[] u = _u;

            for (int f = 0; f < frames; f++)
            {
                wet += wetStep;
                float x = input[f] * inGain + 1e-20f;

                // two diffusers smear the onset into a wash
                float o = d0[p0];
                float w = x + 0.6f * o;
                d0[p0] = w;
                x = o - 0.6f * w;
                if (++p0 >= d0.Length) p0 = 0;
                o = d1[p1];
                w = x + 0.6f * o;
                d1[p1] = w;
                x = o - 0.6f * w;
                if (++p1 >= d1.Length) p1 = 0;

                float sum = 0f;
                for (int i = 0; i < 8; i++)
                {
                    float y = _lines[i][_linePos[i]];
                    float lp = _lineLp[i] = y + _lineA[i] * (_lineLp[i] - y);
                    u[i] = _lineG[i] * lp;
                    sum += u[i];
                }

                // Householder mix: every line feeds every other, energy kept
                float h = sum * 0.25f;
                for (int i = 0; i < 8; i++)
                {
                    _lines[i][_linePos[i]] = u[i] - h + ((i & 1) == 0 ? x : -x);
                    if (++_linePos[i] >= _lineLen[i]) _linePos[i] = 0;
                }

                float left = (u[0] - u[2] + u[4] - u[6]) * 0.5f * wet;
                float right = (u[1] - u[3] + u[5] - u[7]) * 0.5f * wet;
                int at = f * channels;
                if (channels >= 2)
                {
                    data[at] = left;
                    data[at + 1] = right;
                    for (int c = 2; c < channels; c++) data[at + c] = 0f;
                }
                else data[at] = 0.5f * (left + right);
            }

            _wetNow = wetTo;
            _diffPos[0] = p0;
            _diffPos[1] = p1;
            for (int i = 0; i < 8; i++)
                if (_lineLp[i] < 1e-15f && _lineLp[i] > -1e-15f) _lineLp[i] = 0f;
        }
    }
}
