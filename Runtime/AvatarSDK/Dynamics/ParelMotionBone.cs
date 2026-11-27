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
        Jiggle = 7,
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
        /// <summary>
        /// The simulation runs at this fixed rate whatever the frame rate, and Pull, Spring / Momentum, Stiffness and
        /// Gravity mean what they say per step of it (the rate those settings came from). Frames between steps show the
        /// pose between the last two steps, so a Motion Bone moves the same in the editor, at 72 / 90 / 120 Hz and on a slow PC.
        /// </summary>
        private const float ReferenceRate = 60f;
        /// <summary>Most steps one frame takes (frames slower than 15 fps play out in slow motion).</summary>
        private const int MaxStepsPerFrame = 4;
        private const float TeleportDistance = 3f;
        /// <summary>A turn sharper than this in one frame (a snap turn) carries the chain round instead of whipping it.</summary>
        private const float SnapTurnDegrees = 20f;
        /// <summary>1.0 Gravity 1: the push per 60 Hz step, as a share of the bone's length.</summary>
        private const float GravityStrength = 0.12f;
        /// <summary>Limit None swings freely; this only stops a bone turning straight back through its parent, where its
        /// direction can't be told apart from the opposite one.</summary>
        private const float MaxSwingDegrees = 170f;

        [Tooltip("1.0: Gravity pushes the bones, Stiffness keeps them near their rest, motion never stretches them. 1.1: Gravity is how far the rest turns toward the ground (Pull takes the bones there), Stiffness keeps each bone in its last direction, and Max Squish / Stretch Motion are added.")]
        public Version version = Version.Version_1_1;

        // ---- Transforms ---------------------------------------------------------------------
        [Tooltip("Where the chain starts (empty = this object). Everything below it moves.")]
        public Transform rootTransform;
        [Tooltip("Bones (and everything under them) that stay out of the simulation, e.g. eyes under a head-rooted hair chain.")]
        public List<Transform> ignoreTransforms = new List<Transform>();
        [Tooltip("Leaves other Motion Bones below this one (and the bones they move) to themselves, so no bone is simulated twice. On by default; turning it off is unsupported.")]
        public bool ignoreOtherMotionBones = true;
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
        [Range(0f, 180f), Tooltip("How far (degrees) each bone may swing away from where it points at rest (Polar: the pitch, forward / back).")]
        public float maxAngleX = 45f;
        public AnimationCurve maxAngleXCurve;
        [Range(0f, 90f), Tooltip("Polar only: how far (degrees) a bone may swing sideways (the yaw). 0 = not at all.")]
        public float maxAngleZ = 45f;
        public AnimationCurve maxAngleZCurve;
        [Tooltip("Turns the limit's cone / hinge plane away from the rest direction: X pitch, Y roll, Z yaw (Y runs along the bone).")]
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

        /// <summary>A free bone swings; a pinned one sits on a parent that never turns (Multi-Child Ignore); a rigid one
        /// turns with its parent (the other children under First / Average). Only free bones are simulated.</summary>
        private const byte Free = 0, Pinned = 1, Rigid = 2;

        private struct Particle
        {
            public Transform Transform;
            public int Parent;
            public bool IsVirtual;
            public byte Mode;
            /// <summary>Last child of a Multi-Child Average bone: once it has moved, the whole group is lined up.</summary>
            public bool EndsAverage;
            /// <summary>This frame's limit frame for the bone ending here: Y = the cone's axis, Limit Rotation applied.</summary>
            public Quaternion LimitFrame;
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
            /// <summary>Straight down when the chain was built (standing normally), in the parent bone's space: Gravity
            /// Falloff compares the parent's turn since then with the ground.</summary>
            public Vector3 GravityLocal;
            /// <summary>Last frame's animated pose: the steps inside a frame ease from it to this frame's.</summary>
            public Vector3 PrevAnimPosition;
            public Quaternion PrevAnimRotation;
            /// <summary>Parent -> bone after the last step / the one before, in the parent's animated space: what 1.1
            /// Stiffness keeps, and the pose shown between steps.</summary>
            public Vector3 LocalOffset;
            public Vector3 PrevLocalOffset;
        }

        private Particle[] _particles = new Particle[0];
        private List<int>[] _children = new List<int>[0];
        /// <summary>Every bone but the root, parents first and each bone's children next to each other.</summary>
        private int[] _order = new int[0];
        /// <summary>How far each bone's transform turns this step (what ApplyToTransforms will do to it).</summary>
        private Quaternion[] _turn = new Quaternion[0];
        /// <summary>Each bone's animated rotation at the current step's moment.</summary>
        private Quaternion[] _stepRotation = new Quaternion[0];
        /// <summary>Where each bone is shown this frame (between the last two steps).</summary>
        private Vector3[] _shown = new Vector3[0];
        /// <summary>The first simulated bone, read for the _Angle / _Stretch / _Squish parameters.</summary>
        private int _paramIndex = 1;
        private int _count;
        private bool _built;
        /// <summary>The root is a humanoid bone (Head, Chest...): it keeps following animation / IK even without Is Animated.</summary>
        private bool _rootIsHumanoid;
        private bool _needsReset = true;
        /// <summary>Time since the last step (always under one step).</summary>
        private float _accumulator;
        /// <summary>Immobile's anchor last frame / this frame / at the last step.</summary>
        private Vector3 _prevFrameAnchor, _frameAnchor, _stepAnchor;
        private Quaternion _lastAnchorRotation = Quaternion.identity;
        private bool _hasLastMove;
        private Transform _ownerRoot;
        private bool _ownerResolved;
        private IParelAvatarParameterSink _sink;
        private bool _sinkResolved;
        private int _sinkGeneration;
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
            // Nothing turns the root back once this stops running, so it is put back too.
            if (resetWhenDisabled) RestoreInitialPose(true);
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
                case ParelMotionBonePreset.Jiggle:
                    // Loose and bouncy, held in a cone so hard pulls and sudden stops can't fold it over.
                    pull = 0.15f; spring = 0.5f; stiffness = 0.1f; gravity = 0f; maxAngleX = 35f;
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

            // Ignore Other Motion Bones: another Motion Bone starting below this one moves its own bones. Simulating them here
            // too made two solvers fight over the same transforms every frame (a head chain over the ears' own chain).
            Transform root = Root;
            if (ignoreOtherMotionBones)
            {
                Transform top = OwnerRoot != null ? OwnerRoot : root.root;
                foreach (ParelMotionBone other in top.GetComponentsInChildren<ParelMotionBone>(true))
                {
                    if (other == null || other == this) continue;
                    Transform otherRoot = other.Root;
                    if (otherRoot != root && otherRoot.IsChildOf(root)) ignored.Add(otherRoot);
                }
            }

            _rootIsHumanoid = false;
            Animator animator = OwnerRoot != null ? OwnerRoot.GetComponentInChildren<Animator>(true) : null;
            if (animator != null && animator.isHuman)
            {
                for (int b = 0; b < (int)HumanBodyBones.LastBone && !_rootIsHumanoid; b++)
                {
                    if (animator.GetBoneTransform((HumanBodyBones)b) == root) _rootIsHumanoid = true;
                }
            }

            AddParticle(root, -1, 0, particles, depths, ignored);

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
            for (int i = 1; i < _count; i++)
            {
                ref Particle p = ref _particles[i];
                _children[p.Parent].Add(i);
                Transform parentTransform = _particles[p.Parent].Transform;
                p.GravityLocal = parentTransform != null ? Quaternion.Inverse(parentTransform.rotation) * Vector3.down : Vector3.down;
            }

            // What the transforms can show decides what is simulated: a bone with several children that doesn't turn
            // (Ignore) can't move its children, so they are pinned where they sit; under First the other children turn
            // with the first one, under Average all of them turn together. Simulating them as free points let them
            // swing away from bones that never followed, so the ears hanging off them passed every limit.
            for (int i = 0; i < _count; i++)
            {
                List<int> kids = _children[i];
                if (kids.Count < 2) continue;
                switch (multiChildType)
                {
                    case MultiChildType.Ignore:
                        foreach (int k in kids) _particles[k].Mode = Pinned;
                        break;
                    case MultiChildType.First:
                        for (int k = 1; k < kids.Count; k++) _particles[kids[k]].Mode = Rigid;
                        break;
                    case MultiChildType.Average:
                        _particles[kids[kids.Count - 1]].EndsAverage = true;
                        break;
                }
            }

            _order = new int[Mathf.Max(0, _count - 1)];
            _turn = new Quaternion[_count];
            _stepRotation = new Quaternion[_count];
            _shown = new Vector3[_count];
            int head = 0, tail = 0;
            var queue = new int[_count];
            queue[tail++] = 0;
            while (head < tail)
            {
                int node = queue[head++];
                foreach (int k in _children[node])
                {
                    queue[tail++] = k;
                    _order[tail - 2] = k;
                }
            }

            _paramIndex = 1;
            for (int n = 0; n < _order.Length; n++)
            {
                if (_particles[_order[n]].Mode != Free) continue;
                _paramIndex = _order[n];
                break;
            }
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

        /// <summary>Puts the bones back in their original local pose. The root is the anchor animation / IK place, so it
        /// is only put back before animation runs, never after.</summary>
        private void RestoreInitialPose(bool includeRoot = false)
        {
            for (int i = includeRoot ? 0 : 1; i < _count; i++)
            {
                if (_particles[i].IsVirtual || _particles[i].Transform == null) continue;
                _particles[i].Transform.localPosition = _particles[i].InitLocalPosition;
                _particles[i].Transform.localRotation = _particles[i].InitLocalRotation;
            }
        }

        // =========================================================================================
        // Simulation (driven by ParelMotionBoneSystem once per frame, after animation and IK)
        // =========================================================================================

        /// <summary>
        /// Before animation runs: takes last frame's simulated pose off the bones, so animation (if any) lands on the
        /// original pose and Is Animated reads a real rest pose. Without this, Is Animated bones nothing animates read
        /// back their own swing as the rest pose every frame, so they drifted, never came back and ignored the limit.
        /// </summary>
        internal void BeforeAnimation()
        {
            if (_built && !_faulted && _count >= 2) RestoreInitialPose(true);
        }

        /// <summary>Reads this frame's animated pose. False when there is nothing to simulate this frame.</summary>
        internal bool PrepareFrame()
        {
            if (_faulted) return false;
            _suspended = false;
            if (!_built) Build();
            if (_count < 2) return false;

            // Not animated: the whole chain ignores animation, root included. A humanoid root (Head, Chest...) still follows
            // animation / IK: putting it back would freeze that part of the body.
            if (!isAnimated) RestoreInitialPose(!_rootIsHumanoid);

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
                // Last frame's pose: the steps that fall inside this frame ease from it to this one.
                p.PrevAnimPosition = p.AnimPosition;
                p.PrevAnimRotation = p.AnimRotation;
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
                if (p.Parent >= 0 && p.RestLength > 1e-6f)
                {
                    ref Particle parent = ref _particles[p.Parent];
                    p.LimitFrame = LimitFrameFor(parent.AnimRotation, (p.AnimPosition - parent.AnimPosition) / p.RestLength, limitRotation);
                }
            }

            // Immobile's anchor: the root (All Motion) or the avatar in the world (World). Each step carries its share of
            // the anchor's movement since the step before (see Step).
            Transform owner = OwnerRoot;
            Vector3 anchor = immobileType == ImmobileType.World && owner != null ? owner.position : _particles[0].AnimPosition;
            Quaternion anchorRotation = (owner != null ? owner : PoseSpace).rotation;
            Vector3 move = _hasLastMove ? anchor - _frameAnchor : Vector3.zero;
            float turn = _hasLastMove ? Quaternion.Angle(_lastAnchorRotation, anchorRotation) : 0f;
            Quaternion turnDelta = anchorRotation * Quaternion.Inverse(_lastAnchorRotation);
            _prevFrameAnchor = _hasLastMove ? _frameAnchor : anchor;
            _frameAnchor = anchor;
            _lastAnchorRotation = anchorRotation;
            if (!_hasLastMove) _stepAnchor = anchor;
            _hasLastMove = true;

            // Scaled-up avatars cover more ground per frame; bone scale says nothing about that (some rigs use 100).
            float teleport = TeleportDistance * Mathf.Max(1f, owner != null ? MaxAbs(owner.lossyScale) : 1f);
            if (_needsReset || move.sqrMagnitude > teleport * teleport)
            {
                ResetParticles();
            }
            else if (turn > SnapTurnDegrees)
            {
                // Snap turn: the whole chain swings round with the avatar at once, keeping its motion, and this frame's
                // steps start from the turned pose instead of easing round from the old one.
                Vector3 pivot = _particles[0].Position;
                Vector3 target = _particles[0].AnimPosition;
                for (int i = 0; i < _count; i++)
                {
                    ref Particle p = ref _particles[i];
                    p.Position = target + turnDelta * (p.Position - pivot);
                    p.PrevPosition = target + turnDelta * (p.PrevPosition - pivot);
                    p.PrevAnimPosition = p.AnimPosition;
                    p.PrevAnimRotation = p.AnimRotation;
                }
                _prevFrameAnchor = anchor;
                _stepAnchor = anchor;
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
            if (!isAnimated) RestoreInitialPose(!_rootIsHumanoid);
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
            _accumulator = 0f;
            _prevFrameAnchor = _frameAnchor;
            _stepAnchor = _frameAnchor;
            for (int i = 0; i < _count; i++)
            {
                ref Particle p = ref _particles[i];
                p.Position = p.AnimPosition;
                p.PrevPosition = p.AnimPosition;
                p.PrevAnimPosition = p.AnimPosition;
                p.PrevAnimRotation = p.AnimRotation;
                if (i == 0) continue;
                ref Particle parent = ref _particles[p.Parent];
                p.LocalOffset = Quaternion.Inverse(parent.AnimRotation) * (p.AnimPosition - parent.AnimPosition);
                p.PrevLocalOffset = p.LocalOffset;
            }
        }

        private float WorldScale() => MaxAbs(Root.lossyScale);

        /// <summary>
        /// The limit frame of a bone ending at a child: Y along the bone's rest direction (keeping the bone's own
        /// roll), then turned by Limit Rotation (X pitch, Y roll, Z yaw) so the cone / hinge / polar points there.
        /// </summary>
        internal static Quaternion LimitFrameFor(Quaternion boneRotation, Vector3 restDirection, Vector3 limitRotation)
        {
            Quaternion aligned = Quaternion.FromToRotation(boneRotation * Vector3.up, restDirection) * boneRotation;
            return limitRotation == Vector3.zero ? aligned : aligned * Quaternion.Euler(limitRotation);
        }

        private static float MaxAbs(Vector3 s) => Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));

        /// <param name="frameColliders">Every collider that applies to this chain this frame (already filtered).</param>
        internal void Simulate(float deltaTime, List<ParelMotionBoneCollider> frameColliders)
        {
            if (_count < 2 || deltaTime <= 0f) return;

            // Fixed 60 Hz steps whatever the frame rate, each exactly what the settings describe, so a Motion Bone moves the
            // same in a 60 fps or uncapped editor, on a 72 / 90 / 120 Hz headset and on a slow PC. (Steps that followed the
            // frame length changed how the body's movement dragged on the bones: the editor and the game never matched.)
            // A frame takes the steps that fall inside it; a long hitch plays out as slow motion, not one huge step.
            const float h = 1f / ReferenceRate;
            float frame = Mathf.Min(deltaTime, h * MaxStepsPerFrame);
            float start = _accumulator;
            _accumulator += frame;
            int steps = 0;
            while (_accumulator >= h && steps < MaxStepsPerFrame)
            {
                steps++;
                _accumulator -= h;
                // Where in this frame the step falls: 0 = last frame's pose, 1 = this frame's.
                Step(Mathf.Clamp01((steps * h - start) / frame), frameColliders);
            }
            _accumulator = Mathf.Clamp(_accumulator, 0f, h * 0.999f);

            if (!ParticlesAreFinite())
            {
                // Bad input (a NaN setting, a degenerate collider...): start over from the animated pose.
                ResetParticles();
                return;
            }

            _faults = 0;
            ApplyToTransforms(_accumulator / h);
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

        /// <summary>One 60 Hz step at moment <paramref name="u"/> of this frame (0 = last frame's pose, 1 = this frame's).</summary>
        private void Step(float u, List<ParelMotionBoneCollider> frameColliders)
        {
            // The animated pose at this step's moment.
            for (int i = 0; i < _count; i++) _stepRotation[i] = Quaternion.SlerpUnclamped(_particles[i].PrevAnimRotation, _particles[i].AnimRotation, u);

            // The root bone sits wherever animation / IK put it.
            ref Particle root = ref _particles[0];
            root.PrevPosition = root.Position;
            root.Position = Vector3.LerpUnclamped(root.PrevAnimPosition, root.AnimPosition, u);

            // Immobile: that share of the anchor's movement since the last step carries the bones along instead of
            // dragging on them (the position and the one before it move together, so the swing is kept).
            Vector3 anchor = Vector3.LerpUnclamped(_prevFrameAnchor, _frameAnchor, u);
            Vector3 move = anchor - _stepAnchor;
            _stepAnchor = anchor;
            bool carryMove = immobile > 0f && move != Vector3.zero;

            bool v11 = version == Version.Version_1_1;
            bool advanced = integrationType == IntegrationType.Advanced;
            bool pushed = ExternalForce != Vector3.zero;
            Transform poseSpace = PoseSpace;
            _turn[0] = Quaternion.identity;

            for (int n = 0; n < _order.Length; n++)
            {
                int i = _order[n];
                ref Particle p = ref _particles[i];
                ref Particle parent = ref _particles[p.Parent];
                float depth = p.Depth01;
                Quaternion parentRotation = _stepRotation[p.Parent];

                Vector3 animOffset = Vector3.LerpUnclamped(p.PrevAnimPosition - parent.PrevAnimPosition, p.AnimPosition - parent.AnimPosition, u);
                // How the parent's own parent turned: a parent that doesn't turn itself still carries that turn.
                Quaternion inherited = parent.Parent >= 0 ? _turn[parent.Parent] : Quaternion.identity;

                if (p.Mode == Pinned)
                {
                    // On a bone with several children that doesn't turn (Ignore): stays where that bone holds it.
                    _turn[p.Parent] = inherited;
                    Hold(ref p, parent.Position + inherited * animOffset);
                    continue;
                }
                if (p.Mode == Rigid)
                {
                    // Under First: turns with the parent, which turned toward its first child (moved just before).
                    Hold(ref p, parent.Position + _turn[p.Parent] * animOffset);
                    continue;
                }

                if (carryMove)
                {
                    float amount = Mathf.Clamp01(immobile * Curve(immobileCurve, depth));
                    if (amount > 0f)
                    {
                        Vector3 shift = move * amount;
                        p.Position += shift;
                        p.PrevPosition += shift;
                    }
                }

                Vector3 restPosition = _posed ? poseSpace.TransformPoint(p.PosedLocal) : parent.Position + animOffset;
                // Posed bones hold the pose.
                float pullAmount = _posed ? 1f : Mathf.Clamp01(pull * Curve(pullCurve, depth));
                float momentum = _posed ? 0f : Mathf.Clamp01(spring * Curve(springCurve, depth));

                // Gravity Falloff: that share of Gravity is taken off while the bone's rest is turned the way it was when the
                // chain was built (standing normally), and comes back as the rest turns away from that (head tilted, lying
                // down). So Falloff 1 leaves a modelled pose alone when standing, whichever way it points.
                Vector3 target = restPosition;
                float g = _posed ? 0f : gravity * Curve(gravityCurve, depth);
                Vector3 gravityDir = g > 0f ? Vector3.down : Vector3.up;
                float gravityShare = 0f;
                if (g != 0f && p.RestLength > 1e-6f)
                {
                    float asBuilt = Mathf.Max(0f, Vector3.Dot(parentRotation * p.GravityLocal, Vector3.down));
                    float falloff = Mathf.Clamp01(gravityFalloff * Curve(gravityFalloffCurve, depth));
                    gravityShare = Mathf.Abs(g) * (1f - falloff * asBuilt);
                    if (v11)
                    {
                        // 1.1: Gravity is how far toward the ground (up when negative) the rest turns; Pull takes the bone
                        // there, so without Pull gravity does nothing.
                        float length = animOffset.magnitude;
                        if (length > 1e-6f)
                        {
                            target = parent.Position + Vector3.Slerp(animOffset / length, gravityDir, Mathf.Clamp01(gravityShare)) * length;
                        }
                    }
                }

                // Momentum (verlet) with drag: the bone keeps that share of its own world movement each step, so when the
                // body moves the bone trails, swings past rest when it stops and settles.
                Vector3 velocity = (p.Position - p.PrevPosition) * momentum;
                p.PrevPosition = p.Position;
                p.Position += velocity;

                // 1.0 Gravity and External Force push the bone (lengths per step).
                if (!_posed)
                {
                    if (!v11 && gravityShare > 0f) p.Position += gravityDir * (gravityShare * GravityStrength * p.RestLength);
                    if (pushed) p.Position += ExternalForce * (GravityStrength * p.RestLength);
                }

                // Pull back toward the rest (or posed) position.
                p.Position += (target - p.Position) * pullAmount;

                if (advanced && !_posed)
                {
                    float stiff = Mathf.Clamp01(stiffness * Curve(stiffnessCurve, depth));
                    if (stiff > 0f && v11)
                    {
                        // 1.1 Stiffness: that share of the bone keeps the direction it had last step (turning with its parent).
                        Vector3 offset = p.Position - parent.Position;
                        float length = offset.magnitude;
                        Vector3 kept = parentRotation * p.LocalOffset;
                        if (length > 1e-6f && kept.sqrMagnitude > 1e-12f)
                        {
                            p.Position = parent.Position + Vector3.Slerp(offset / length, kept.normalized, stiff) * length;
                        }
                    }
                    else if (stiff > 0f)
                    {
                        // 1.0 Stiffness: the bone stays within a distance of its rest position.
                        float maxDeviation = p.RestLength * (1f - stiff) * 2f;
                        Vector3 deviation = p.Position - restPosition;
                        float d = deviation.magnitude;
                        if (d > maxDeviation && d > 1e-6f) p.Position = restPosition + deviation * (maxDeviation / d);
                    }
                }

                // The limit sits on the parent as it is at this step's moment (the bone is shown in the parent's space, so a
                // cone taken from the frame's end pose showed bones past their limit while the parent turned).
                if (limitType != LimitType.None && p.RestLength > 1e-6f)
                {
                    float restLength = animOffset.magnitude;
                    if (restLength > 1e-6f) p.LimitFrame = LimitFrameFor(parentRotation, animOffset / restLength, limitRotation);
                }
                ApplyLimit(ref p, ref parent, animOffset);
                ApplyLength(ref p, ref parent, animOffset, false);
                if (frameColliders != null && frameColliders.Count > 0)
                {
                    Collide(ref p, ref parent, frameColliders);
                    // A collider push can't shove a bone past its limit either.
                    ApplyLimit(ref p, ref parent, animOffset);
                    ApplyLength(ref p, ref parent, animOffset, true);
                }

                // How the parent bone turns to follow (ApplyToTransforms aims it the same way).
                List<int> siblings = _children[p.Parent];
                if (siblings[0] == i && (siblings.Count == 1 || multiChildType == MultiChildType.First))
                {
                    _turn[p.Parent] = Quaternion.FromToRotation(inherited * animOffset, p.Position - parent.Position) * inherited;
                }
                else if (p.EndsAverage)
                {
                    LineUpAverage(p.Parent, inherited, u);
                }
            }

            if (_grabIndex > 0 && _grabIndex < _count) ApplyGrab();

            // Each bone's offset from its parent in the parent's animated space: what 1.1 Stiffness keeps next step, and the
            // pose shown between steps (it then follows this frame's pose exactly).
            for (int n = 0; n < _order.Length; n++)
            {
                int i = _order[n];
                ref Particle p = ref _particles[i];
                p.PrevLocalOffset = p.LocalOffset;
                p.LocalOffset = Quaternion.Inverse(_stepRotation[p.Parent]) * (p.Position - _particles[p.Parent].Position);
            }
        }

        /// <summary>Puts a bone that isn't simulated exactly where its parent's turn holds it.</summary>
        private static void Hold(ref Particle p, Vector3 position)
        {
            p.Position = position;
            p.PrevPosition = position;
        }

        /// <summary>
        /// Multi-Child Average: the parent turns toward the average of its children, so they all turn with it as one
        /// piece (each child keeping its place on the parent) instead of drifting apart from bones that can't follow.
        /// </summary>
        private void LineUpAverage(int parentIndex, Quaternion inherited, float u)
        {
            ref Particle parent = ref _particles[parentIndex];
            List<int> kids = _children[parentIndex];
            Vector3 from = Vector3.zero;
            Vector3 to = Vector3.zero;
            foreach (int k in kids)
            {
                from += inherited * StepOffset(k, parentIndex, u);
                to += _particles[k].Position - parent.Position;
            }
            Quaternion turn = from.sqrMagnitude > 1e-12f && to.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(from, to) * inherited : inherited;
            _turn[parentIndex] = turn;
            foreach (int k in kids)
            {
                ref Particle child = ref _particles[k];
                child.Position = parent.Position + turn * StepOffset(k, parentIndex, u);
            }
        }

        /// <summary>A bone's animated offset from its parent at step moment <paramref name="u"/> of this frame.</summary>
        private Vector3 StepOffset(int child, int parent, float u)
        {
            ref Particle c = ref _particles[child];
            ref Particle p = ref _particles[parent];
            return Vector3.LerpUnclamped(c.PrevAnimPosition - p.PrevAnimPosition, c.AnimPosition - p.AnimPosition, u);
        }

        private void ApplyLimit(ref Particle p, ref Particle parent, Vector3 animOffset)
        {
            if (p.RestLength <= 1e-6f) return;

            Vector3 dir = p.Position - parent.Position;
            float length = dir.magnitude;
            if (length <= 1e-6f) return;
            dir /= length;

            float maxX = maxAngleX * Curve(maxAngleXCurve, p.Depth01);
            // The limit frame (from PrepareFrame): Y = the cone's axis, i.e. the rest direction turned by Limit
            // Rotation. Angle and Polar used to ignore Limit Rotation and sat on the bare rest direction.
            Quaternion frame = p.LimitFrame;
            Vector3 cone = frame * Vector3.up;

            switch (limitType)
            {
                case LimitType.None:
                {
                    Vector3 axis = animOffset / p.RestLength;
                    float swing = Vector3.Angle(axis, dir);
                    if (swing <= MaxSwingDegrees) return;
                    dir = Vector3.Slerp(axis, dir, MaxSwingDegrees / swing);
                    break;
                }
                case LimitType.Angle:
                {
                    float angle = Vector3.Angle(cone, dir);
                    if (angle <= maxX) return;
                    dir = Vector3.Slerp(cone, dir, maxX / angle);
                    break;
                }
                case LimitType.Hinge:
                {
                    // Swings in one plane (pitch): the frame's X is the hinge.
                    Vector3 planar = Vector3.ProjectOnPlane(dir, frame * Vector3.right);
                    if (planar.sqrMagnitude < 1e-8f) planar = cone;
                    planar.Normalize();
                    float angle = Vector3.Angle(cone, planar);
                    dir = angle > maxX ? Vector3.Slerp(cone, planar, maxX / angle) : planar;
                    break;
                }
                case LimitType.Polar:
                {
                    // An ellipse: Max Angle X is the pitch (toward the frame's Z, the same plane Hinge swings in),
                    // Max Angle Z the yaw (toward its X). 0 on either locks that way.
                    float maxZ = maxAngleZ * Curve(maxAngleZCurve, p.Depth01);
                    Vector3 pitchAxis = frame * Vector3.forward;
                    Vector3 yawAxis = frame * Vector3.right;
                    float ly = Vector3.Dot(dir, cone);
                    float pitch = Mathf.Atan2(Vector3.Dot(dir, pitchAxis), ly) * Mathf.Rad2Deg;
                    float yaw = Mathf.Atan2(Vector3.Dot(dir, yawAxis), ly) * Mathf.Rad2Deg;
                    bool changed = false;
                    if (maxX < 0.01f && pitch != 0f) { pitch = 0f; changed = true; }
                    if (maxZ < 0.01f && yaw != 0f) { yaw = 0f; changed = true; }
                    float rp = maxX >= 0.01f ? pitch / maxX : 0f;
                    float ry = maxZ >= 0.01f ? yaw / maxZ : 0f;
                    float e = rp * rp + ry * ry;
                    if (e > 1f)
                    {
                        float k = 1f / Mathf.Sqrt(e);
                        pitch *= k;
                        yaw *= k;
                        changed = true;
                    }
                    if (!changed) return;
                    pitch = Mathf.Clamp(pitch, -89f, 89f);
                    yaw = Mathf.Clamp(yaw, -89f, 89f);
                    dir = (cone + pitchAxis * Mathf.Tan(pitch * Mathf.Deg2Rad) + yawAxis * Mathf.Tan(yaw * Mathf.Deg2Rad)).normalized;
                    break;
                }
            }

            p.Position = parent.Position + dir * length;
        }

        /// <param name="afterCollision">1.1: a collider may stretch / squish the bone up to Max Stretch / Squish whatever
        /// Stretch Motion says; motion (false) only by Stretch Motion's share. 1.0: only grabbing changes a bone's length.</param>
        private void ApplyLength(ref Particle p, ref Particle parent, Vector3 animOffset, bool afterCollision)
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
            bool v11 = version == Version.Version_1_1;
            float motion = !v11 ? 0f : afterCollision ? 1f : Mathf.Clamp01(stretchMotion * Curve(stretchMotionCurve, p.Depth01));
            // Stretch Motion is the share of the stretch / squish motion asks for that the bone takes, up to the maxima.
            float target = Mathf.Clamp(rest + (length - rest) * motion, rest * (1f - squish), rest * (1f + stretch));
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
                if (_particles[i].Mode != Free) continue;
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

        /// <summary>Back to the rest pose: lets go of a grab, drops a pose and restarts the motion (Reset Poses).</summary>
        public void ResetPose()
        {
            ReleaseGrab(false);
            _posed = false;
            RestoreInitialPose();
            _needsReset = true;
        }

        private void ApplyGrab()
        {
            ref Particle grabbed = ref _particles[_grabIndex];
            // Grab Movement 0: the bone gets to the hand at Pull's pace; 1: it is at the hand straight away.
            float follow = Mathf.Lerp(Mathf.Max(0.05f, Mathf.Clamp01(pull * Curve(pullCurve, grabbed.Depth01))), 1f, Mathf.Clamp01(grabMovement));
            grabbed.Position = Vector3.Lerp(grabbed.Position, _grabTarget, follow);
            grabbed.PrevPosition = grabbed.Position;

            // FABRIK along the path anchor -> grabbed bone so the whole chain bends toward the hand. The anchor is the
            // root, or the nearest bone above that isn't simulated (pinned / rigid), which a hand can't move either.
            var path = ParelMotionBoneSystem.PathBuffer;
            path.Clear();
            for (int b = _grabIndex; b > 0 && _particles[b].Mode == Free; b = _particles[b].Parent) path.Add(b);
            if (path.Count == 0) return;

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
                ref Particle parent = ref _particles[p.Parent];
                Vector3 parentPosition = parent.Position;
                Vector3 direction = p.Position - parentPosition;
                float length = direction.magnitude;
                float stretch = maxStretch * Curve(maxStretchCurve, p.Depth01);
                float target = Mathf.Clamp(length, p.RestLength * (1f - SquishAt(p.Depth01)), p.RestLength * (1f + stretch));
                p.Position = length > 1e-6f ? parentPosition + direction * (target / length) : parentPosition;
                // A hard pull can't bend bones past their limits (it used to fold them over).
                ApplyLimit(ref p, ref parent, p.AnimPosition - parent.AnimPosition);
                p.PrevPosition = p.Position;
            }
        }

        // =========================================================================================
        // Output
        // =========================================================================================

        /// <param name="alpha">How far this frame is from the step before the last one to the last (0-1).</param>
        private void ApplyToTransforms(float alpha)
        {
            // What is shown: each bone at its offset from its parent between the last two steps, in its parent's animated
            // space, hung from this frame's root, so the chain follows this frame's pose exactly and only its swing is
            // eased between steps.
            _shown[0] = _particles[0].AnimPosition;
            for (int n = 0; n < _order.Length; n++)
            {
                int k = _order[n];
                ref Particle shown = ref _particles[k];
                _shown[k] = _shown[shown.Parent] + _particles[shown.Parent].AnimRotation * Vector3.LerpUnclamped(shown.PrevLocalOffset, shown.LocalOffset, alpha);
            }

            bool stretchy = maxStretch > 0f || maxSquish > 0f;
            for (int i = 0; i < _count; i++)
            {
                ref Particle p = ref _particles[i];
                if (p.IsVirtual || p.Transform == null) continue;

                if (i > 0 && stretchy) p.Transform.position = _shown[i];

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
                    to += _shown[children[c]] - _shown[i];
                }

                if (from.sqrMagnitude < 1e-12f || to.sqrMagnitude < 1e-12f) continue;
                p.Transform.rotation = Quaternion.FromToRotation(from, to) * p.Transform.rotation;
            }
        }

        private void WriteParameters()
        {
            if (string.IsNullOrEmpty(parameter) || _count < 2) return;
            if (!_sinkResolved || (_sink == null && _sinkGeneration != ParelAvatarSinks.Generation))
            {
                _sinkResolved = true;
                _sinkGeneration = ParelAvatarSinks.Generation;
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

            ref Particle first = ref _particles[Mathf.Clamp(_paramIndex, 1, _count - 1)];
            ref Particle root = ref _particles[first.Parent];
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

            DrawEditorChain(Root, 0, Mathf.Max(1, ChainDepth(Root, 0)));
        }

        private bool Ignored(Transform t) => ignoreTransforms != null && ignoreTransforms.Contains(t);

        private int SimulatedChildCount(Transform t)
        {
            int n = 0;
            for (int c = 0; c < t.childCount; c++) if (!Ignored(t.GetChild(c))) n++;
            return n;
        }

        private int ChainDepth(Transform t, int depth)
        {
            if (t == null || depth > 64) return depth;
            int deepest = depth;
            int kids = 0;
            for (int c = 0; c < t.childCount; c++)
            {
                Transform child = t.GetChild(c);
                if (Ignored(child)) continue;
                kids++;
                deepest = Mathf.Max(deepest, ChainDepth(child, depth + 1));
            }
            if (kids == 0 && endpointPosition != Vector3.zero) deepest = depth + 1;
            return deepest;
        }

        /// <summary>Edit-mode chain: bones, radii, and each moving bone's limit at its own depth (curves applied).
        /// Children of a bone with several children under Ignore don't move, so they get no cone.</summary>
        private void DrawEditorChain(Transform t, int depth, int maxDepth)
        {
            if (t == null || depth > 64) return;
            bool fixedKids = multiChildType == MultiChildType.Ignore && SimulatedChildCount(t) > 1;
            for (int c = 0; c < t.childCount; c++)
            {
                Transform child = t.GetChild(c);
                if (Ignored(child)) continue;
                float depth01 = (float)(depth + 1) / maxDepth;
                Gizmos.DrawLine(t.position, child.position);
                if (radius > 0f) Gizmos.DrawWireSphere(child.position, radius * Curve(radiusCurve, depth01) * Mathf.Max(0.0001f, MaxAbs(Root.lossyScale)));
                if (!fixedKids && limitType != LimitType.None && limitOpacity > 0f) DrawLimit(t, child, depth01);
                DrawEditorChain(child, depth + 1, maxDepth);
            }
            if (t.childCount == 0 && endpointPosition != Vector3.zero) Gizmos.DrawLine(t.position, t.TransformPoint(endpointPosition));
        }

        /// <summary>Draws the limit cone (Angle / Polar) or fan (Hinge) for one bone in the Scene view.</summary>
        private void DrawLimit(Transform parent, Transform child, float depth01)
        {
            Vector3 axis = child.position - parent.position;
            float length = axis.magnitude;
            if (length < 1e-5f) return;
            axis /= length;
            Color previous = Gizmos.color;
            Gizmos.color = new Color(0.4f, 0.75f, 1f, Mathf.Clamp01(limitOpacity));
            // The same frame the simulation limits against: the rest direction turned by Limit Rotation.
            Quaternion frame = LimitFrameFor(parent.rotation, axis, limitRotation);
            Vector3 cone = frame * Vector3.up;
            Vector3 yawAxis = frame * Vector3.right;
            Vector3 pitchAxis = frame * Vector3.forward;
            const int segments = 16;
            Vector3 last = Vector3.zero;
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments * Mathf.PI * 2f;
                float pitch = maxAngleX * Curve(maxAngleXCurve, depth01);
                float yaw = limitType == LimitType.Polar ? maxAngleZ * Curve(maxAngleZCurve, depth01) : pitch;
                Vector3 dir;
                if (limitType == LimitType.Hinge)
                {
                    float a = Mathf.Lerp(-pitch, pitch, (float)i / segments) * Mathf.Deg2Rad;
                    dir = cone * Mathf.Cos(a) + pitchAxis * Mathf.Sin(a);
                }
                else if (limitType == LimitType.Angle)
                {
                    // A round cone: every direction exactly Max Angle from the axis.
                    Vector3 rim = yawAxis * Mathf.Cos(t) + pitchAxis * Mathf.Sin(t);
                    float a = Mathf.Min(pitch, 179f) * Mathf.Deg2Rad;
                    dir = cone * Mathf.Cos(a) + rim * Mathf.Sin(a);
                }
                else
                {
                    float ap = Mathf.Sin(t) * pitch * Mathf.Deg2Rad;
                    float ay = Mathf.Cos(t) * yaw * Mathf.Deg2Rad;
                    dir = (cone + pitchAxis * Mathf.Tan(Mathf.Clamp(ap, -1.55f, 1.55f)) + yawAxis * Mathf.Tan(Mathf.Clamp(ay, -1.55f, 1.55f))).normalized;
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
