using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// What the game gives the world portals: where the local player's head is, where they stand, how to move
    /// them, how to take them to another world, and the portal look it draws. Filled in by the game at start-up
    /// (and by the Volt simulator in the editor's play mode).
    /// </summary>
    public static class ParelVRPortalHooks
    {
        /// <summary>The local player's head (eyes) in world space.</summary>
        public static Func<Vector3> LocalHead;
        /// <summary>The local player's standing position and facing.</summary>
        public static Func<Pose> LocalPlayer;
        /// <summary>Moves the local player (feet position, facing).</summary>
        public static Action<Vector3, Quaternion> TeleportLocalPlayer;
        /// <summary>Takes the local player to a world (by its world ID).</summary>
        public static Action<string> JoinWorld;
        /// <summary>Puts the game's portal doorway on a transform (height in metres); returns it, or null.</summary>
        public static Func<Transform, float, GameObject> CreateDoorway;
        /// <summary>
        /// Builds the game's whole world portal for this component (opening near the player, the world behind it shown
        /// live, walking through it); returns it, or null. When it returns one, the game notices the player walking in.
        /// </summary>
        public static Func<ParelVRWorldPortal, GameObject> CreateWorldPortal;
        /// <summary>A moment of portal light over the view while the player passes through (strength 0..1).</summary>
        public static Action<float> Flash;
        /// <summary>
        /// Whether a point where the player went in (the portal's own space, on its plane) is inside the doorway the game
        /// drew there (its round ring), so going through any part of what is seen as the way through goes through.
        /// Null, or no doorway drawn: the Size rectangle.
        /// </summary>
        public static Func<GameObject, Vector3, bool> InsideDoorway;
        /// <summary>
        /// Brings a <see cref="ParelVRPortal"/> to life: its live view, players and props passing through, and what is
        /// half-way through seen at the other end. Returns what it added, or null.
        /// </summary>
        public static Func<ParelVRPortal, Component> CreatePortal;

        internal static Vector3? Head()
        {
            if (LocalHead != null) return LocalHead();
            Camera main = Camera.main;
            return main != null ? main.transform.position : (Vector3?)null;
        }
    }

    /// <summary>
    /// What both ends of a local portal pair share. The opening is Size, its foot at the object's position and its
    /// plane the object's own, so it can stand in a wall, lie in the floor like a hole (blue line up) or hang in the
    /// ceiling. Going into it takes the local player, and props with a Rigidbody, out of the other end. A Local Portal
    /// In always takes them in; its Out takes them back too when the In is two-way.
    /// </summary>
    public abstract class ParelVRLocalPortalEnd : MonoBehaviour
    {
        // Feet this close above a doorway lying in the floor have stepped into it.
        private const float StepInHeight = 0.15f;
        // After a trip, a moment before the same doorway takes them again.
        private const float GuardSeconds = 0.5f;

        /// <summary>Width and height of the opening, in metres.</summary>
        public abstract Vector2 Size { get; }

        /// <summary>The end going in here leads to, or null while this end takes nobody in.</summary>
        public abstract ParelVRLocalPortalEnd LeadsTo { get; }

        /// <summary>Either side can be gone into (off: only the side the blue line points to).</summary>
        protected abstract bool BothSides { get; }

        /// <summary>Props with a Rigidbody go through too.</summary>
        protected abstract bool TakesObjects { get; }

        /// <summary>The game's doorway drawn here, or null.</summary>
        protected abstract GameObject Doorway { get; }

        /// <summary>Somebody or something came out here from <paramref name="from"/> just now.</summary>
        protected abstract void ShowArrival(ParelVRLocalPortalEnd from);

        /// <summary>Lying in the floor (its blue line points up): stepping onto it is going in, like into a hole.</summary>
        public bool InFloor => Vector3.Dot(transform.forward, Vector3.up) > 0.5f;

        /// <summary>A world point in the opening's own space (metres, unscaled; Z = in front of its plane).</summary>
        public Vector3 Local(Vector3 world) => Quaternion.Inverse(transform.rotation) * (world - transform.position);

        private Vector3 lastHead;
        private bool hasLast;
        private bool playerWaits = true;
        private float guardUntil;
        private static bool warned;

        private static readonly Collider[] hits = new Collider[64];
        private readonly Dictionary<Rigidbody, Collider[]> bodies = new Dictionary<Rigidbody, Collider[]>();
        private readonly Dictionary<Rigidbody, float> lastDepth = new Dictionary<Rigidbody, float>();
        private readonly Dictionary<Rigidbody, float> waiting = new Dictionary<Rigidbody, float>();
        private readonly HashSet<Rigidbody> near = new HashSet<Rigidbody>();
        private readonly List<Rigidbody> scratch = new List<Rigidbody>();

        protected virtual void OnEnable()
        {
            hasLast = false;
            // Somebody already standing on a floor doorway as it switches on is taken once they step off and on.
            playerWaits = true;
        }

        private static Vector3 Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up);

        // ---- the local player ----------------------------------------------------------------------------------

        protected virtual void LateUpdate()
        {
            ParelVRLocalPortalEnd to = LeadsTo;
            Vector3? headWorld = ParelVRPortalHooks.Head();
            if (to == null || headWorld == null)
            {
                hasLast = false;
                return;
            }

            Vector3 head = Local(headWorld.Value);
            bool had = hasLast;
            Vector3 before = lastHead;
            lastHead = head;
            hasLast = true;

            if (InFloor && ParelVRPortalHooks.LocalPlayer != null)
            {
                Vector3 feet = Local(ParelVRPortalHooks.LocalPlayer().position);
                bool on = feet.z <= StepInHeight && feet.z >= -1.5f && Inside(new Vector3(feet.x, feet.y, 0f), true);
                // Came out onto it: taken in again only once they have stepped off.
                if (playerWaits) playerWaits = on;
                else if (on && Time.unscaledTime >= guardUntil)
                {
                    SendPlayer(to, true, new Vector2(feet.x, feet.y));
                    return;
                }
            }
            else playerWaits = false;

            if (!had || Time.unscaledTime < guardUntil || (head - before).sqrMagnitude > 1f) return;
            if (Mathf.Sign(head.z) == Mathf.Sign(before.z) || Mathf.Abs(before.z) < 1e-5f) return;
            bool fromFront = before.z > 0f;
            if (!fromFront && !BothSides) return;
            Vector3 at = Vector3.Lerp(before, head, before.z / (before.z - head.z));
            if (Inside(at, false)) SendPlayer(to, fromFront, new Vector2(at.x, at.y));
        }

        private bool Inside(Vector3 at, bool exact)
        {
            GameObject doorway = Doorway;
            if (doorway != null && doorway.activeInHierarchy && ParelVRPortalHooks.InsideDoorway != null)
                return ParelVRPortalHooks.InsideDoorway(doorway, at);
            Vector2 size = Size;
            float below = exact ? 0f : 0.4f, above = exact ? 0f : 0.3f;
            return Mathf.Abs(at.x) <= size.x * 0.5f && at.y >= -below && at.y <= size.y + above;
        }

        private void SendPlayer(ParelVRLocalPortalEnd to, bool fromFront, Vector2 at)
        {
            guardUntil = Time.unscaledTime + GuardSeconds;
            hasLast = false;
            if (ParelVRPortalHooks.TeleportLocalPlayer == null || ParelVRPortalHooks.LocalPlayer == null)
            {
                if (!warned) Debug.LogWarning("[ParelVR] Local Portal: the game has not connected portals (are you in the game or the Volt simulator?).", this);
                warned = true;
                return;
            }

            Transform exit = to.transform;
            Quaternion flip = Quaternion.Euler(0f, fromFront ? 180f : 0f, 0f);
            Quaternion turn = exit.rotation * flip * Quaternion.Inverse(transform.rotation);
            Pose player = ParelVRPortalHooks.LocalPlayer();
            bool upright = Vector3.Dot(turn * Vector3.up, Vector3.up) > 0.99f;
            Vector3 position;
            if (upright)
            {
                // In and out are one doorway: the player comes out exactly where the view through it showed them going
                // (the same distance from the exit as from this one, mirrored through it), so nothing jumps as they
                // pass. Only never behind the exit's plane: something often stands right behind an exit.
                Vector3 there = flip * Local(player.position);
                there.z = Mathf.Max(there.z, 0.03f);
                position = exit.position + exit.rotation * there;
            }
            else position = to.ComeOut(flip * new Vector3(at.x, at.y, 0f));

            Vector3 forward = Flat(turn * (player.rotation * Vector3.forward));
            if (!upright)
            {
                // Out of a wall: facing out of it. Out of a floor or ceiling: the way they were going, else the way
                // the exit's top edge points.
                if (Mathf.Abs(Vector3.Dot(exit.forward, Vector3.up)) <= 0.5f) forward = Flat(exit.forward);
                else if (forward.sqrMagnitude < 0.01f) forward = Flat(turn * (player.rotation * Vector3.up));
            }
            if (forward.sqrMagnitude < 1e-4f) forward = Flat(player.rotation * Vector3.forward);
            Quaternion facing = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : Quaternion.Euler(0f, exit.eulerAngles.y, 0f);

            to.PlayerArrived(this);
            ParelVRPortalHooks.Flash?.Invoke(1f);
            ParelVRPortalHooks.TeleportLocalPlayer(position, facing);
        }

        /// <summary>
        /// Where somebody coming out of this end at a point of its plane (its own space) stands, when the two ends are
        /// turned differently (a hole in the floor leading to a wall): standing up, clear of it.
        /// </summary>
        private Vector3 ComeOut(Vector3 p)
        {
            Vector3 point = transform.position + transform.rotation * p;
            float up = Vector3.Dot(transform.forward, Vector3.up);
            // Up out of a floor: standing on it.
            if (up > 0.5f) return point + Vector3.up * 0.02f;
            // Down out of a ceiling: on the ground below, or dropping to it.
            if (up < -0.5f)
                return Physics.Raycast(point + Vector3.down * 0.3f, Vector3.down, out RaycastHit ground, 1.65f, ~0, QueryTriggerInteraction.Ignore)
                    ? ground.point
                    : point + Vector3.down * 1.95f;
            // Out of a wall: at its foot, a step in front of it.
            Vector2 size = Size;
            Vector3 foot = transform.position + transform.rotation * new Vector3(Mathf.Clamp(p.x, -size.x * 0.5f, size.x * 0.5f), 0f, 0f);
            return foot + Flat(transform.forward).normalized * 0.35f;
        }

        private void PlayerArrived(ParelVRLocalPortalEnd from)
        {
            playerWaits = true;
            hasLast = false;
            guardUntil = Time.unscaledTime + GuardSeconds;
            ShowArrival(from);
        }

        // ---- props -----------------------------------------------------------------------------------------------

        protected virtual void FixedUpdate()
        {
            ParelVRLocalPortalEnd to = LeadsTo;
            if (to == null || !TakesObjects)
            {
                if (lastDepth.Count > 0 || waiting.Count > 0 || bodies.Count > 0)
                {
                    lastDepth.Clear();
                    waiting.Clear();
                    bodies.Clear();
                }
                return;
            }

            Vector2 size = Size;
            Vector3 n = transform.forward;
            Vector3 centre = transform.position + transform.rotation * new Vector3(0f, size.y * 0.5f, 0f);
            Vector3 half = new Vector3(size.x * 0.5f + 0.5f, size.y * 0.5f + 0.5f, 1.5f);
            int count = Physics.OverlapBoxNonAlloc(centre, half, hits, transform.rotation, ~0, QueryTriggerInteraction.Ignore);
            float dt = Time.fixedDeltaTime;
            near.Clear();
            for (int i = 0; i < count; i++)
            {
                // Kinematic bodies are held or driven (a held prop goes through once let go).
                Rigidbody body = hits[i].attachedRigidbody;
                if (body == null || body.isKinematic || !near.Add(body)) continue;
                Vector3 c = Local(body.worldCenterOfMass);
                bool had = lastDepth.TryGetValue(body, out float before);
                lastDepth[body] = c.z;
                bool inside = Mathf.Abs(c.x) <= size.x * 0.5f && c.y >= 0f && c.y <= size.y;
                Bounds b = BoundsOf(body);
                float mid = Vector3.Dot(b.center - transform.position, n);
                float reach = Mathf.Abs(b.extents.x * n.x) + Mathf.Abs(b.extents.y * n.y) + Mathf.Abs(b.extents.z * n.z);
                float vn = Vector3.Dot(body.linearVelocity, n);
                // Its face toward the opening reaches it within this step, before it would hit the floor or wall
                // behind (which would stop it): a ball keeps its speed, a box set down on a floor doorway drops in.
                bool touching = inside && (c.z >= 0f
                    ? mid - reach + Mathf.Min(vn, 0f) * dt <= 0.02f
                    : mid + reach + Mathf.Max(vn, 0f) * dt >= -0.02f);
                if (waiting.ContainsKey(body))
                {
                    // Just came out here: taken in again once it has moved off.
                    if (!touching) waiting.Remove(body);
                    continue;
                }

                bool crossed = inside && had && (before >= 0f) != (c.z >= 0f) && Mathf.Abs(c.z - before) < 2f;
                if (!touching && !crossed) continue;
                bool fromFront = crossed ? before >= 0f : c.z >= 0f;
                if (!fromFront && !BothSides) continue;
                // Moving off the opening (it bounced off beside it): not going in.
                if (!crossed && (fromFront ? vn > 0.05f : vn < -0.05f)) continue;
                SendObject(body, to, fromFront, c, reach);
            }

            scratch.Clear();
            foreach (Rigidbody body in lastDepth.Keys)
                if (body == null || !near.Contains(body)) scratch.Add(body);
            foreach (Rigidbody body in scratch)
            {
                lastDepth.Remove(body);
                bodies.Remove(body);
            }

            scratch.Clear();
            foreach (KeyValuePair<Rigidbody, float> w in waiting)
                if (w.Key == null || !near.Contains(w.Key) && Time.time - w.Value > 0.25f) scratch.Add(w.Key);
            foreach (Rigidbody body in scratch) waiting.Remove(body);
        }

        private Bounds BoundsOf(Rigidbody body)
        {
            if (!bodies.TryGetValue(body, out Collider[] colliders)) bodies[body] = colliders = body.GetComponentsInChildren<Collider>();
            var b = new Bounds(body.worldCenterOfMass, Vector3.zero);
            bool any = false;
            foreach (Collider c in colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || c.attachedRigidbody != body) continue;
                if (!any) b = c.bounds;
                else b.Encapsulate(c.bounds);
                any = true;
            }

            return b;
        }

        private void SendObject(Rigidbody body, ParelVRLocalPortalEnd to, bool fromFront, Vector3 c, float reach)
        {
            Transform exit = to.transform;
            Quaternion flip = Quaternion.Euler(0f, fromFront ? 180f : 0f, 0f);
            Quaternion turn = exit.rotation * flip * Quaternion.Inverse(transform.rotation);
            // Out of the exit's front, as far from it as it was from this one (so a ball bouncing between two floor
            // doorways keeps its height trip after trip), and clear of it.
            Vector3 p = flip * c;
            p.z = Mathf.Max(Mathf.Abs(c.z), reach + 0.01f);
            Vector3 centreOfMass = exit.position + exit.rotation * p;
            Quaternion rotation = turn * body.rotation;
            Vector3 position = centreOfMass - turn * (body.worldCenterOfMass - body.position);
            // Physics keeps the speed of the middle of the last step: half a step of gravity makes it the speed now,
            // which is what goes through; the far side keeps it as the middle of a step again (no height lost a trip).
            Vector3 g = body.useGravity ? Physics.gravity : Vector3.zero;
            float half = 0.5f * Time.fixedDeltaTime;
            Vector3 velocity = turn * (body.linearVelocity + g * half) - g * half, spin = turn * body.angularVelocity;

            lastDepth.Remove(body);
            RigidbodyInterpolation interpolation = body.interpolation;
            body.interpolation = RigidbodyInterpolation.None;
            body.position = position;
            body.rotation = rotation;
            body.transform.SetPositionAndRotation(position, rotation);
            body.linearVelocity = velocity;
            body.angularVelocity = spin;
            body.interpolation = interpolation;
            to.ObjectArrived(body, this);
        }

        private void ObjectArrived(Rigidbody body, ParelVRLocalPortalEnd from)
        {
            waiting[body] = Time.time;
            lastDepth.Remove(body);
            ShowArrival(from);
        }

#if UNITY_EDITOR
        /// <summary>Editor tests: switching on, one frame of the player check, one physics step of the props check.</summary>
        public void TestEnable() => OnEnable();
        public void TestPlayerStep() => LateUpdate();
        public void TestPropStep() => FixedUpdate();
        public void TestUnguard() => guardUntil = 0f;
#endif
    }

    /// <summary>
    /// The doorway a player walks into. Walking through it (from the side its blue line points to) takes them to
    /// its <see cref="Exit"/>, keeping where they stand in the doorway and the way they walk, as if the two were
    /// one opening. Size is the doorway's opening, its foot at this object's position. Turn the object to stand it in
    /// a wall, lay it in the floor like a hole (blue line up: stepping onto it drops them in) or hang it in the
    /// ceiling. Props with a Rigidbody go through too. Two Way: the Out takes them back here as well.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Local Portal In")]
    public sealed class ParelVRLocalPortalIn : ParelVRLocalPortalEnd
    {
        [Tooltip("Where walking through this doorway takes the player.")]
        [SerializeField] private ParelVRLocalPortalOut exit;
        [Tooltip("Width and height of the opening, in metres. Its foot is at this object's position, its height along this object's up: " +
                 "turn the object to stand it in a wall, lay it in the floor (blue line up) or hang it in the ceiling.")]
        [SerializeField] private Vector2 size = new Vector2(1.2f, 2.3f);
        [Tooltip("Draw the ParelVR portal doorway here (off: the world's own models show where it is).")]
        [SerializeField] private bool showDoorway = true;
        [Tooltip("Both ways: walking into the Out brings the player (and props) back out here, and either side of each doorway can be walked into. " +
                 "Off: only this doorway leads anywhere, from the side its blue line points to.")]
        [SerializeField] private bool twoWay;
        [Tooltip("Props with a Rigidbody go through too, keeping their speed turned the way the Out faces (a held prop goes through once let go).")]
        [SerializeField] private bool objectsPass = true;
        [SerializeField] private Color gizmoColor = new Color(0.62f, 0.38f, 1f, 0.95f);

        public ParelVRLocalPortalOut Exit
        {
            get => exit;
            set
            {
                if (exit == value) return;
                if (exit != null) exit.Forget(this);
                exit = value;
                if (exit != null && isActiveAndEnabled) exit.Remember(this);
            }
        }

        public override Vector2 Size => size;
        /// <summary>Both ways: the Out leads back here too, and either side of each doorway can be walked into.</summary>
        public bool TwoWay { get => twoWay; set => twoWay = value; }
        public bool ObjectsPass { get => objectsPass; set => objectsPass = value; }

        public override ParelVRLocalPortalEnd LeadsTo => exit != null && exit.isActiveAndEnabled ? exit : null;
        protected override bool BothSides => twoWay;
        protected override bool TakesObjects => objectsPass;
        protected override GameObject Doorway => doorway;
        // The doorway here already opens as the player comes near.
        protected override void ShowArrival(ParelVRLocalPortalEnd from) { }

        private GameObject doorway;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (exit != null) exit.Remember(this);
            if (showDoorway && doorway == null && ParelVRPortalHooks.CreateDoorway != null)
                doorway = ParelVRPortalHooks.CreateDoorway(transform, size.y);
            if (doorway != null) doorway.SetActive(true);
        }

        private void OnDisable()
        {
            if (exit != null) exit.Forget(this);
            if (doorway != null) doorway.SetActive(false);
        }

        private void OnDestroy()
        {
            if (doorway != null) Destroy(doorway);
        }

        private void OnDrawGizmos()
        {
            ParelVRPortalGizmo.Draw(transform, size, gizmoColor, twoWay ? "In (two-way)" : "In");
            if (exit == null) return;
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.35f);
            Gizmos.DrawLine(ParelVRPortalGizmo.Middle(transform, size), ParelVRPortalGizmo.Middle(exit.transform, exit.Size));
        }
    }

    internal static class ParelVRPortalGizmo
    {
        /// <summary>The middle of an opening (its foot at the object, its height along the object's up).</summary>
        internal static Vector3 Middle(Transform t, Vector2 size) => t.position + t.rotation * new Vector3(0f, size.y * 0.5f, 0f);

        /// <summary>The opening's outline, a ring at its foot and the facing line, like the spawn point's.</summary>
        internal static void Draw(Transform t, Vector2 size, Color color, string kind)
        {
            Gizmos.color = color;
            Matrix4x4 was = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            float hw = size.x * 0.5f;
            Vector3 a = new Vector3(-hw, 0f, 0f), b = new Vector3(hw, 0f, 0f), c = new Vector3(hw, size.y, 0f), d = new Vector3(-hw, size.y, 0f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawLine(b, c);
            Gizmos.DrawLine(c, d);
            Gizmos.DrawLine(d, a);
            Gizmos.color = new Color(color.r, color.g, color.b, color.a * 0.18f);
            Gizmos.DrawCube(new Vector3(0f, size.y * 0.5f, 0f), new Vector3(size.x, size.y, 0.01f));
            Gizmos.matrix = was;
            Gizmos.color = color;
            Gizmos.DrawWireSphere(t.position, 0.4f);
            Gizmos.DrawLine(Middle(t, size), Middle(t, size) + t.forward * 1.2f);
#if UNITY_EDITOR
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.Label(t.position + t.rotation * new Vector3(0f, size.y + 0.15f, 0f), "Portal " + kind);
#endif
        }
    }
}
