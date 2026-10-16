using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>Starting points for common Motion Bone set-ups (see <see cref="ParelMotionBone.ApplyPreset"/>).</summary>
    public enum ParelMotionBonePreset
    {
        Hair = 0,
        LongHair = 1,
        Tail = 2,
        Ears = 3,
        Clothing = 4,
        Accessory = 5,
        SoftBody = 6,
    }

    /// <summary>
    /// Motion Bone: secondary motion for hair, tails, ears, skirts and anything else hanging off the
    /// avatar, simulated from <see cref="rootTransform"/> down through every child bone. Supports
    /// pull / momentum / stiffness / gravity (+ falloff) / immobile, angle / hinge / polar limits,
    /// stretch and squish, colliders (its own list plus everyone's hands and fingers), grabbing and
    /// posing, and the {parameter}_IsGrabbed / _IsPosed / _Angle / _Stretch / _Squish parameters.
    /// The simulation is local: every client runs it for every avatar, no bone is networked.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/Motion Bone")]
    public sealed class ParelMotionBone : MonoBehaviour
    {
        public enum IntegrationType
        {
            Simplified = 0,
            Advanced = 1,
        }

        public enum MultiChildType
        {
            Ignore = 0,
            First = 1,
            Average = 2,
        }

        public enum ImmobileType
        {
            AllMotion = 0,
            World = 1,
        }

        public enum LimitType
        {
            None = 0,
            Angle = 1,
            Hinge = 2,
            Polar = 3,
        }

        /// <summary>Motion Bone versions. 1.1 adds Max Squish and Stretch Motion.</summary>
        public enum Version
        {
            Version_1_0 = 0,
            Version_1_1 = 1,
        }

        public const int MaxParticles = 256;
        /// <summary>The step length the force settings are tuned for. Faster frame rates take shorter steps.</summary>
        private const float StepSeconds = 1f / 90f;
        private const int MaxStepsPerFrame = 4;
        private const float TeleportDistance = 3f;
        /// <summary>A turn sharper than this in one frame (a snap turn) carries the chain round instead of whipping it.</summary>
        private const float SnapTurnDegrees = 20f;
        private const float GravityStrength = 0.12f;

        [Tooltip("1.1 adds Max Squish and Stretch Motion. Keep 1.0 for Motion Bones tuned before that.")]
        public Version version = Version.Version_1_1;

        // ---- Transforms ---------------------------------------------------------------------
        [Tooltip("Where the chain starts (empty = this object). Everything below it moves.")]
        public Transform rootTransform;
        [Tooltip("Bones (and everything under them) that stay out of the simulation, e.g. eyes under a head-rooted hair chain.")]
        public List<Transform> ignoreTransforms = new List<Transform>();
        [Tooltip("Adds a virtual bone past each end bone (in that bone's local space) so end bones rotate too.")]
        public Vector3 endpointPosition;
        [Tooltip("What a bone with several children does: Ignore leaves its rotation alone, First aims it at the first child, Average at all of them.")]
        public MultiChildType multiChildType = MultiChildType.Ignore;

        // ---- Forces -------------------------------------------------------------------------
        [Tooltip("Simplified: Pull and Spring only, easy to tune. Advanced: adds Stiffness and treats Spring as Momentum.")]
        public IntegrationType integrationType = IntegrationType.Simplified;
        [Range(0f, 1f), Tooltip("How strongly bones return to their rest pose.")]
        public float pull = 0.2f;
        public AnimationCurve pullCurve;
        [Range(0f, 1f), Tooltip("Simplified: how much bones wobble. Advanced: momentum.")]
        public float spring = 0.2f;
        public AnimationCurve springCurve;
        [Range(0f, 1f), Tooltip("Advanced only: how much bones keep their rest angle.")]
        public float stiffness = 0.2f;
        public AnimationCurve stiffnessCurve;
        [Range(-1f, 1f), Tooltip("Pulls bones down (positive) or up (negative), relative to their length.")]
        public float gravity;
        public AnimationCurve gravityCurve;
        [Range(0f, 1f), Tooltip("Less gravity for bones already hanging down at rest.")]
        public float gravityFalloff;
        public AnimationCurve gravityFalloffCurve;
        [Tooltip("All Motion: Immobile ignores any movement of the chain. World: only the avatar moving through the world (walking, flying), not its own animation.")]
        public ImmobileType immobileType = ImmobileType.AllMotion;
        [Range(0f, 1f), Tooltip("How much movement is ignored (1 = bones don't react to you moving).")]
        public float immobile;
        public AnimationCurve immobileCurve;

        // ---- Limits -------------------------------------------------------------------------
        [Tooltip("Angle: a cone around the rest direction. Hinge: swing in one plane only. Polar: a cone with separate X and Z widths.")]
        public LimitType limitType = LimitType.None;
        [Range(0f, 180f), Tooltip("How far (degrees) a bone may swing away from its rest direction.")]
        public float maxAngleX = 45f;
        public AnimationCurve maxAngleXCurve;
        [Range(0f, 90f), Tooltip("Polar only: how far (degrees) a bone may swing sideways.")]
        public float maxAngleZ = 45f;
        public AnimationCurve maxAngleZCurve;
        [Tooltip("Rotates the limit's axis / hinge plane.")]
        public Vector3 limitRotation;

        // ---- Collision ----------------------------------------------------------------------
        [Tooltip("Thickness of each bone for collisions and grabbing, in the root's scale (0 = a thin line).")]
        public float radius;
        public AnimationCurve radiusCurve;
        [Tooltip("Whose hand / finger colliders bump these bones. The Colliders list below always applies.")]
        public ParelDynamicsPermission allowCollision = ParelDynamicsPermission.Everyone;
        [Tooltip("Colliders on this avatar the bones can't pass through (body, head, legs...).")]
        public List<ParelMotionBoneCollider> colliders = new List<ParelMotionBoneCollider>();

        // ---- Stretch & Squish ---------------------------------------------------------------
        [Range(0f, 5f), Tooltip("How far bones may stretch, as a multiple of their length.")]
        public float maxStretch;
        public AnimationCurve maxStretchCurve;
        [Range(0f, 1f), Tooltip("How far bones may squish, as a fraction of their length.")]
        public float maxSquish;
        public AnimationCurve maxSquishCurve;
        [Range(0f, 1f), Tooltip("How much movement and gravity stretch / squish the bones (0 = only grabbing does).")]
        public float stretchMotion;
        public AnimationCurve stretchMotionCurve;

        // ---- Grab & Pose --------------------------------------------------------------------
        [Tooltip("Whose hands may grab these bones.")]
        public ParelDynamicsPermission allowGrabbing = ParelDynamicsPermission.Everyone;
        [Tooltip("Whose hands may leave the bones posed when letting go.")]
        public ParelDynamicsPermission allowPosing = ParelDynamicsPermission.Everyone;
        [Range(0f, 1f), Tooltip("How quickly a grabbed bone follows the hand.")]
        public float grabMovement = 0.5f;
        [Tooltip("Grabbed bones jump to the hand instead of keeping their offset.")]
        public bool snapToHand;

        // ---- Options ------------------------------------------------------------------------
        [Tooltip("Base name for {name}_IsGrabbed, _IsPosed, _Angle, _Stretch and _Squish (empty = none).")]
        public string parameter = string.Empty;
        [Tooltip("Tick if an animation moves these bones (they aren't reset each frame).")]
        public bool isAnimated;
        [Tooltip("Put the bones back in their original pose when this component is disabled.")]
        public bool resetWhenDisabled;

        // ---- Gizmos -------------------------------------------------------------------------
        [Tooltip("Draw the chain, radii and limits in the Scene view while selected.")]
        public bool showGizmos = true;
        [Range(0f, 1f)] public float boneOpacity = 0.5f;
        [Range(0f, 1f)] public float limitOpacity = 0.5f;

        /// <summary>
        /// Extra world-space push for scripts (wind, fans, interactions), in Gravity units: a length of 1
        /// is as strong as Gravity 1 on this chain. Not saved and not networked.
        /// </summary>
        [System.NonSerialized] public Vector3 ExternalForce;

        private struct Particle
        {
            public Transform Transform;
            public int Parent;
            public bool IsVirtual;
            public Vector3 EndOffset;
            public Vector3 InitLocalPosition;
            public Quaternion InitLocalRotation;
            public Vector3 Position;
            public Vector3 PrevPosition;
            public Vector3 AnimPosition;
            public Quaternion AnimRotation;
            public float RestLength;
            public float Depth01;
            public float Radius;
            public Vector3 PosedLocal;
        }

        private Particle[] _particles = new Particle[0];
        private List<int>[] _children = new List<int>[0];
        private int _count;
        private bool _built;
        private bool _needsReset = true;
        private float _lastStep = StepSeconds;
        private Vector3 _lastMovePosition;
        private Quaternion _lastAnchorRotation = Quaternion.identity;
        private bool _hasLastMove;
        private Transform _ownerRoot;
        private bool _ownerResolved;
        private IParelAvatarParameterSink _sink;
        private bool _sinkResolved;
        private Bounds _bounds;
        private bool _suspended;
        private bool _faulted;
        private int _faults;

        // Grab / pose state.
        private int _grabIndex = -1;
        private Vector3 _grabTarget;
        private bool _posed;

        private bool _lastGrabbed;
        private bool _lastPosed;
        private float _lastAngle = -1f;
        private float _lastStretch = -1f;
        private float _lastSquish = -1f;

        public Transform Root => rootTransform != null ? rootTransform : transform;
        public int ParticleCount => _count;
        public bool IsGrabbed => _grabIndex >= 0;
        public bool IsPosed => _posed;
        /// <summary>Paused this frame: over the simulation budget, out of range, or stopped after errors.</summary>
        public bool IsSuspended => _suspended || _faulted;

        /// <summary>The avatar root this Motion Bone belongs to (null for world Motion Bones).</summary>
        public Transform OwnerRoot
        {
            get
            {
                if (!_ownerResolved)
                {
                    _ownerResolved = true;
                    ParelAvatarDescriptor descriptor = GetComponentInParent<ParelAvatarDescriptor>();
                    _ownerRoot = descriptor != null ? descriptor.transform : null;
                }
                return _ownerRoot;
            }
        }

        internal Bounds WorldBounds => _bounds;

        private void OnEnable()
        {
            _needsReset = true;
            _ownerResolved = false;
            _suspended = false;
            _faulted = false;
            _faults = 0;
            ParelMotionBoneSystem.Register(this);
        }

        private void OnDisable()
        {
            ParelMotionBoneSystem.Unregister(this);
            ReleaseGrab(false);
            if (resetWhenDisabled) RestoreInitialPose();
        }

        private void OnTransformParentChanged()
        {
            _ownerResolved = false;
            _needsReset = true;
        }

        /// <summary>Re-reads the bone hierarchy (call after adding / removing bones).</summary>
        public void Rebuild()
        {
            MarkForRebuild();
            _faulted = false;
            _faults = 0;
        }

        /// <summary>Drops all motion and starts again from the animated pose (call after teleporting an avatar).</summary>
        public void ResetSimulation() => _needsReset = true;

        private void MarkForRebuild()
        {
            _built = false;
            _needsReset = true;
        }

        /// <summary>
        /// Fills the force, limit and stretch settings with a starting point for <paramref name="preset"/>
        /// and clears their curves. Transforms, radius, colliders, permissions, grabbing and the parameter
        /// are left alone.
        /// </summary>
        public void ApplyPreset(ParelMotionBonePreset preset)
        {
            version = Version.Version_1_1;
            integrationType = IntegrationType.Advanced;
            immobileType = ImmobileType.AllMotion;
            limitType = LimitType.Angle;
            limitRotation = Vector3.zero;
            stiffness = 0f;
            gravityFalloff = 0f;
            immobile = 0f;
            maxAngleZ = 45f;
            maxStretch = 0f;
            maxSquish = 0f;
            stretchMotion = 0f;

            switch (preset)
            {
                case ParelMotionBonePreset.Hair:
                    pull = 0.2f; spring = 0.25f; stiffness = 0.2f; gravity = 0.1f; gravityFalloff = 0.6f; immobile = 0.3f; maxAngleX = 60f;
                    break;
                case ParelMotionBonePreset.LongHair:
                    pull = 0.12f; spring = 0.35f; stiffness = 0.1f; gravity = 0.25f; gravityFalloff = 0.5f; immobile = 0.35f; maxAngleX = 75f;
                    break;
                case ParelMotionBonePreset.Tail:
                    pull = 0.15f; spring = 0.45f; stiffness = 0.15f; gravity = 0.1f; gravityFalloff = 0.3f; maxAngleX = 90f;
                    break;
                case ParelMotionBonePreset.Ears:
                    pull = 0.5f; spring = 0.3f; stiffness = 0.6f; gravity = 0f; immobile = 0.5f; maxAngleX = 25f;
                    break;
                case ParelMotionBonePreset.Clothing:
                    pull = 0.3f; spring = 0.15f; stiffness = 0.3f; gravity = 0.2f; gravityFalloff = 0.8f; immobile = 0.6f; maxAngleX = 45f;
                    break;
                case ParelMotionBonePreset.Accessory:
                    integrationType = IntegrationType.Simplified;
                    limitType = LimitType.None;
                    pull = 0.1f; spring = 0.6f; gravity = 0.4f; maxAngleX = 45f;
                    break;
                case ParelMotionBonePreset.SoftBody:
                    pull = 0.4f; spring = 0.6f; stiffness = 0.5f; gravity = 0.05f; immobile = 0.2f; maxAngleX = 20f;
                    maxStretch = 0.2f; maxSquish = 0.1f; stretchMotion = 0.5f;
                    break;
            }

            pullCurve = new AnimationCurve();
            springCurve = new AnimationCurve();
            stiffnessCurve = new AnimationCurve();
            gravityCurve = new AnimationCurve();
            gravityFalloffCurve = new AnimationCurve();
            immobileCurve = new AnimationCurve();
            maxAngleXCurve = new AnimationCurve();
            maxAngleZCurve = new AnimationCurve();
            maxStretchCurve = new AnimationCurve();
            maxSquishCurve = new AnimationCurve();
            stretchMotionCurve = new AnimationCurve();
        }

        private void Build()
        {
            _built = true;
            _count = 0;
            _grabIndex = -1;
            _posed = false;
            var particles = new List<Particle>();
            var depths = new List<int>();
            var ignored = new HashSet<Transform>();
            if (ignoreTransforms != null)
            {
                foreach (Transform t in ignoreTransforms)
                {
                    if (t != null) ignored.Add(t);
                }
            }

            AddParticle(Root, -1, 0, particles, depths, ignored);

            int maxDepth = 1;
            foreach (int d in depths) maxDepth = Mathf.Max(maxDepth, d);

            _count = particles.Count;
            _particles = particles.ToArray();
            _children = new List<int>[_count];
            for (int i = 0; i < _count; i++)
            {
                _particles[i].Depth01 = (float)depths[i] / maxDepth;
                _children[i] = new List<int>();
            }
            for (int i = 1; i < _count; i++) _children[_particles[i].Parent].Add(i);
        }

        private void AddParticle(Transform t, int parent, int depth, List<Particle> particles, List<int> depths, HashSet<Transform> ignored)
        {
            if (particles.Count >= MaxParticles) return;
            int index = particles.Count;
            particles.Add(new Particle
            {
                Transform = t,
                Parent = parent,
                InitLocalPosition = t.localPosition,
                InitLocalRotation = t.localRotation,
            });
            depths.Add(depth);

            int added = 0;
            for (int c = 0; c < t.childCount; c++)
            {
                Transform child = t.GetChild(c);
                if (ignored.Contains(child)) continue;
                if (particles.Count >= MaxParticles) break;
                AddParticle(child, index, depth + 1, particles, depths, ignored);
                added++;
            }

            if (added == 0 && endpointPosition != Vector3.zero && particles.Count < MaxParticles)
            {
                particles.Add(new Particle { Transform = null, Parent = index, IsVirtual = true, EndOffset = endpointPosition });
                depths.Add(depth + 1);
            }
        }

        private void RestoreInitialPose()
        {
            for (int i = 0; i < _count; i++)
            {
                if (_particles[i].IsVirtual || _particles[i].Transform == null) continue;
                _particles[i].Transform.localPosition = _particles[i].InitLocalPosition;
                _particles[i].Transform.localRotation = _particles[i].InitLocalRotation;
            }
        }

        // =========================================================================================
        // Simulation (driven by ParelMotionBoneSystem once per frame, after animation and IK)
        // =========================================================================================

        /// <summary>Reads this frame's animated pose. False when there is nothing to simulate this frame.</summary>
        internal bool PrepareFrame()
        {
            if (_faulted) return false;
            _suspended = false;
            if (!_built) Build();
            if (_count < 2) return false;

            if (!isAnimated) RestoreInitialPose();

            float scale = WorldScale();
            for (int i = 0; i < _count; i++)
            {
                ref Particle p = ref _particles[i];
                Transform source = p.IsVirtual ? _particles[p.Parent].Transform : p.Transform;
                if (source == null)
                {
                    // A bone was destroyed (outfit toggles, avatar edits): re-read the hierarchy next frame.
                    MarkForRebuild();
                    return false;
                }
                if (p.IsVirtual)
                {
                    p.AnimPosition = source.TransformPoint(p.EndOffset);
                    p.AnimRotation = source.rotation;
                }
                else
                {
                    p.AnimPosition = source.position;
                    p.AnimRotation = source.rotation;
                }
                p.RestLength = p.Parent >= 0 ? Vector3.Distance(p.AnimPosition, _particles[p.Parent].AnimPosition) : 0f;
                p.Radius = radius * Curve(radiusCurve, p.Depth01) * scale;
            }

            Transform owner = OwnerRoot;
            Vector3 moveAnchor = immobileType == ImmobileType.World && owner != null ? owner.position : _particles[0].AnimPosition;
            Quaternion anchorRotation = (owner != null ? owner : PoseSpace).rotation;
            Vector3 move = _hasLastMove ? moveAnchor - _lastMovePosition : Vector3.zero;
            float turn = _hasLastMove ? Quaternion.Angle(_lastAnchorRotation, anchorRotation) : 0f;
            Quaternion turnDelta = anchorRotation * Quaternion.Inverse(_lastAnchorRotation);
            _lastMovePosition = moveAnchor;
            _lastAnchorRotation = anchorRotation;
            _hasLastMove = true;

            // Scaled-up avatars cover more ground per frame; bone scale says nothing about that (some rigs use 100).
            float teleport = TeleportDistance * Mathf.Max(1f, owner != null ? MaxAbs(owner.lossyScale) : 1f);
            if (_needsReset || move.sqrMagnitude > teleport * teleport)
            {
                ResetParticles();
            }
            else if (turn > SnapTurnDegrees)
            {
                // Snap turn: swing the whole chain round with the avatar, keeping its motion.
                Vector3 pivot = _particles[0].Position;
                Vector3 target = _particles[0].AnimPosition;
                for (int i = 0; i < _count; i++)
                {
                    ref Particle p = ref _particles[i];
                    p.Position = target + turnDelta * (p.Position - pivot);
                    p.PrevPosition = target + turnDelta * (p.PrevPosition - pivot);
                }
            }
            else if (move != Vector3.zero)
            {
                // Immobile: carry the particles along with the avatar so its own movement isn't felt.
                for (int i = 1; i < _count; i++)
                {
                    ref Particle p = ref _particles[i];
                    float amount = immobile * Curve(immobileCurve, p.Depth01);
                    if (amount <= 0f) continue;
                    Vector3 shift = move * amount;
                    p.Position += shift;
                    p.PrevPosition += shift;
                }
            }

            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            for (int i = 0; i < _count; i++)
            {
                Vector3 pos = _particles[i].Position;
                float r = _particles[i].Radius;
                min = Vector3.Min(min, pos - Vector3.one * r);
                max = Vector3.Max(max, pos + Vector3.one * r);
            }
            _bounds = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }

        /// <summary>Skipped this frame (budget / distance): rest in the original pose and start fresh when resumed.</summary>
        internal void Suspend()
        {
            if (_suspended) return;
            _suspended = true;
            _needsReset = true;
            ReleaseGrab(false);
            if (!isAnimated) RestoreInitialPose();
        }

        /// <summary>Called when this Motion Bone threw. True once it has failed often enough to be stopped.</summary>
        internal bool Fault()
        {
            MarkForRebuild();
            _faulted = ++_faults >= 3;
            return _faulted;
        }

        private void ResetParticles()
        {
            _needsReset = false;
            _lastStep = StepSeconds;
            for (int i = 0; i < _count; i++)
            {
                _particles[i].Position = _particles[i].AnimPosition;
                _particles[i].PrevPosition = _particles[i].AnimPosition;
            }
        }

        private float WorldScale() => MaxAbs(Root.lossyScale);

        private static float MaxAbs(Vector3 s) => Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));

        /// <param name="frameColliders">Every collider that applies to this chain this frame (already filtered).</param>
        internal void Simulate(float deltaTime, List<ParelMotionBoneCollider> frameColliders)
        {
            if (_count < 2 || deltaTime <= 0f) return;

            // Frames at 90 Hz or faster take one step of the frame's length; slower ones are split into
            // up to MaxStepsPerFrame steps. A longer hitch plays out as slow motion, not one huge step.
            float frame = Mathf.Min(deltaTime, StepSeconds * MaxStepsPerFrame);
            int steps = Mathf.Clamp(Mathf.CeilToInt(frame / StepSeconds - 0.1f), 1, MaxStepsPerFrame);
            float h = frame / steps;
            Vector3 rootFrom = _particles[0].Position;
            for (int s = 1; s <= steps; s++) Step(h, rootFrom, (float)s / steps, frameColliders);

            if (!ParticlesAreFinite())
            {
                // Bad input (a NaN setting, a degenerate collider...): start over from the animated pose.
                ResetParticles();
                return;
            }

            _faults = 0;
            ApplyToTransforms();
            WriteParameters();
        }

        private bool ParticlesAreFinite()
        {
            for (int i = 0; i < _count; i++)
            {
                Vector3 p = _particles[i].Position;
                float sum = p.x + p.y + p.z;
                if (float.IsNaN(sum) || float.IsInfinity(sum)) return false;
            }
            return true;
        }

        private void Step(float h, Vector3 rootFrom, float t, List<ParelMotionBoneCollider> frameColliders)
        {
            // The root bone is pinned to wherever animation / IK put it, eased there across the frame's steps.
            ref Particle root = ref _particles[0];
            root.PrevPosition = root.Position;
            root.Position = Vector3.LerpUnclamped(rootFrom, root.AnimPosition, t);

            float frameScale = h * 60f;
            // Momentum is tuned per 90 Hz step; raising it to the step ratio keeps the same decay per second,
            // and scaling by the step change keeps the velocity when the step length changes.
            float stepRatio = h / StepSeconds;
            float carry = h / _lastStep;
            _lastStep = h;
            float simplified = integrationType == IntegrationType.Simplified ? 0.95f : 1f;
            bool momentumCurve = springCurve != null && springCurve.length > 0;
            float baseMomentum = Mathf.Pow(Mathf.Clamp01(spring) * simplified, stepRatio);
            bool pushed = ExternalForce != Vector3.zero;
            Transform poseSpace = PoseSpace;

            for (int i = 1; i < _count; i++)
            {
                ref Particle p = ref _particles[i];
                ref Particle parent = ref _particles[p.Parent];
                float depth = p.Depth01;

                Vector3 animOffset = p.AnimPosition - parent.AnimPosition;
                Vector3 restPosition = _posed ? poseSpace.TransformPoint(p.PosedLocal) : parent.Position + animOffset;

                // Momentum (verlet).
                float momentum = momentumCurve ? Mathf.Pow(Mathf.Clamp01(spring * Curve(springCurve, depth)) * simplified, stepRatio) : baseMomentum;
                Vector3 velocity = (p.Position - p.PrevPosition) * (momentum * carry);
                p.PrevPosition = p.Position;
                p.Position += velocity;

                // Gravity, faded out for bones that already hang along it at rest.
                float g = gravity * Curve(gravityCurve, depth);
                if (g != 0f && p.RestLength > 1e-6f)
                {
                    float hanging = Mathf.Max(0f, Vector3.Dot(animOffset / p.RestLength, g > 0f ? Vector3.down : Vector3.up));
                    float falloff = gravityFalloff * Curve(gravityFalloffCurve, depth);
                    g *= 1f - falloff * hanging;
                    p.Position += Vector3.down * (g * p.RestLength * GravityStrength * frameScale);
                }
                if (pushed) p.Position += ExternalForce * (p.RestLength * GravityStrength * frameScale);

                // Pull back toward the rest (or posed) position.
                float pullAmount = _posed ? 1f : Mathf.Clamp01(pull * Curve(pullCurve, depth));
                float pullStep = 1f - Mathf.Pow(1f - pullAmount, frameScale);
                p.Position += (restPosition - p.Position) * pullStep;

                // Stiffness (Advanced): don't stray far from the rest angle.
                if (integrationType == IntegrationType.Advanced)
                {
                    float stiff = Mathf.Clamp01(stiffness * Curve(stiffnessCurve, depth));
                    if (stiff > 0f)
                    {
                        float maxDeviation = p.RestLength * (1f - stiff) * 2f;
                        Vector3 deviation = p.Position - restPosition;
                        float d = deviation.magnitude;
                        if (d > maxDeviation && d > 1e-6f) p.Position = restPosition + deviation * (maxDeviation / d);
                    }
                }

                ApplyLimit(ref p, ref parent, animOffset);
                ApplyLength(ref p, ref parent, animOffset);
                if (frameColliders != null && frameColliders.Count > 0)
                {
                    Collide(ref p, ref parent, frameColliders);
                    ApplyLength(ref p, ref parent, animOffset);
                }
            }

            if (_grabIndex > 0 && _grabIndex < _count) ApplyGrab();
        }

        private void ApplyLimit(ref Particle p, ref Particle parent, Vector3 animOffset)
        {
            if (limitType == LimitType.None || p.RestLength <= 1e-6f) return;

            Vector3 dir = p.Position - parent.Position;
            float length = dir.magnitude;
            if (length <= 1e-6f) return;
            dir /= length;

            Quaternion frame = parent.AnimRotation * Quaternion.Euler(limitRotation);
            Vector3 axis = animOffset / p.RestLength;
            float maxX = maxAngleX * Curve(maxAngleXCurve, p.Depth01);

            switch (limitType)
            {
                case LimitType.Angle:
                {
                    float angle = Vector3.Angle(axis, dir);
                    if (angle > maxX) dir = Vector3.Slerp(axis, dir, maxX / angle);
                    break;
                }
                case LimitType.Hinge:
                {
                    Vector3 hingeNormal = frame * Vector3.right;
                    Vector3 planar = Vector3.ProjectOnPlane(dir, hingeNormal);
                    if (planar.sqrMagnitude < 1e-8f) planar = axis;
                    planar.Normalize();
                    float angle = Vector3.Angle(axis, planar);
                    dir = angle > maxX ? Vector3.Slerp(axis, planar, maxX / angle) : planar;
                    break;
                }
                case LimitType.Polar:
                {
                    float maxZ = maxAngleZ * Curve(maxAngleZCurve, p.Depth01);
                    Vector3 xAxis = Vector3.ProjectOnPlane(frame * Vector3.right, axis).normalized;
                    if (xAxis.sqrMagnitude < 1e-8f) break;
                    Vector3 zAxis = Vector3.Cross(xAxis, axis);
                    float ly = Vector3.Dot(dir, axis);
                    float ax = Mathf.Atan2(Vector3.Dot(dir, xAxis), ly) * Mathf.Rad2Deg;
                    float az = Mathf.Atan2(Vector3.Dot(dir, zAxis), ly) * Mathf.Rad2Deg;
                    float rx = maxX > 0.01f ? ax / maxX : 0f;
                    float rz = maxZ > 0.01f ? az / maxZ : 0f;
                    float e = rx * rx + rz * rz;
                    if (e > 1f)
                    {
                        float k = 1f / Mathf.Sqrt(e);
                        ax *= k;
                        az *= k;
                        Vector3 rebuilt = axis + xAxis * Mathf.Tan(ax * Mathf.Deg2Rad) + zAxis * Mathf.Tan(az * Mathf.Deg2Rad);
                        dir = rebuilt.normalized;
                    }
                    break;
                }
            }

            p.Position = parent.Position + dir * length;
        }

        private void ApplyLength(ref Particle p, ref Particle parent, Vector3 animOffset)
        {
            Vector3 delta = p.Position - parent.Position;
            float length = delta.magnitude;
            float rest = p.RestLength;
            if (rest <= 1e-6f)
            {
                p.Position = parent.Position;
                return;
            }
            if (length <= 1e-6f)
            {
                delta = animOffset;
                length = rest;
            }

            float stretch = maxStretch * Curve(maxStretchCurve, p.Depth01);
            float squish = SquishAt(p.Depth01);
            // 1.0: motion stretches freely up to Max Stretch. 1.1: only as much as Stretch Motion allows.
            float motion = version == Version.Version_1_1 ? Mathf.Clamp01(stretchMotion * Curve(stretchMotionCurve, p.Depth01)) : 1f;
            float target = Mathf.Clamp(length, rest * (1f - squish * motion), rest * (1f + stretch * motion));
            p.Position = parent.Position + delta * (target / length);
        }

        private float SquishAt(float depth) => version == Version.Version_1_1 ? maxSquish * Curve(maxSquishCurve, depth) : 0f;

        private static void Collide(ref Particle p, ref Particle parent, List<ParelMotionBoneCollider> frameColliders)
        {
            for (int c = 0; c < frameColliders.Count; c++) CollideBone(frameColliders[c], ref p, ref parent);
        }

        /// <summary>
        /// Pushes a bone out of (or into) a collider. Bones are spheres at their tip, or -- unless the
        /// collider has Bones As Spheres -- capsules from their parent to their tip, approximated by
        /// also testing the bone's midpoint and moving the tip twice as far.
        /// </summary>
        private static void CollideBone(ParelMotionBoneCollider collider, ref Particle p, ref Particle parent)
        {
            collider.Collide(ref p.Position, p.Radius);
            if (collider.bonesAsSpheres) return;
            Vector3 mid = (parent.Position + p.Position) * 0.5f;
            Vector3 before = mid;
            collider.Collide(ref mid, Mathf.Max(p.Radius, parent.Radius));
            Vector3 push = mid - before;
            if (push.sqrMagnitude > 1e-12f) p.Position += push * 2f;
        }

        // =========================================================================================
        // Grab & pose
        // =========================================================================================

        private Transform PoseSpace => Root.parent != null ? Root.parent : Root;

        /// <summary>Nearest grabbable particle to <paramref name="point"/> within <paramref name="reach"/>, or -1.</summary>
        internal int FindGrabbable(Vector3 point, float reach, out float distance)
        {
            distance = float.MaxValue;
            int best = -1;
            for (int i = 1; i < _count; i++)
            {
                float d = Vector3.Distance(_particles[i].Position, point) - _particles[i].Radius;
                if (d <= reach && d < distance)
                {
                    distance = d;
                    best = i;
                }
            }
            return best;
        }

        internal Vector3 ParticlePosition(int index) => _particles[index].Position;

        internal void BeginGrab(int particle)
        {
            _grabIndex = particle;
            _grabTarget = _particles[particle].Position;
            _posed = false;
        }

        internal void SetGrabTarget(Vector3 target) => _grabTarget = target;

        internal void ReleaseGrab(bool pose)
        {
            if (_grabIndex < 0) return;
            _grabIndex = -1;
            if (pose)
            {
                Transform space = PoseSpace;
                for (int i = 0; i < _count; i++) _particles[i].PosedLocal = space.InverseTransformPoint(_particles[i].Position);
                _posed = true;
            }
        }

        /// <summary>Lets posed bones fall back to normal physics.</summary>
        public void ClearPose() => _posed = false;

        private void ApplyGrab()
        {
            float follow = Mathf.Lerp(0.08f, 1f, grabMovement);
            ref Particle grabbed = ref _particles[_grabIndex];
            grabbed.Position = Vector3.Lerp(grabbed.Position, _grabTarget, follow);
            grabbed.PrevPosition = grabbed.Position;

            // FABRIK along the path root -> grabbed bone so the whole chain bends toward the hand.
            var path = ParelMotionBoneSystem.PathBuffer;
            path.Clear();
            for (int k = _grabIndex; k > 0; k = _particles[k].Parent) path.Add(k);

            for (int n = 0; n < path.Count - 1; n++)
            {
                int child = path[n];
                int parent = path[n + 1];
                Vector3 direction = _particles[parent].Position - _particles[child].Position;
                float length = direction.magnitude;
                if (length > 1e-6f) _particles[parent].Position = _particles[child].Position + direction / length * _particles[child].RestLength;
            }

            _particles[0].Position = _particles[0].AnimPosition;
            for (int n = path.Count - 1; n >= 0; n--)
            {
                int index = path[n];
                ref Particle p = ref _particles[index];
                Vector3 parentPosition = _particles[p.Parent].Position;
                Vector3 direction = p.Position - parentPosition;
                float length = direction.magnitude;
                float stretch = maxStretch * Curve(maxStretchCurve, p.Depth01);
                float target = Mathf.Clamp(length, p.RestLength * (1f - SquishAt(p.Depth01)), p.RestLength * (1f + stretch));
                p.Position = length > 1e-6f ? parentPosition + direction * (target / length) : parentPosition;
                p.PrevPosition = p.Position;
            }
        }

        // =========================================================================================
        // Output
        // =========================================================================================

        private void ApplyToTransforms()
        {
            bool stretchy = maxStretch > 0f || maxSquish > 0f;
            for (int i = 0; i < _count; i++)
            {
                ref Particle p = ref _particles[i];
                if (p.IsVirtual || p.Transform == null) continue;

                if (i > 0 && stretchy) p.Transform.position = p.Position;

                List<int> children = _children[i];
                if (children.Count == 0) continue;
                if (children.Count > 1 && multiChildType == MultiChildType.Ignore) continue;

                Vector3 from = Vector3.zero;
                Vector3 to = Vector3.zero;
                int used = multiChildType == MultiChildType.First ? 1 : children.Count;
                Vector3 origin = p.Transform.position;
                for (int c = 0; c < used; c++)
                {
                    ref Particle child = ref _particles[children[c]];
                    if (!child.IsVirtual && child.Transform == null) continue;
                    Vector3 childNow = child.IsVirtual ? p.Transform.TransformPoint(child.EndOffset) : child.Transform.position;
                    from += childNow - origin;
                    to += child.Position - origin;
                }

                if (from.sqrMagnitude < 1e-12f || to.sqrMagnitude < 1e-12f) continue;
                p.Transform.rotation = Quaternion.FromToRotation(from, to) * p.Transform.rotation;
            }
        }

        private void WriteParameters()
        {
            if (string.IsNullOrEmpty(parameter) || _count < 2) return;
            if (!_sinkResolved)
            {
                _sinkResolved = true;
                _sink = ParelAvatarSinks.Find(this);
            }
            if (_sink == null) return;

            bool grabbed = _grabIndex >= 0;
            if (grabbed != _lastGrabbed)
            {
                _lastGrabbed = grabbed;
                _sink.SetParameter(parameter + "_IsGrabbed", grabbed ? 1f : 0f, ParelParameterSource.MotionBone);
            }
            if (_posed != _lastPosed)
            {
                _lastPosed = _posed;
                _sink.SetParameter(parameter + "_IsPosed", _posed ? 1f : 0f, ParelParameterSource.MotionBone);
            }

            ref Particle first = ref _particles[1];
            ref Particle root = ref _particles[0];
            Vector3 now = first.Position - root.Position;
            Vector3 rest = first.AnimPosition - root.AnimPosition;
            float angle = now.sqrMagnitude > 1e-12f && rest.sqrMagnitude > 1e-12f ? Vector3.Angle(rest, now) / 180f : 0f;
            float length = now.magnitude;
            float stretch = maxStretch > 0f && first.RestLength > 1e-6f ? Mathf.Clamp01((length - first.RestLength) / (first.RestLength * maxStretch)) : 0f;
            float squish = maxSquish > 0f && first.RestLength > 1e-6f ? Mathf.Clamp01((first.RestLength - length) / (first.RestLength * maxSquish)) : 0f;

            if (Mathf.Abs(angle - _lastAngle) > 0.002f)
            {
                _lastAngle = angle;
                _sink.SetParameter(parameter + "_Angle", angle, ParelParameterSource.MotionBone);
            }
            if (Mathf.Abs(stretch - _lastStretch) > 0.002f)
            {
                _lastStretch = stretch;
                _sink.SetParameter(parameter + "_Stretch", stretch, ParelParameterSource.MotionBone);
            }
            if (Mathf.Abs(squish - _lastSquish) > 0.002f)
            {
                _lastSquish = squish;
                _sink.SetParameter(parameter + "_Squish", squish, ParelParameterSource.MotionBone);
            }
        }

        private static float Curve(AnimationCurve curve, float t)
        {
            return curve != null && curve.length > 0 ? curve.Evaluate(t) : 1f;
        }

        private void OnDrawGizmosSelected()
        {
            if (!showGizmos) return;
            Gizmos.color = new Color(1f, 0.85f, 0.2f, Mathf.Clamp01(boneOpacity) * 0.6f + 0.4f);
            if (Application.isPlaying && _count > 0)
            {
                if (IsSuspended) Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.5f);
                for (int i = 1; i < _count; i++)
                {
                    Gizmos.DrawLine(_particles[_particles[i].Parent].Position, _particles[i].Position);
                    if (_particles[i].Radius > 0f) Gizmos.DrawWireSphere(_particles[i].Position, _particles[i].Radius);
                }
                return;
            }

            DrawEditorChain(Root, 0);
        }

        private void DrawEditorChain(Transform t, int depth)
        {
            if (t == null || depth > 64) return;
            for (int c = 0; c < t.childCount; c++)
            {
                Transform child = t.GetChild(c);
                if (ignoreTransforms != null && ignoreTransforms.Contains(child)) continue;
                Gizmos.DrawLine(t.position, child.position);
                if (radius > 0f) Gizmos.DrawWireSphere(child.position, radius * Curve(radiusCurve, Mathf.Clamp01(depth / 8f)) * Mathf.Max(0.0001f, child.lossyScale.x));
                if (limitType != LimitType.None && limitOpacity > 0f) DrawLimit(t, child);
                DrawEditorChain(child, depth + 1);
            }
            if (t.childCount == 0 && endpointPosition != Vector3.zero) Gizmos.DrawLine(t.position, t.TransformPoint(endpointPosition));
        }

        /// <summary>Draws the limit cone (Angle / Polar) or fan (Hinge) for one bone in the Scene view.</summary>
        private void DrawLimit(Transform parent, Transform child)
        {
            Vector3 axis = child.position - parent.position;
            float length = axis.magnitude;
            if (length < 1e-5f) return;
            axis /= length;
            Color previous = Gizmos.color;
            Gizmos.color = new Color(0.4f, 0.75f, 1f, Mathf.Clamp01(limitOpacity));
            Quaternion frame = parent.rotation * Quaternion.Euler(limitRotation);
            Vector3 side = Vector3.ProjectOnPlane(frame * Vector3.right, axis).normalized;
            if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(axis, Vector3.up).normalized;
            Vector3 other = Vector3.Cross(axis, side);
            const int segments = 16;
            Vector3 last = Vector3.zero;
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments * Mathf.PI * 2f;
                float angleX = maxAngleX;
                float angleZ = limitType == LimitType.Polar ? maxAngleZ : maxAngleX;
                Vector3 dir;
                if (limitType == LimitType.Hinge)
                {
                    float a = Mathf.Lerp(-angleX, angleX, (float)i / segments) * Mathf.Deg2Rad;
                    dir = axis * Mathf.Cos(a) + other * Mathf.Sin(a);
                }
                else
                {
                    float ax = Mathf.Cos(t) * angleX * Mathf.Deg2Rad;
                    float az = Mathf.Sin(t) * angleZ * Mathf.Deg2Rad;
                    dir = (axis + side * Mathf.Tan(Mathf.Clamp(ax, -1.4f, 1.4f)) + other * Mathf.Tan(Mathf.Clamp(az, -1.4f, 1.4f))).normalized;
                }
                Vector3 point = parent.position + dir * length * 0.6f;
                if (i > 0) Gizmos.DrawLine(last, point);
                if (i % 4 == 0) Gizmos.DrawLine(parent.position, point);
                last = point;
            }
            Gizmos.color = previous;
        }
    }
}
