using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Added at run time to every AudioSource a 3D Audio Raycast component drives (never saved, hidden in the
    /// Inspector). The engine works out what the sound should go through on the main thread; this plays it on the
    /// audio thread:
    /// <list type="bullet">
    /// <item>the sound is split into low, middle and high bands that always add back up to the original, and each band
    /// gets the share that gets through the walls on the way (so muffling is a real wall's colour, not one filter);</item>
    /// <item>the way round (a doorway, a window) is added as its own copy: from its direction (level and time
    /// difference between the ears), delayed by the extra distance, with its own band colour;</item>
    /// <item>up to six real first reflections (image sources off the walls, floor and ceiling round the listener),
    /// each from its own direction at its own delay;</item>
    /// <item>a share goes to the shared room echo tail.</item>
    /// </list>
    /// Every gain and delay glides sample by sample, so nothing clicks or zips as you move.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class ParelRaycastAudioVoice : MonoBehaviour
    {
        internal const int MaxReflections = 6;
        private const int MaxChannels = 8;
        private const int Block = 32;
        private const int Taps = MaxReflections + 1;

        // ---- main thread ----
        internal ParelRaycastAudio Owner;
        internal AudioSource Source;
        internal float Radius;
        internal bool IsArea;
        internal float Occlusion;
        internal float NextRays, NextReflections;
        internal bool ReflectionsAllowed;
        internal float Distance;

        // what the last rays found (amplitude per band, before smoothing)
        internal readonly float[] RawDirect = { 1f, 1f, 1f };
        internal readonly float[] RawPath = new float[3];
        internal bool HasPath;
        internal float PathExtra;
        internal Vector3 PathDir, PathPoint, PathPoint2;
        internal int PathBends;
        internal int ReflectionCount;
        internal readonly float[] ReflectionExtra = new float[MaxReflections];
        internal readonly float[] ReflectionGain = new float[MaxReflections];
        internal readonly Vector3[] ReflectionDir = new Vector3[MaxReflections];

        // path search memory: the last way round is tried first
        internal int LastKind;
        internal Vector3 LastC, LastQ;
        internal readonly Vector3[] AimO = new Vector3[24], AimD = new Vector3[24], SrcO = new Vector3[48], SrcD = new Vector3[48];
        internal readonly float[] AimL = new float[24], SrcL = new float[48];
        internal int ProbeSet;

        // smoothed, in dB
        private readonly float[] _dbDirect = new float[3];
        private readonly float[] _dbPath = { -80f, -80f, -80f };

        private float _savedBlend;
        private bool _savedSpatialize, _savedPost, _saved;
        private Collider[] _ownColliders = new Collider[0];
        private float _nextOwnLook;

        // ---- main thread -> audio thread ----
        private float _tDL = 1f, _tDM = 1f, _tDH = 1f, _tPL, _tPM, _tPH, _tSL, _tSM, _tSH;
        private float _pathDelay, _pathPan, _pathGain;
        private readonly float[] _erDelay = new float[MaxReflections];
        private readonly float[] _erGain = new float[MaxReflections];
        private readonly float[] _erPan = new float[MaxReflections];
        private volatile bool _ready;
        private volatile bool _sendOn;

        // ---- audio thread ----
        private float _fs = 48000f, _k1, _k2, _kEr, _ks, _itd, _jump;
        private float _gDL = 1f, _gDM = 1f, _gDH = 1f, _gPL, _gPM, _gPH, _gSL, _gSM, _gSH;
        private readonly float[] _z = new float[MaxChannels * 4];
        private float _erLp;
        private float[] _histP, _histE, _send;
        private int _mask, _w;
        private readonly float[] _cDL = new float[Taps], _cDR = new float[Taps], _cGL = new float[Taps], _cGR = new float[Taps];
        private readonly float[] _sDL = new float[Taps], _sDR = new float[Taps], _sGL = new float[Taps], _sGR = new float[Taps];
        private readonly bool[] _live = new bool[Taps];
        internal long Cursor;
        internal bool HasCursor;

        internal void Begin(ParelRaycastAudio owner, AudioSource source, float radius, bool area)
        {
            hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
            Owner = owner;
            Source = source;
            Radius = radius;
            IsArea = area;
            if (!_saved)
            {
                _savedBlend = source.spatialBlend;
                _savedSpatialize = source.spatialize;
                _savedPost = source.spatializePostEffects;
                _saved = true;
            }

            ApplySourceSettings();
            for (int b = 0; b < 3; b++)
            {
                _dbDirect[b] = 0f;
                _dbPath[b] = -80f;
                RawDirect[b] = 1f;
                RawPath[b] = 0f;
            }

            HasPath = false;
            ReflectionCount = 0;
            LastKind = 0;
            System.Array.Clear(AimL, 0, AimL.Length);
            System.Array.Clear(SrcL, 0, SrcL.Length);
            NextRays = Time.unscaledTime + Random.value * 0.05f;
            NextReflections = Time.unscaledTime + Random.value * 0.25f;
            _nextOwnLook = 0f;
            Setup(AudioSettings.outputSampleRate);
        }

        internal void ApplySourceSettings()
        {
            if (Source == null || Owner == null) return;
            if (!IsArea) Source.spatialBlend = 1f;
            Source.spatialize = Owner.headset3DAudio;
            // The headset's 3D audio runs first, so the reflections and the way round can be placed left/right after it.
            Source.spatializePostEffects = false;
            if (Owner.headset3DAudio && ParelRaycastAudioEngine.MetaSpatializer)
            {
                Source.SetSpatializerFloat(4, Radius); // volumetric radius: a big sound sounds big up close
                Source.SetSpatializerFloat(5, 1f); // the spatializer's own room model off: the rays do the room
            }
        }

        internal void End()
        {
            _ready = false;
            _sendOn = false;
            if (Source != null && _saved)
            {
                Source.spatialBlend = _savedBlend;
                Source.spatialize = _savedSpatialize;
                Source.spatializePostEffects = _savedPost;
            }

            _saved = false;
        }

        internal void Setup(int sampleRate)
        {
            _ready = false;
            _fs = Mathf.Max(8000, sampleRate);
            _k1 = 1f - Mathf.Exp(-2f * Mathf.PI * 350f / _fs);
            _k2 = 1f - Mathf.Exp(-2f * Mathf.PI * 2200f / _fs);
            _kEr = 1f - Mathf.Exp(-2f * Mathf.PI * 7000f / _fs);
            _ks = 1f - Mathf.Exp(-1f / (0.012f * _fs));
            _itd = 0.00066f * _fs;
            _jump = 0.025f * _fs;
            int size = Mathf.NextPowerOfTwo(Mathf.CeilToInt(_fs * 0.36f));
            if (_histP == null || _histP.Length != size)
            {
                _histP = new float[size];
                _histE = new float[size];
            }

            _mask = size - 1;
            if (_send == null) _send = new float[8192];
            System.Array.Clear(_z, 0, _z.Length);
            for (int j = 0; j < Taps; j++) _cGL[j] = _cGR[j] = 0f;
            HasCursor = false;
            _ready = true;
        }

        /// <summary>A collider that is part of the sound's own object never blocks it.</summary>
        internal bool IsOwn(Collider c)
        {
            if (c == null || Source == null) return false;
            Transform t = c.transform;
            if (t == Source.transform || t.IsChildOf(Source.transform)) return true;
            if (Time.unscaledTime >= _nextOwnLook)
            {
                _nextOwnLook = Time.unscaledTime + 5f;
                // colliders on the same rigidbody (a radio's handle parented next to it) count as its own too
                Rigidbody body = Source.GetComponentInParent<Rigidbody>();
                _ownColliders = body != null ? body.GetComponentsInChildren<Collider>() : new Collider[0];
            }

            for (int i = 0; i < _ownColliders.Length; i++)
                if (_ownColliders[i] == c) return true;
            return false;
        }

        /// <summary>Glides the sound towards what the last rays found and hands it to the audio thread.</summary>
        internal void Publish(float dt, Vector3 ear, Vector3 earRight, float sampleRate)
        {
            ParelRaycastAudio owner = Owner;
            AudioSource s = Source;
            if (owner == null || s == null) return;

            float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.03f, owner.smoothing));
            for (int b = 0; b < 3; b++)
            {
                _dbDirect[b] += (ToDb(RawDirect[b]) - _dbDirect[b]) * k;
                float wantPath = HasPath ? ToDb(RawPath[b]) : -80f;
                _dbPath[b] += (wantPath - _dbPath[b]) * k;
            }

            _tDL = FromDb(_dbDirect[0]);
            _tDM = FromDb(_dbDirect[1]);
            _tDH = FromDb(_dbDirect[2]);
            _tPL = FromDb(_dbPath[0]);
            _tPM = FromDb(_dbPath[1]);
            _tPH = FromDb(_dbPath[2]);

            float c = sampleRate / 343f;
            bool pathAudible = _tPM > 0.0012f;
            _pathGain = pathAudible ? 1f : 0f;
            if (HasPath)
            {
                _pathDelay = PathExtra * c;
                _pathPan = Vector3.Dot(PathDir, earRight);
            }

            int n = owner.roomEcho && ReflectionsAllowed ? ReflectionCount : 0;
            for (int i = 0; i < MaxReflections; i++)
            {
                if (i < n)
                {
                    _erDelay[i] = ReflectionExtra[i] * c;
                    _erGain[i] = ReflectionGain[i];
                    _erPan[i] = Vector3.Dot(ReflectionDir[i], earRight);
                }
                else _erGain[i] = 0f;
            }

            // the share for the room's echo tail: it carries farther than the direct sound (it fills the room)
            float send = 0f;
            if (owner.roomEcho && owner.echoAmount > 0f && !s.mute && ParelRaycastAudioEngine.BusLive)
            {
                float roll = ParelRaycastAudioEngine.Rolloff(s, Distance);
                float reach = Mathf.Sqrt(roll);
                bool spatialized = s.spatialize && ParelRaycastAudioEngine.HasSpatializer && s.spatialBlend > 0.5f;
                // through the headset's 3D audio this filter hears the sound after its distance fade, else before it
                float ratio = spatialized ? Mathf.Min(reach / Mathf.Max(roll, 0.0005f), 20f) : reach;
                send = s.volume * ratio * owner.echoAmount * 0.55f;
            }

            float through = Mathf.Max(_tDM, _tPM);
            _tSL = send * Mathf.Lerp(1f, Mathf.Max(_tDL, _tPL), 0.75f);
            _tSM = send * Mathf.Lerp(1f, through, 0.75f);
            _tSH = send * Mathf.Lerp(1f, Mathf.Max(_tDH, _tPH), 0.75f);
            _sendOn = send > 0.00001f;

            Occlusion = 1f - Mathf.Clamp01(Mathf.Max(RawDirect[1], HasPath ? RawPath[1] : 0f));
        }

        private static float ToDb(float g) => 20f * Mathf.Log10(Mathf.Max(g, 0.0001f));
        private static float FromDb(float db) => db <= -79f ? 0f : Mathf.Pow(10f, db * 0.05f);

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_ready || channels <= 0 || channels > MaxChannels) return;
            int frames = data.Length / channels;
            if (frames <= 0) return;

            float k1 = _k1, k2 = _k2, ks = _ks, kEr = _kEr;
            float inv = 1f / channels;
            float tDL = _tDL, tDM = _tDM, tDH = _tDH, tPL = _tPL, tPM = _tPM, tPH = _tPH, tSL = _tSL, tSM = _tSM, tSH = _tSH;
            float gDL = _gDL, gDM = _gDM, gDH = _gDH, gPL = _gPL, gPM = _gPM, gPH = _gPH, gSL = _gSL, gSM = _gSM, gSH = _gSH;
            float[] z = _z, hp = _histP, he = _histE, send = _send;
            int mask = _mask, size = mask + 1, w = _w;
            bool sendOn = _sendOn && frames <= send.Length;
            float maxDelay = size - Block - 4;

            for (int start = 0; start < frames; start += Block)
            {
                int end = Mathf.Min(frames, start + Block);
                int len = end - start;
                TapTargets(len, maxDelay);

                for (int f = start; f < end; f++)
                {
                    gDL += (tDL - gDL) * ks; gDM += (tDM - gDM) * ks; gDH += (tDH - gDH) * ks;
                    gPL += (tPL - gPL) * ks; gPM += (tPM - gPM) * ks; gPH += (tPH - gPH) * ks;
                    gSL += (tSL - gSL) * ks; gSM += (tSM - gSM) * ks; gSH += (tSH - gSH) * ks;

                    float lowM = 0f, midM = 0f, highM = 0f, rawM = 0f;
                    int i = f * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        float x = data[i + ch] + 1e-20f;
                        int zi = ch * 4;
                        float a = z[zi] += (x - z[zi]) * k1;
                        float lo = z[zi + 1] += (a - z[zi + 1]) * k1;
                        float a2 = z[zi + 2] += (x - z[zi + 2]) * k2;
                        float b2 = z[zi + 3] += (a2 - z[zi + 3]) * k2;
                        float mi = b2 - lo, hi = x - b2;
                        data[i + ch] = gDL * lo + gDM * mi + gDH * hi;
                        lowM += lo; midM += mi; highM += hi; rawM += x;
                    }

                    lowM *= inv; midM *= inv; highM *= inv; rawM *= inv;
                    if (sendOn) send[f] = gSL * lowM + gSM * midM + gSH * highM;

                    _erLp += (rawM - _erLp) * kEr;
                    hp[w] = gPL * lowM + gPM * midM + gPH * highM;
                    he[w] = _erLp;

                    float left = 0f, right = 0f;
                    for (int j = 0; j < Taps; j++)
                    {
                        if (!_live[j]) continue;
                        float[] h = j == 0 ? hp : he;
                        float dl = _cDL[j] += _sDL[j], dr = _cDR[j] += _sDR[j];
                        float gl = _cGL[j] += _sGL[j], gr = _cGR[j] += _sGR[j];
                        left += Read(h, w, dl, mask, size) * gl;
                        right += Read(h, w, dr, mask, size) * gr;
                    }

                    if (channels >= 2)
                    {
                        data[i] += left;
                        data[i + 1] += right;
                    }
                    else data[i] += 0.5f * (left + right);

                    w = (w + 1) & mask;
                }
            }

            _w = w;
            _gDL = gDL; _gDM = gDM; _gDH = gDH; _gPL = gPL; _gPM = gPM; _gPH = gPH; _gSL = gSL; _gSM = gSM; _gSH = gSH;

            // keep the filters out of denormal numbers once the sound goes quiet
            for (int q = 0; q < z.Length; q++)
                if (z[q] < 1e-15f && z[q] > -1e-15f) z[q] = 0f;
            if (_erLp < 1e-15f && _erLp > -1e-15f) _erLp = 0f;

            if (sendOn) ParelRaycastAudioEngine.AddSend(this, send, frames);
            else HasCursor = false;
        }

        // Each tap glides towards its delay (at most ~4% pitch, the Doppler of a moving reflection) and its gain over
        // one block; a delay too far to glide fades out, jumps and fades back in.
        private void TapTargets(int len, float maxDelay)
        {
            float invLen = 1f / len;
            float maxGlide = 0.04f * len;
            for (int j = 0; j < Taps; j++)
            {
                float delay, pan, gain;
                if (j == 0)
                {
                    delay = _pathDelay;
                    pan = _pathPan;
                    gain = _pathGain;
                }
                else
                {
                    delay = _erDelay[j - 1];
                    pan = _erPan[j - 1];
                    gain = _erGain[j - 1];
                }

                float x = Mathf.Clamp(pan, -1f, 1f);
                float itd = _itd * Mathf.Abs(x);
                float wantDL = Mathf.Clamp(delay + (x > 0f ? itd : 0f), 1f, maxDelay);
                float wantDR = Mathf.Clamp(delay + (x < 0f ? itd : 0f), 1f, maxDelay);
                float wantGL = gain * Mathf.Sqrt(0.5f * (1f - 0.8f * x));
                float wantGR = gain * Mathf.Sqrt(0.5f * (1f + 0.8f * x));

                bool quiet = _cGL[j] + _cGR[j] < 0.0004f;
                if (quiet && wantGL + wantGR < 0.0004f)
                {
                    _live[j] = false;
                    _cGL[j] = _cGR[j] = 0f;
                    _cDL[j] = wantDL;
                    _cDR[j] = wantDR;
                    continue;
                }

                if (quiet)
                {
                    // silent: start right at its delay, then fade in
                    _cDL[j] = wantDL;
                    _cDR[j] = wantDR;
                }
                else if (Mathf.Abs(wantDL - _cDL[j]) > _jump || Mathf.Abs(wantDR - _cDR[j]) > _jump)
                    wantGL = wantGR = 0f;

                _live[j] = true;
                _sDL[j] = Mathf.Clamp(wantDL - _cDL[j], -maxGlide, maxGlide) * invLen;
                _sDR[j] = Mathf.Clamp(wantDR - _cDR[j], -maxGlide, maxGlide) * invLen;
                _sGL[j] = (wantGL - _cGL[j]) * 0.35f * invLen;
                _sGR[j] = (wantGR - _cGR[j]) * 0.35f * invLen;
            }
        }

        private static float Read(float[] h, int w, float delay, int mask, int size)
        {
            float p = w - delay;
            if (p < 0f) p += size;
            int i0 = (int)p;
            float fr = p - i0;
            i0 &= mask;
            return h[i0] + (h[(i0 + 1) & mask] - h[i0]) * fr;
        }
    }
}
