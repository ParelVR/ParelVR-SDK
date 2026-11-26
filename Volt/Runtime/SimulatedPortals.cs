using System;
using System.Collections.Generic;
using ParelVR.VoltHost;
using ParelVR.WorldSDK;
using UnityEngine;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// The world's portals (ParelVR Portal) in the simulator: props and the editor player go through them keeping their
    /// speed, the floor or wall a portal is in lets them through, and scripts get the same VoltPortal values and portal
    /// events as in ParelVR. The live view through a portal is drawn by ParelVR only: here its surface shows as made.
    /// </summary>
    internal sealed class SimulatedPortals : MonoBehaviour, IVoltPortalService
    {
        private sealed class Prop
        {
            public Rigidbody Body;
            public Collider[] Colliders;
            public ParelVRPortal Portal;
            public float LastZ;
            public bool Started, Front = true, Ignoring, Arriving;
            public int Seen;
        }

        private sealed class Record
        {
            public int Count;
            public VoltPortalPassData Last;
        }

        private readonly Dictionary<Rigidbody, Prop> _props = new Dictionary<Rigidbody, Prop>();
        private readonly Dictionary<ParelVRPortal, Record> _records = new Dictionary<ParelVRPortal, Record>();
        private readonly Dictionary<string, (int trips, float last)> _trips = new Dictionary<string, (int, float)>();
        private readonly Collider[] _hits = new Collider[96];
        private readonly List<Rigidbody> _scratch = new List<Rigidbody>();
        private SimulatedPlayer _player;
        private ParelVRPortal _playerIgnoring;
        private Vector3 _lastEye;
        private bool _hasEye;
        private float _guardUntil;
        private int _step;

        public event Action<int, VoltPortalPassData> Event;

        public void Setup(SimulatedPlayer player)
        {
            _player = player;
            if (ParelVRPortal.All.Count > 0)
                Debug.Log("[Volt Simulator] " + ParelVRPortal.All.Count + " portal(s): props and the player go through them here; the live view through them shows in ParelVR.");
        }

        // ── props ──

        private void FixedUpdate()
        {
            _step++;
            foreach (ParelVRPortal portal in ParelVRPortal.All)
            {
                if (portal == null || !portal.IsOpen || !portal.ObjectsPass) continue;
                Rect r = portal.ShapeRect;
                Vector3 half = new Vector3(r.width * 0.5f + 0.5f, r.height * 0.5f + 0.5f, 1.5f);
                Vector3 centre = portal.transform.position + portal.transform.rotation * new Vector3(r.center.x, r.center.y, 0.5f);
                int n = UnityEngine.Physics.OverlapBoxNonAlloc(centre, half, _hits, portal.transform.rotation, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    Rigidbody body = _hits[i].attachedRigidbody;
                    if (body == null || IsWall(portal, _hits[i])) continue;
                    if (_props.TryGetValue(body, out Prop prop) && prop.Portal != portal) continue;
                    if (prop == null)
                    {
                        prop = new Prop { Body = body, Colliders = body.GetComponentsInChildren<Collider>(), Portal = portal, LastZ = Z(portal, body) };
                        prop.Front = prop.LastZ >= 0f;
                        _props[body] = prop;
                    }
                    prop.Seen = _step;
                }
            }

            _scratch.Clear();
            _scratch.AddRange(_props.Keys);
            foreach (Rigidbody body in _scratch)
                if (_props.TryGetValue(body, out Prop prop)) StepProp(prop);
        }

        private static bool IsWall(ParelVRPortal portal, Collider c)
        {
            foreach (Collider wall in portal.PassThrough)
                if (wall == c) return true;
            return false;
        }

        private static float Z(ParelVRPortal portal, Rigidbody body) => portal.Local(body.worldCenterOfMass).z;

        private void StepProp(Prop p)
        {
            ParelVRPortal portal = p.Portal;
            if (p.Body == null || portal == null || !portal.IsOpen || p.Seen != _step)
            {
                if (p.Started && p.Body != null) Raise(2, PassOf(p, portal, null), null);
                Ignore(p, false);
                _props.Remove(p.Body); // a destroyed body is still the same key
                return;
            }

            Vector3 l = portal.Local(p.Body.worldCenterOfMass);
            bool inside = portal.InOpeningLocal(l);
            float reach = p.Colliders.Length > 0 && p.Colliders[0] != null ? p.Colliders[0].bounds.extents.magnitude : 0.3f;
            bool touching = inside && Mathf.Abs(l.z) < reach;
            Ignore(p, touching);

            bool frontNow = l.z >= 0f;
            if (touching && !p.Started && !p.Arriving && (p.Front || portal.TwoSided))
            {
                p.Started = true;
                Raise(1, PassOf(p, portal, null), null);
            }

            bool crossed = p.Front && !frontNow || !p.Front && frontNow && portal.TwoSided;
            if (crossed && inside && Mathf.Abs(l.z - p.LastZ) < 2f)
            {
                Send(p, portal, new Vector2(l.x, l.y));
                return;
            }

            if (!touching)
            {
                if (p.Started && frontNow == p.Front) Raise(2, PassOf(p, portal, null), null);
                p.Started = false;
                p.Arriving = false;
                p.Front = frontNow;
            }

            p.LastZ = l.z;
        }

        private void Ignore(Prop p, bool on)
        {
            if (p.Ignoring == on || p.Portal == null) return;
            p.Ignoring = on;
            foreach (Collider wall in p.Portal.PassThrough)
                if (wall != null)
                    foreach (Collider c in p.Colliders)
                        if (c != null) UnityEngine.Physics.IgnoreCollision(c, wall, on);
        }

        /// <summary>
        /// How long ago something now <paramref name="depth"/> past a plane (negative) crossed it, moving at
        /// <paramref name="speed"/> along the plane's normal under <paramref name="pull"/>: the curved path, solved exactly
        /// (a straight-line guess puts the crossing a little off and every trip gains or loses a little height).
        /// </summary>
        private static float CrossedAgo(float depth, float speed, float pull, float step)
        {
            if (depth >= 0f || speed >= -1e-3f) return 0f;
            float a = 0.5f * pull, b = -speed, c = depth;
            float root = Mathf.Sqrt(Mathf.Max(0f, b * b - 4f * a * c));
            float tau = -2f * c / (b + root);
            return float.IsNaN(tau) || tau < 0f ? 0f : Mathf.Min(tau, step);
        }

        private void Send(Prop p, ParelVRPortal portal, Vector2 at)
        {
            ParelVRPortal far = portal.LinkedPortal;
            Rigidbody body = p.Body;
            Matrix4x4 m = portal.ToLinked(portal.transform.position + portal.transform.forward * (p.Front ? 1f : -1f));
            VoltPortalPassData pass = PassOf(p, portal, far);
            pass.OpeningPoint = at;
            Vector3 toPosition = m.MultiplyPoint3x4(body.position);
            Quaternion toRotation = m.rotation * body.rotation;
            Vector3 v = portal.KeepMomentum ? m.rotation * body.linearVelocity : Vector3.zero;
            Vector3 w = portal.KeepMomentum ? m.rotation * body.angularVelocity : Vector3.zero;
            if (portal.KeepMomentum && !body.isKinematic)
            {
                // Back to the moment it crossed, through the pair, then on for the same time on the far side (no height
                // gained or lost in the step it crossed in).
                // Physics keeps the speed of the middle of the last step; half a step of gravity makes it the speed now.
                Vector3 n = portal.transform.forward * (p.Front ? 1f : -1f);
                Vector3 g = body.useGravity ? UnityEngine.Physics.gravity : Vector3.zero;
                float dt = Time.fixedDeltaTime;
                Vector3 now = body.linearVelocity + 0.5f * dt * g;
                float vn = Vector3.Dot(now, n);
                float tau = CrossedAgo(Vector3.Dot(body.worldCenterOfMass - portal.transform.position, n), vn, Vector3.Dot(g, n), dt);
                Vector3 crossVelocity = m.rotation * (now - g * tau);
                toPosition = m.MultiplyPoint3x4(body.position - now * tau + 0.5f * tau * tau * g) + crossVelocity * tau + 0.5f * tau * tau * g;
                v = crossVelocity + g * tau - 0.5f * dt * g;
            }

            Ignore(p, false);
            _props.Remove(body);
            RigidbodyInterpolation interpolation = body.interpolation;
            body.interpolation = RigidbodyInterpolation.None;
            body.position = toPosition;
            body.rotation = toRotation;
            body.transform.SetPositionAndRotation(toPosition, toRotation);
            if (!body.isKinematic)
            {
                body.linearVelocity = v;
                body.angularVelocity = w;
            }
            body.interpolation = interpolation;

            // The far side takes it on half in its wall, already let through.
            var arrived = new Prop { Body = body, Colliders = p.Colliders, Portal = far, Seen = _step, LastZ = Z(far, body), Arriving = true };
            _props[body] = arrived;
            Ignore(arrived, true);

            pass.ExitPosition = toPosition;
            pass.ExitRotation = toRotation;
            pass.ExitVelocity = v;
            pass.AngularVelocity = w;
            portal.onObjectPassed?.Invoke(body.gameObject);
            Raise(0, pass, "object:" + body.GetInstanceID());
        }

        private static VoltPortalPassData PassOf(Prop p, ParelVRPortal portal, ParelVRPortal far)
        {
            Rigidbody body = p.Body;
            Bounds b = default;
            bool any = false;
            foreach (Collider c in p.Colliders)
                if (c != null)
                {
                    if (!any) b = c.bounds;
                    else b.Encapsulate(c.bounds);
                    any = true;
                }
            Vector3 l = portal != null && body != null ? portal.Local(body.worldCenterOfMass) : Vector3.zero;
            return new VoltPortalPassData
            {
                EntrancePortal = portal,
                ExitPortal = far != null ? far : portal != null ? portal.LinkedPortal : null,
                Name = body != null ? body.name : string.Empty,
                Object = body != null ? body.gameObject : null,
                Rigidbody = body,
                Mass = body != null ? body.mass : 0f,
                Size = any ? b.size : Vector3.zero,
                FromFront = p.Front,
                OpeningPoint = new Vector2(l.x, l.y),
                EnterPosition = body != null ? body.position : Vector3.zero,
                EnterRotation = body != null ? body.rotation : Quaternion.identity,
                EnterVelocity = body != null ? body.linearVelocity : Vector3.zero,
                ExitPosition = body != null ? body.position : Vector3.zero,
                ExitRotation = body != null ? body.rotation : Quaternion.identity
            };
        }

        // ── the editor player ──

        private void LateUpdate()
        {
            Camera eye = _player != null ? _player.View : null;
            CharacterController capsule = _player != null ? _player.Capsule : null;
            if (eye == null || capsule == null) return;
            Vector3 e = eye.transform.position;
            if (!_hasEye || Time.time < _guardUntil)
            {
                _lastEye = e;
                _hasEye = true;
                return;
            }

            ParelVRPortal over = null;
            foreach (ParelVRPortal portal in ParelVRPortal.All)
            {
                if (portal == null || !portal.IsOpen || !portal.PlayersPass) continue;
                Vector3 a = portal.Local(_lastEye), b = portal.Local(e);
                if (portal.InOpeningLocal(portal.Local(capsule.bounds.center)) && Mathf.Abs(portal.Local(capsule.bounds.center).z) < 1f) over = portal;
                bool forward = a.z >= 0f && b.z < 0f, backward = portal.TwoSided && a.z < 0f && b.z >= 0f;
                if (!(forward || backward) || (e - _lastEye).sqrMagnitude > 4f) continue;
                float t = a.z / (a.z - b.z);
                Vector3 hit = Vector3.Lerp(a, b, t);
                if (!portal.InOpeningLocal(hit)) continue;
                PlayerThrough(portal, forward, new Vector2(hit.x, hit.y));
                return;
            }

            if (over != _playerIgnoring)
            {
                if (_playerIgnoring != null) IgnoreWalls(_playerIgnoring, capsule, false);
                if (over != null) IgnoreWalls(over, capsule, true);
                _playerIgnoring = over;
            }
            _lastEye = e;
        }

        private static void IgnoreWalls(ParelVRPortal portal, Collider capsule, bool on)
        {
            foreach (Collider wall in portal.PassThrough)
                if (wall != null) UnityEngine.Physics.IgnoreCollision(capsule, wall, on);
        }

        private void PlayerThrough(ParelVRPortal portal, bool fromFront, Vector2 at)
        {
            ParelVRPortal far = portal.LinkedPortal;
            Matrix4x4 m = portal.ToLinked(portal.transform.position + portal.transform.forward * (fromFront ? 1f : -1f));
            Transform root = _player.transform;
            Vector3 velocity = _player.Velocity;
            Vector3 feet = m.MultiplyPoint3x4(root.position);
            Quaternion facing = m.rotation * _player.View.transform.rotation;
            Vector3 look = Vector3.ProjectOnPlane(facing * Vector3.forward, Vector3.up);
            Quaternion yaw = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look, Vector3.up) : root.rotation;
            // A tilted pair (wall to floor): stand up at the exit, clear of its plane.
            if (Vector3.Dot(m.rotation * Vector3.up, Vector3.up) < 0.99f)
            {
                Vector3 exit = m.MultiplyPoint3x4(portal.transform.position + portal.transform.rotation * new Vector3(at.x, at.y, 0f));
                float up = Mathf.Abs(Vector3.Dot(far.transform.forward, Vector3.up));
                Vector3 centre = exit + far.transform.forward * Mathf.Lerp(0.4f, 0.95f, up);
                feet = centre - Vector3.up * 0.9f;
                if (UnityEngine.Physics.Raycast(centre, Vector3.down, out RaycastHit ground, 0.9f, ~0, QueryTriggerInteraction.Ignore) && !IsWall(far, ground.collider))
                    feet.y = Mathf.Max(feet.y, ground.point.y);
            }
            Vector3 v = portal.KeepMomentum ? m.rotation * velocity : Vector3.zero;
            if (Vector3.Dot(far.transform.forward, Vector3.up) > 0.5f) v.y = Mathf.Max(v.y, 2f);

            if (_playerIgnoring != null) IgnoreWalls(_playerIgnoring, _player.Capsule, false);
            _playerIgnoring = null;
            var pass = new VoltPortalPassData
            {
                EntrancePortal = portal,
                ExitPortal = far,
                IsPlayer = true,
                IsLocalPlayer = true,
                Name = _player.LocalPlayer != null ? _player.LocalPlayer.DisplayName : "Player",
                FromFront = fromFront,
                OpeningPoint = at,
                EnterPosition = root.position,
                EnterRotation = root.rotation,
                EnterVelocity = velocity,
                ExitPosition = feet,
                ExitRotation = yaw,
                ExitVelocity = v
            };
            _player.Teleport(feet, yaw);
            _player.SetVelocity(v);
            _guardUntil = Time.time + 0.3f;
            _hasEye = false;
            portal.onPlayerPassed?.Invoke();
            Raise(0, pass, "player:local");
        }

        // ── events ──

        private void Raise(int kind, VoltPortalPassData pass, string tripKey)
        {
            pass.Time = Time.time;
            pass.Frame = Time.frameCount;
            if (tripKey != null)
            {
                _trips.TryGetValue(tripKey, out (int trips, float last) log);
                pass.Trips = log.trips + 1;
                pass.SinceLast = log.trips > 0 ? pass.Time - log.last : -1f;
                _trips[tripKey] = (pass.Trips, pass.Time);
            }

            if (kind == 0 && pass.EntrancePortal is ParelVRPortal entrance)
            {
                if (!_records.TryGetValue(entrance, out Record record)) _records[entrance] = record = new Record();
                record.Count++;
                record.Last = pass;
            }

            try { Event?.Invoke(kind, pass); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void OnDestroy()
        {
            foreach (Prop p in _props.Values) Ignore(p, false);
            _props.Clear();
            if (_playerIgnoring != null && _player != null && _player.Capsule != null) IgnoreWalls(_playerIgnoring, _player.Capsule, false);
        }

        // ── IVoltPortalService ──

        private static ParelVRPortal P(Component c) => c as ParelVRPortal;

        public bool IsAvailable => true;

        public void GetAll(List<Component> into)
        {
            foreach (ParelVRPortal p in ParelVRPortal.All)
                if (p != null) into.Add(p);
        }

        public Component Get(GameObject on) => on != null ? on.GetComponentInParent<ParelVRPortal>(true) : null;
        public bool IsOpen(Component portal) => P(portal) != null && P(portal).IsOpen;
        public bool GetEnabled(Component portal) => P(portal) != null && P(portal).enabled;
        public void SetEnabled(Component portal, bool enabled) { if (P(portal) != null) P(portal).enabled = enabled; }
        public Component GetLinked(Component portal) => P(portal) != null ? P(portal).LinkedPortal : null;

        public void SetLinked(Component portal, Component linked)
        {
            ParelVRPortal p = P(portal);
            if (p != null) p.LinkedPortal = P(linked) != p ? P(linked) : null;
        }

        public Vector3 OpeningCenter(Component portal)
        {
            ParelVRPortal p = P(portal);
            if (p == null) return Vector3.zero;
            Rect r = p.ShapeRect;
            return p.transform.position + p.transform.rotation * new Vector3(r.center.x, r.center.y, 0f);
        }

        public Vector2 OpeningSize(Component portal) => P(portal) != null ? P(portal).ShapeRect.size : Vector2.zero;

        public bool GetFlag(Component portal, int flag)
        {
            ParelVRPortal p = P(portal);
            if (p == null) return false;
            return flag == 0 ? p.TwoSided : flag == 1 ? p.PlayersPass : flag == 2 ? p.ObjectsPass : flag == 3 && p.KeepMomentum;
        }

        public void SetFlag(Component portal, int flag, bool on)
        {
            ParelVRPortal p = P(portal);
            if (p == null) return;
            if (flag == 0) p.TwoSided = on;
            else if (flag == 1) p.PlayersPass = on;
            else if (flag == 2) p.ObjectsPass = on;
            else if (flag == 3) p.KeepMomentum = on;
        }

        public Color GetColor(Component portal, int which) => P(portal) == null ? Color.clear : which == 0 ? P(portal).EdgeGlow : P(portal).NestedColor;

        public void SetColor(Component portal, int which, Color color)
        {
            ParelVRPortal p = P(portal);
            if (p == null) return;
            if (which == 0) p.EdgeGlow = color;
            else p.NestedColor = color;
        }

        public float GetDrawDistance(Component portal) => P(portal) != null ? P(portal).DrawWithin : 0f;
        public void SetDrawDistance(Component portal, float metres) { if (P(portal) != null) P(portal).DrawWithin = metres; }

        public Matrix4x4 ToLinked(Component portal)
        {
            ParelVRPortal p = P(portal);
            return p != null && p.LinkedPortal != null ? p.ToLinked(p.transform.position + p.transform.forward) : Matrix4x4.identity;
        }

        public bool InOpening(Component portal, Vector3 point) => P(portal) != null && P(portal).InOpening(point);
        public int PassCount(Component portal) => P(portal) != null && _records.TryGetValue(P(portal), out Record r) ? r.Count : 0;
        public VoltPortalPassData LastPass(Component portal) => P(portal) != null && _records.TryGetValue(P(portal), out Record r) ? r.Last : null;

        public void ObjectsInside(Component portal, List<GameObject> into)
        {
            foreach (Prop p in _props.Values)
                if (p.Portal == P(portal) && p.Started && p.Body != null) into.Add(p.Body.gameObject);
        }

        public bool LocalPlayerReachingIn(Component portal) => false;

        public bool SendLocalPlayer(Component portal)
        {
            ParelVRPortal p = P(portal);
            if (p == null || !p.IsOpen || !p.PlayersPass || _player == null || _player.View == null) return false;
            Rect r = p.ShapeRect;
            PlayerThrough(p, true, r.center);
            return true;
        }

        public bool Send(Component portal, GameObject go)
        {
            ParelVRPortal p = P(portal);
            Rigidbody body = go != null ? go.GetComponentInParent<Rigidbody>() : null;
            if (p == null || !p.IsOpen || go == null) return false;
            Rect r = p.ShapeRect;
            Vector3 mid = p.transform.position + p.transform.rotation * new Vector3(r.center.x, r.center.y, -0.02f);
            if (body == null)
            {
                Matrix4x4 m = ToLinked(p);
                go.transform.SetPositionAndRotation(m.MultiplyPoint3x4(mid), m.rotation * go.transform.rotation);
                return true;
            }

            if (_props.TryGetValue(body, out Prop old)) Ignore(old, false);
            body.position += mid - body.worldCenterOfMass;
            body.transform.position = body.position;
            var prop = new Prop { Body = body, Colliders = body.GetComponentsInChildren<Collider>(), Portal = p, Seen = _step };
            Send(prop, p, r.center);
            return true;
        }
    }
}
