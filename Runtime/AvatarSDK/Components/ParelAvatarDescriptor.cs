using System;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Root descriptor for a ParelVR avatar -- the counterpart of VRChat's Avatar Descriptor, with the
    /// same sections in the same order: View, LipSync, Eye Look, Playable Layers, Lower Body,
    /// Expressions and Colliders. Lives on the avatar's root next to its humanoid Animator. The
    /// avatar's Blueprint ID lives on the <see cref="ParelPipelineManager"/> next to it, which ties
    /// re-uploads to the same avatar.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/Avatar SDK/Parel Avatar Descriptor")]
    public sealed class ParelAvatarDescriptor : MonoBehaviour
    {
        // Append-only: values are serialized as ints in uploaded avatars.
        public enum LipSyncStyle
        {
            /// <summary>ParelVR finds the face mesh and visemes itself (by blend shape name).</summary>
            Default = 0,
            /// <summary>Use the face mesh + 15 viseme blend shapes chosen below.</summary>
            VisemeBlendShape = 1,
            /// <summary>No lip sync.</summary>
            None = 2,
            /// <summary>Rotate a jaw bone between Closed and Open by voice volume.</summary>
            JawFlapBone = 3,
            /// <summary>Drive one "mouth open" blend shape by voice volume.</summary>
            JawFlapBlendShape = 4,
            /// <summary>Only the Viseme / Voice parameters are set; the FX layer animates the mouth.</summary>
            VisemeParameterOnly = 5,
        }

        public enum ColliderState
        {
            /// <summary>Placed from the humanoid rig.</summary>
            Automatic = 0,
            /// <summary>Uses the transform / radius / height below.</summary>
            Custom = 1,
            /// <summary>This collider doesn't exist (no contacts, no PhysBone collisions).</summary>
            Disabled = 2,
        }

        /// <summary>How the eyelids are animated (VRChat's Eyelid Type).</summary>
        public enum EyelidType
        {
            None = 0,
            Bones = 1,
            Blendshapes = 2,
        }

        /// <summary>One of the avatar's built-in contact colliders (head, torso, hands, feet, fingers).</summary>
        [Serializable]
        public sealed class ColliderConfig
        {
            [Tooltip("Edit the left and right sides together.")]
            public bool isMirrored = true;
            public ColliderState state = ColliderState.Automatic;
            public Transform transform;
            public float radius = 0.05f;
            public float height = 0.1f;
            public Vector3 position;
            public Quaternion rotation = Quaternion.identity;

            public ColliderConfig Clone()
            {
                return new ColliderConfig
                {
                    isMirrored = isMirrored, state = state, transform = transform, radius = radius, height = height, position = position, rotation = rotation,
                };
            }
        }

        /// <summary>A pair of local eye (or eyelid) rotations, optionally linked so both eyes use the same value.</summary>
        [Serializable]
        public struct EyeRotations
        {
            public bool linked;
            public Quaternion left;
            public Quaternion right;

            public static EyeRotations Identity => new EyeRotations { linked = true, left = Quaternion.identity, right = Quaternion.identity };
        }

        [Serializable]
        public struct EyelidRotations
        {
            public EyeRotations upper;
            public EyeRotations lower;

            public static EyelidRotations Identity => new EyelidRotations { upper = EyeRotations.Identity, lower = EyeRotations.Identity };
        }

        /// <summary>VRChat's "Eye Movements": how calm/excited and shy/confident the eyes behave.</summary>
        [Serializable]
        public struct EyeMovements
        {
            [Range(0f, 1f)] public float confidence;
            [Range(0f, 1f)] public float excitement;
        }

        /// <summary>Everything under the descriptor's Eye Look section.</summary>
        [Serializable]
        public sealed class CustomEyeLookSettings
        {
            public EyeMovements eyeMovement = new EyeMovements { confidence = 0.5f, excitement = 0.5f };

            public Transform leftEye;
            public Transform rightEye;
            public EyeRotations eyesLookingStraight = EyeRotations.Identity;
            public EyeRotations eyesLookingUp = EyeRotations.Identity;
            public EyeRotations eyesLookingDown = EyeRotations.Identity;
            public EyeRotations eyesLookingLeft = EyeRotations.Identity;
            public EyeRotations eyesLookingRight = EyeRotations.Identity;

            public EyelidType eyelidType = EyelidType.None;
            public Transform upperLeftEyelid;
            public Transform upperRightEyelid;
            public Transform lowerLeftEyelid;
            public Transform lowerRightEyelid;
            public EyelidRotations eyelidsDefault = EyelidRotations.Identity;
            public EyelidRotations eyelidsClosed = EyelidRotations.Identity;
            public EyelidRotations eyelidsLookingUp = EyelidRotations.Identity;
            public EyelidRotations eyelidsLookingDown = EyelidRotations.Identity;

            public SkinnedMeshRenderer eyelidsSkinnedMesh;
            /// <summary>Blend shape indices for Blink, Looking Up and Looking Down (-1 = none).</summary>
            public int[] eyelidsBlendshapes = { -1, -1, -1 };
        }

        /// <summary>One playable layer slot (VRChat's CustomAnimLayer).</summary>
        [Serializable]
        public struct CustomAnimLayer
        {
            public bool isEnabled;
            public ParelAvatarLayer type;
            public RuntimeAnimatorController animatorController;
            public AvatarMask mask;
            /// <summary>True = use ParelVR's default for this layer (the controller is ignored).</summary>
            public bool isDefault;
        }

        public static readonly ParelAvatarLayer[] BaseLayerTypes = { ParelAvatarLayer.Base, ParelAvatarLayer.Additive, ParelAvatarLayer.Gesture, ParelAvatarLayer.Action, ParelAvatarLayer.FX };
        public static readonly ParelAvatarLayer[] SpecialLayerTypes = { ParelAvatarLayer.Sitting, ParelAvatarLayer.TPose, ParelAvatarLayer.IKPose };

        [SerializeField] private Animator avatarAnimator;

        // ---- View ---------------------------------------------------------------------------
        [Tooltip("Where your eyes are, relative to the avatar root. This is the first-person camera position.")]
        [SerializeField] private Vector3 viewPoint = new Vector3(0f, 1.6f, 0.08f);
        [SerializeField] private bool viewPointInitialized;

        // Kept for avatars authored before the View Position existed (and the old Build Kit tab).
        [SerializeField, HideInInspector] private Transform viewPosition;
        [SerializeField] private Transform headOverride;
        [SerializeField] private Transform leftHandOverride;
        [SerializeField] private Transform rightHandOverride;

        // ---- Lip Sync -----------------------------------------------------------------------
        [SerializeField] private LipSyncStyle lipSync = LipSyncStyle.Default;
        [SerializeField] private SkinnedMeshRenderer visemeSkinnedMesh;
        [SerializeField] private string[] visemeBlendShapes = new string[ParelVisemeAutoMapper.VisemeCount];
        [SerializeField] private Transform jawBone;
        [SerializeField] private Vector3 jawClosedRotation;
        [SerializeField] private Vector3 jawOpenRotation = new Vector3(18f, 0f, 0f);
        // Legacy: Jaw Flap Blend Shape now uses the Face Mesh above, like VRChat.
        [SerializeField, HideInInspector] private SkinnedMeshRenderer jawFlapMesh;
        [SerializeField] private string jawFlapBlendShape = string.Empty;

        // ---- Eye Look -----------------------------------------------------------------------
        [SerializeField] private bool enableEyeLook;
        [SerializeField] private CustomEyeLookSettings customEyeLookSettings = new CustomEyeLookSettings();

        // Legacy eye fields (SDK 1.0 layout). Read only until the descriptor is upgraded.
        [SerializeField, HideInInspector] private bool enableBlink = true;
        [SerializeField, HideInInspector] private SkinnedMeshRenderer eyelidsSkinnedMesh;
        [SerializeField, HideInInspector] private string blinkBlendShape = string.Empty;
        [SerializeField, HideInInspector] private string lookingUpBlendShape = string.Empty;
        [SerializeField, HideInInspector] private string lookingDownBlendShape = string.Empty;
        [SerializeField, HideInInspector] private bool customEyeLook;
        [SerializeField, HideInInspector] private Transform leftEye;
        [SerializeField, HideInInspector] private Transform rightEye;
        [SerializeField, HideInInspector] private float eyeLookUp = 15f;
        [SerializeField, HideInInspector] private float eyeLookDown = 12f;
        [SerializeField, HideInInspector] private float eyeLookIn = 15f;
        [SerializeField, HideInInspector] private float eyeLookOut = 20f;
        [SerializeField, HideInInspector] private float eyeCalmExcited = 0.5f;
        [SerializeField, HideInInspector] private float eyeShyConfident = 0.5f;

        // ---- Playable Layers ----------------------------------------------------------------
        [SerializeField] private bool customizeAnimationLayers;
        [SerializeField] private CustomAnimLayer[] baseAnimationLayers = DefaultLayers(BaseLayerTypes);
        [SerializeField] private CustomAnimLayer[] specialAnimationLayers = DefaultLayers(SpecialLayerTypes);

        // Legacy per-layer fields (SDK 1.0 layout).
        [SerializeField, HideInInspector] private RuntimeAnimatorController baseLayer;
        [SerializeField, HideInInspector] private RuntimeAnimatorController additiveLayer;
        [SerializeField, HideInInspector] private RuntimeAnimatorController gestureLayer;
        [SerializeField, HideInInspector] private RuntimeAnimatorController actionLayer;
        [SerializeField, HideInInspector] private RuntimeAnimatorController fxController;
        [SerializeField, HideInInspector] private RuntimeAnimatorController sittingLayer;
        [SerializeField, HideInInspector] private RuntimeAnimatorController tPoseLayer;
        [SerializeField, HideInInspector] private RuntimeAnimatorController ikPoseLayer;

        // ---- Lower Body ---------------------------------------------------------------------
        [Tooltip("With 3-point or 4-point tracking, step the feet automatically when you move.")]
        [SerializeField] private bool useAutoFootstep = true;
        [Tooltip("Play locomotion animations even when full-body tracked.")]
        [SerializeField] private bool forceLocomotionAnimations = true;

        // ---- Expressions ----------------------------------------------------------------------
        [SerializeField] private bool customExpressions;
        [SerializeField] private ParelExpressionsMenu expressionsMenu;
        [SerializeField] private ParelExpressionParameters expressionParameters;

        // ---- Colliders ------------------------------------------------------------------------
        [SerializeField] private ColliderConfig colliderHead = new ColliderConfig { radius = 0.08f };
        [SerializeField] private ColliderConfig colliderTorso = new ColliderConfig { radius = 0.12f, height = 0.35f };
        [SerializeField] private ColliderConfig colliderHandL = new ColliderConfig { radius = 0.045f, height = 0.1f };
        [SerializeField] private ColliderConfig colliderHandR = new ColliderConfig { radius = 0.045f, height = 0.1f };
        [SerializeField] private ColliderConfig colliderFootL = new ColliderConfig { radius = 0.05f, height = 0.15f };
        [SerializeField] private ColliderConfig colliderFootR = new ColliderConfig { radius = 0.05f, height = 0.15f };
        [SerializeField] private ColliderConfig colliderFingerIndexL = new ColliderConfig { radius = 0.01f, height = 0.06f };
        [SerializeField] private ColliderConfig colliderFingerIndexR = new ColliderConfig { radius = 0.01f, height = 0.06f };
        [SerializeField] private ColliderConfig colliderFingerMiddleL = new ColliderConfig { radius = 0.01f, height = 0.06f };
        [SerializeField] private ColliderConfig colliderFingerMiddleR = new ColliderConfig { radius = 0.01f, height = 0.06f };
        [SerializeField] private ColliderConfig colliderFingerRingL = new ColliderConfig { radius = 0.01f, height = 0.06f };
        [SerializeField] private ColliderConfig colliderFingerRingR = new ColliderConfig { radius = 0.01f, height = 0.06f };
        [SerializeField] private ColliderConfig colliderFingerLittleL = new ColliderConfig { radius = 0.009f, height = 0.05f };
        [SerializeField] private ColliderConfig colliderFingerLittleR = new ColliderConfig { radius = 0.009f, height = 0.05f };

        // ---- Scaling (ParelVR) ------------------------------------------------------------------
        [Tooltip("Let the wearer resize this avatar in game (radial menu / RSP).")]
        [SerializeField] private bool allowUserScaling = true;
        [SerializeField] private float minUserScale = 0.5f;
        [SerializeField] private float maxUserScale = 2f;

        // ---- Pipeline -------------------------------------------------------------------------
        // Legacy home of the Blueprint ID; the Pipeline Manager component holds it now.
        [SerializeField, HideInInspector] private string blueprintId = string.Empty;
        [SerializeField, HideInInspector] private string unityVersion = string.Empty;

        // False until the 1.0 layout above has been copied into the VRChat-style layout.
        [SerializeField, HideInInspector] private bool layoutUpgraded;

        private static CustomAnimLayer[] DefaultLayers(ParelAvatarLayer[] types)
        {
            var layers = new CustomAnimLayer[types.Length];
            for (int i = 0; i < types.Length; i++) layers[i] = new CustomAnimLayer { isEnabled = true, type = types[i], isDefault = true };
            return layers;
        }

        // =========================================================================================
        // Basics
        // =========================================================================================

        public Animator AvatarAnimator
        {
            get => avatarAnimator;
            set => avatarAnimator = value;
        }

        /// <summary>True once the descriptor uses the current (VRChat-style) data layout.</summary>
        public bool LayoutUpgraded => layoutUpgraded;

        /// <summary>The Unity version the avatar was last built with.</summary>
        public string UnityVersion
        {
            get => unityVersion;
            set => unityVersion = value ?? string.Empty;
        }

        /// <summary>Optional explicit view transform (legacy). <see cref="ViewPoint"/> is what's used.</summary>
        public Transform ViewPosition
        {
            get => viewPosition;
            set => viewPosition = value;
        }

        /// <summary>Eye position relative to the avatar root (VRChat's "View Position").</summary>
        public Vector3 ViewPoint
        {
            get => viewPosition != null && !viewPointInitialized ? transform.InverseTransformPoint(viewPosition.position) : viewPoint;
            set
            {
                viewPoint = value;
                viewPointInitialized = true;
            }
        }

        public bool ViewPointInitialized => viewPointInitialized;

        public Transform HeadOverride
        {
            get => headOverride;
            set => headOverride = value;
        }

        public Transform LeftHandOverride
        {
            get => leftHandOverride;
            set => leftHandOverride = value;
        }

        public Transform RightHandOverride
        {
            get => rightHandOverride;
            set => rightHandOverride = value;
        }

        // =========================================================================================
        // Lip Sync
        // =========================================================================================

        public LipSyncStyle LipSync
        {
            get => lipSync;
            set => lipSync = value;
        }

        /// <summary>The Face Mesh: viseme blend shapes, and the jaw flap blend shape.</summary>
        public SkinnedMeshRenderer VisemeSkinnedMesh
        {
            get => visemeSkinnedMesh;
            set => visemeSkinnedMesh = value;
        }

        /// <summary>15 blend shape names in viseme order (sil, PP, FF, TH, DD, kk, CH, SS, nn, RR, aa, E, ih, oh, ou).</summary>
        public string[] VisemeBlendShapes
        {
            get
            {
                if (visemeBlendShapes == null || visemeBlendShapes.Length != ParelVisemeAutoMapper.VisemeCount)
                {
                    var resized = new string[ParelVisemeAutoMapper.VisemeCount];
                    if (visemeBlendShapes != null)
                    {
                        for (int i = 0; i < resized.Length && i < visemeBlendShapes.Length; i++) resized[i] = visemeBlendShapes[i];
                    }
                    visemeBlendShapes = resized;
                }
                return visemeBlendShapes;
            }
            set => visemeBlendShapes = value;
        }

        public Transform JawBone
        {
            get => jawBone;
            set => jawBone = value;
        }

        public Quaternion JawClosedRotation => Quaternion.Euler(jawClosedRotation);
        public Quaternion JawOpenRotation => Quaternion.Euler(jawOpenRotation);

        public Vector3 JawClosedEuler
        {
            get => jawClosedRotation;
            set => jawClosedRotation = value;
        }

        public Vector3 JawOpenEuler
        {
            get => jawOpenRotation;
            set => jawOpenRotation = value;
        }

        /// <summary>The mesh the jaw flap blend shape lives on (the Face Mesh).</summary>
        public SkinnedMeshRenderer JawFlapMesh
        {
            get => jawFlapMesh != null ? jawFlapMesh : visemeSkinnedMesh;
            set
            {
                visemeSkinnedMesh = value;
                jawFlapMesh = null;
            }
        }

        public string JawFlapBlendShape
        {
            get => jawFlapBlendShape;
            set => jawFlapBlendShape = value ?? string.Empty;
        }

        // =========================================================================================
        // Eye Look
        // =========================================================================================

        public bool EnableEyeLook
        {
            get => layoutUpgraded ? enableEyeLook : customEyeLook || (enableBlink && eyelidsSkinnedMesh != null);
            set => enableEyeLook = value;
        }

        public CustomEyeLookSettings EyeLookSettings => customEyeLookSettings ??= new CustomEyeLookSettings();

        /// <summary>Whether this avatar blinks. With Eye Look off, ParelVR blinks with whatever it can find.</summary>
        public bool EnableBlink
        {
            get
            {
                if (!layoutUpgraded) return enableBlink;
                return !enableEyeLook || EyeLookSettings.eyelidType != EyelidType.None;
            }
        }

        /// <summary>The eyelid blend shape mesh, or null to let ParelVR find one.</summary>
        public SkinnedMeshRenderer EyelidsSkinnedMesh
        {
            get
            {
                if (!layoutUpgraded) return eyelidsSkinnedMesh;
                return enableEyeLook && EyeLookSettings.eyelidType == EyelidType.Blendshapes ? EyeLookSettings.eyelidsSkinnedMesh : null;
            }
        }

        public string BlinkBlendShape => layoutUpgraded ? EyelidShapeName(0) : blinkBlendShape;
        public string LookingUpBlendShape => layoutUpgraded ? EyelidShapeName(1) : lookingUpBlendShape;
        public string LookingDownBlendShape => layoutUpgraded ? EyelidShapeName(2) : lookingDownBlendShape;

        private string EyelidShapeName(int slot)
        {
            SkinnedMeshRenderer mesh = EyelidsSkinnedMesh;
            int[] indices = EyeLookSettings.eyelidsBlendshapes;
            if (mesh == null || mesh.sharedMesh == null || indices == null || slot >= indices.Length) return string.Empty;
            int index = indices[slot];
            return index >= 0 && index < mesh.sharedMesh.blendShapeCount ? mesh.sharedMesh.GetBlendShapeName(index) : string.Empty;
        }

        /// <summary>True when the creator set up their own eye bones and rotation states.</summary>
        public bool CustomEyeLook => layoutUpgraded ? enableEyeLook && (EyeLookSettings.leftEye != null || EyeLookSettings.rightEye != null) : customEyeLook;

        public Transform LeftEye
        {
            get
            {
                Transform custom = layoutUpgraded ? EyeLookSettings.leftEye : leftEye;
                return custom != null ? custom : ResolveBone(HumanBodyBones.LeftEye);
            }
            set
            {
                if (layoutUpgraded) EyeLookSettings.leftEye = value;
                else leftEye = value;
            }
        }

        public Transform RightEye
        {
            get
            {
                Transform custom = layoutUpgraded ? EyeLookSettings.rightEye : rightEye;
                return custom != null ? custom : ResolveBone(HumanBodyBones.RightEye);
            }
            set
            {
                if (layoutUpgraded) EyeLookSettings.rightEye = value;
                else rightEye = value;
            }
        }

        /// <summary>
        /// Eye rotation limits in degrees: up, down, toward the nose, away from the nose -- read from the
        /// Looking Up / Down / Left / Right rotation states of the left eye.
        /// </summary>
        public Vector4 EyeLimits
        {
            get
            {
                if (!layoutUpgraded || !CustomEyeLook) return new Vector4(eyeLookUp, eyeLookDown, eyeLookIn, eyeLookOut);
                CustomEyeLookSettings s = EyeLookSettings;
                Quaternion straight = s.eyesLookingStraight.left;
                return new Vector4(
                    Mathf.Clamp(Quaternion.Angle(straight, s.eyesLookingUp.left), 0f, 45f),
                    Mathf.Clamp(Quaternion.Angle(straight, s.eyesLookingDown.left), 0f, 45f),
                    Mathf.Clamp(Quaternion.Angle(straight, s.eyesLookingRight.left), 0f, 45f),
                    Mathf.Clamp(Quaternion.Angle(straight, s.eyesLookingLeft.left), 0f, 45f));
            }
        }

        public void SetEyeLimits(float up, float down, float inward, float outward)
        {
            eyeLookUp = Mathf.Clamp(up, 0f, 45f);
            eyeLookDown = Mathf.Clamp(down, 0f, 45f);
            eyeLookIn = Mathf.Clamp(inward, 0f, 45f);
            eyeLookOut = Mathf.Clamp(outward, 0f, 45f);
            if (layoutUpgraded) ResetEyeRotationStates(eyeLookUp, eyeLookDown, eyeLookIn, eyeLookOut);
        }

        /// <summary>0 = calm (long holds), 1 = excited (quick, frequent eye movements).</summary>
        public float EyeCalmExcited
        {
            get => layoutUpgraded ? EyeLookSettings.eyeMovement.excitement : eyeCalmExcited;
            set
            {
                if (layoutUpgraded) EyeLookSettings.eyeMovement.excitement = Mathf.Clamp01(value);
                else eyeCalmExcited = Mathf.Clamp01(value);
            }
        }

        /// <summary>0 = shy (looks away from people), 1 = confident (holds eye contact).</summary>
        public float EyeShyConfident
        {
            get => layoutUpgraded ? EyeLookSettings.eyeMovement.confidence : eyeShyConfident;
            set
            {
                if (layoutUpgraded) EyeLookSettings.eyeMovement.confidence = Mathf.Clamp01(value);
                else eyeShyConfident = Mathf.Clamp01(value);
            }
        }

        /// <summary>
        /// Sets the five eye rotation states from the eyes' current pose: Looking Straight is the
        /// current local rotation, the other four turn the eye by the given angles around the avatar's
        /// own axes (so it works whatever way the eye bones are oriented).
        /// </summary>
        public void ResetEyeRotationStates(float up = 15f, float down = 12f, float inward = 15f, float outward = 20f)
        {
            CustomEyeLookSettings s = EyeLookSettings;
            Transform left = s.leftEye != null ? s.leftEye : ResolveBone(HumanBodyBones.LeftEye);
            Transform right = s.rightEye != null ? s.rightEye : ResolveBone(HumanBodyBones.RightEye);
            s.eyesLookingStraight = new EyeRotations { linked = true, left = Local(left), right = Local(right) };
            s.eyesLookingUp = Turned(left, right, transform.right, -up, -up);
            s.eyesLookingDown = Turned(left, right, transform.right, down, down);
            // Looking left: the left eye turns outward, the right eye inward.
            s.eyesLookingLeft = Turned(left, right, transform.up, -outward, -inward);
            s.eyesLookingRight = Turned(left, right, transform.up, inward, outward);
        }

        private static Quaternion Local(Transform t) => t != null ? t.localRotation : Quaternion.identity;

        private static EyeRotations Turned(Transform left, Transform right, Vector3 axis, float leftDegrees, float rightDegrees)
        {
            return new EyeRotations
            {
                linked = Mathf.Approximately(leftDegrees, rightDegrees),
                left = TurnLocal(left, axis, leftDegrees),
                right = TurnLocal(right, axis, rightDegrees),
            };
        }

        /// <summary>The local rotation <paramref name="t"/> would have if turned by degrees around a world axis.</summary>
        public static Quaternion TurnLocal(Transform t, Vector3 worldAxis, float degrees)
        {
            if (t == null) return Quaternion.identity;
            Quaternion world = Quaternion.AngleAxis(degrees, worldAxis) * t.rotation;
            return t.parent != null ? Quaternion.Inverse(t.parent.rotation) * world : world;
        }

        // =========================================================================================
        // Playable Layers
        // =========================================================================================

        /// <summary>VRChat's "Customize" switch: false = every layer uses ParelVR's defaults.</summary>
        public bool CustomizeAnimationLayers
        {
            get => layoutUpgraded ? customizeAnimationLayers : HasLegacyLayers();
            set => customizeAnimationLayers = value;
        }

        public CustomAnimLayer[] BaseAnimationLayers => baseAnimationLayers ??= DefaultLayers(BaseLayerTypes);
        public CustomAnimLayer[] SpecialAnimationLayers => specialAnimationLayers ??= DefaultLayers(SpecialLayerTypes);

        public RuntimeAnimatorController BaseLayer
        {
            get => GetLayer(ParelAvatarLayer.Base);
            set => SetLayer(ParelAvatarLayer.Base, value);
        }

        public RuntimeAnimatorController AdditiveLayer
        {
            get => GetLayer(ParelAvatarLayer.Additive);
            set => SetLayer(ParelAvatarLayer.Additive, value);
        }

        public RuntimeAnimatorController GestureLayer
        {
            get => GetLayer(ParelAvatarLayer.Gesture);
            set => SetLayer(ParelAvatarLayer.Gesture, value);
        }

        public RuntimeAnimatorController ActionLayer
        {
            get => GetLayer(ParelAvatarLayer.Action);
            set => SetLayer(ParelAvatarLayer.Action, value);
        }

        public RuntimeAnimatorController FxController
        {
            get => GetLayer(ParelAvatarLayer.FX);
            set => SetLayer(ParelAvatarLayer.FX, value);
        }

        public RuntimeAnimatorController SittingLayer
        {
            get => GetLayer(ParelAvatarLayer.Sitting);
            set => SetLayer(ParelAvatarLayer.Sitting, value);
        }

        public RuntimeAnimatorController TPoseLayer
        {
            get => GetLayer(ParelAvatarLayer.TPose);
            set => SetLayer(ParelAvatarLayer.TPose, value);
        }

        public RuntimeAnimatorController IKPoseLayer
        {
            get => GetLayer(ParelAvatarLayer.IKPose);
            set => SetLayer(ParelAvatarLayer.IKPose, value);
        }

        /// <summary>The controller assigned to a playable layer, or null when ParelVR's default is used.</summary>
        public RuntimeAnimatorController GetLayer(ParelAvatarLayer layer)
        {
            if (!layoutUpgraded) return GetLegacyLayer(layer);
            if (!customizeAnimationLayers) return null;
            int index = FindLayer(layer, out CustomAnimLayer[] list);
            if (index < 0) return null;
            CustomAnimLayer entry = list[index];
            return entry.isDefault ? null : entry.animatorController;
        }

        /// <summary>The avatar mask stored for a playable layer (taken from its controller's first layer).</summary>
        public AvatarMask GetLayerMask(ParelAvatarLayer layer)
        {
            if (!layoutUpgraded || !customizeAnimationLayers) return null;
            int index = FindLayer(layer, out CustomAnimLayer[] list);
            return index >= 0 && !list[index].isDefault ? list[index].mask : null;
        }

        /// <summary>Assigns a controller to a playable layer (null = back to ParelVR's default) and turns Customize on.</summary>
        public void SetLayer(ParelAvatarLayer layer, RuntimeAnimatorController controller)
        {
            if (!layoutUpgraded)
            {
                SetLegacyLayer(layer, controller);
                return;
            }

            int index = FindLayer(layer, out CustomAnimLayer[] list);
            if (index < 0)
            {
                bool special = Array.IndexOf(SpecialLayerTypes, layer) >= 0;
                CustomAnimLayer[] source = special ? SpecialAnimationLayers : BaseAnimationLayers;
                var grown = new CustomAnimLayer[source.Length + 1];
                Array.Copy(source, grown, source.Length);
                grown[source.Length] = new CustomAnimLayer { isEnabled = true, type = layer, isDefault = true };
                if (special) specialAnimationLayers = grown;
                else baseAnimationLayers = grown;
                index = FindLayer(layer, out list);
            }

            list[index].animatorController = controller;
            list[index].isDefault = controller == null;
            if (controller != null) customizeAnimationLayers = true;
        }

        private int FindLayer(ParelAvatarLayer layer, out CustomAnimLayer[] list)
        {
            list = BaseAnimationLayers;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].type == layer) return i;
            }
            list = SpecialAnimationLayers;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].type == layer) return i;
            }
            return -1;
        }

        /// <summary>Puts every playable layer back on ParelVR's defaults (VRChat's "Reset to Default").</summary>
        public void ResetAnimationLayersToDefault()
        {
            baseAnimationLayers = DefaultLayers(BaseLayerTypes);
            specialAnimationLayers = DefaultLayers(SpecialLayerTypes);
        }

        private bool HasLegacyLayers() =>
            baseLayer != null || additiveLayer != null || gestureLayer != null || actionLayer != null ||
            fxController != null || sittingLayer != null || tPoseLayer != null || ikPoseLayer != null;

        private RuntimeAnimatorController GetLegacyLayer(ParelAvatarLayer layer)
        {
            switch (layer)
            {
                case ParelAvatarLayer.Base: return baseLayer;
                case ParelAvatarLayer.Additive: return additiveLayer;
                case ParelAvatarLayer.Gesture: return gestureLayer;
                case ParelAvatarLayer.Action: return actionLayer;
                case ParelAvatarLayer.FX: return fxController;
                case ParelAvatarLayer.Sitting: return sittingLayer;
                case ParelAvatarLayer.TPose: return tPoseLayer;
                case ParelAvatarLayer.IKPose: return ikPoseLayer;
                default: return null;
            }
        }

        private void SetLegacyLayer(ParelAvatarLayer layer, RuntimeAnimatorController controller)
        {
            switch (layer)
            {
                case ParelAvatarLayer.Base: baseLayer = controller; break;
                case ParelAvatarLayer.Additive: additiveLayer = controller; break;
                case ParelAvatarLayer.Gesture: gestureLayer = controller; break;
                case ParelAvatarLayer.Action: actionLayer = controller; break;
                case ParelAvatarLayer.FX: fxController = controller; break;
                case ParelAvatarLayer.Sitting: sittingLayer = controller; break;
                case ParelAvatarLayer.TPose: tPoseLayer = controller; break;
                case ParelAvatarLayer.IKPose: ikPoseLayer = controller; break;
            }
        }

        // =========================================================================================
        // Lower Body, Expressions, Colliders, Scaling
        // =========================================================================================

        public bool UseAutoFootstep
        {
            get => useAutoFootstep;
            set => useAutoFootstep = value;
        }

        public bool ForceLocomotionAnimations
        {
            get => forceLocomotionAnimations;
            set => forceLocomotionAnimations = value;
        }

        /// <summary>VRChat's Expressions "Customize" switch: false = no Expressions Menu / Parameters.</summary>
        public bool CustomExpressions
        {
            get => layoutUpgraded ? customExpressions : expressionsMenu != null || expressionParameters != null;
            set => customExpressions = value;
        }

        public ParelExpressionsMenu ExpressionsMenu
        {
            get => !layoutUpgraded || customExpressions ? expressionsMenu : null;
            set
            {
                expressionsMenu = value;
                if (value != null) customExpressions = true;
            }
        }

        public ParelExpressionParameters ExpressionParameters
        {
            get => !layoutUpgraded || customExpressions ? expressionParameters : null;
            set
            {
                expressionParameters = value;
                if (value != null) customExpressions = true;
            }
        }

        public ColliderConfig GetCollider(ParelAvatarColliderSlot slot)
        {
            switch (slot)
            {
                case ParelAvatarColliderSlot.Head: return colliderHead ??= new ColliderConfig { radius = 0.08f };
                case ParelAvatarColliderSlot.Torso: return colliderTorso ??= new ColliderConfig { radius = 0.12f, height = 0.35f };
                case ParelAvatarColliderSlot.HandL: return colliderHandL ??= new ColliderConfig { radius = 0.045f };
                case ParelAvatarColliderSlot.HandR: return colliderHandR ??= new ColliderConfig { radius = 0.045f };
                case ParelAvatarColliderSlot.FootL: return colliderFootL ??= new ColliderConfig { radius = 0.05f, height = 0.15f };
                case ParelAvatarColliderSlot.FootR: return colliderFootR ??= new ColliderConfig { radius = 0.05f, height = 0.15f };
                case ParelAvatarColliderSlot.FingerIndexL: return colliderFingerIndexL ??= new ColliderConfig { radius = 0.01f, height = 0.06f };
                case ParelAvatarColliderSlot.FingerIndexR: return colliderFingerIndexR ??= new ColliderConfig { radius = 0.01f, height = 0.06f };
                case ParelAvatarColliderSlot.FingerMiddleL: return colliderFingerMiddleL ??= new ColliderConfig { radius = 0.01f, height = 0.06f };
                case ParelAvatarColliderSlot.FingerMiddleR: return colliderFingerMiddleR ??= new ColliderConfig { radius = 0.01f, height = 0.06f };
                case ParelAvatarColliderSlot.FingerRingL: return colliderFingerRingL ??= new ColliderConfig { radius = 0.01f, height = 0.06f };
                case ParelAvatarColliderSlot.FingerRingR: return colliderFingerRingR ??= new ColliderConfig { radius = 0.01f, height = 0.06f };
                case ParelAvatarColliderSlot.FingerLittleL: return colliderFingerLittleL ??= new ColliderConfig { radius = 0.009f, height = 0.05f };
                default: return colliderFingerLittleR ??= new ColliderConfig { radius = 0.009f, height = 0.05f };
            }
        }

        /// <summary>The humanoid bone an automatic collider follows.</summary>
        public static HumanBodyBones ColliderBone(ParelAvatarColliderSlot slot)
        {
            switch (slot)
            {
                case ParelAvatarColliderSlot.Head: return HumanBodyBones.Head;
                case ParelAvatarColliderSlot.Torso: return HumanBodyBones.Chest;
                case ParelAvatarColliderSlot.HandL: return HumanBodyBones.LeftHand;
                case ParelAvatarColliderSlot.HandR: return HumanBodyBones.RightHand;
                case ParelAvatarColliderSlot.FootL: return HumanBodyBones.LeftFoot;
                case ParelAvatarColliderSlot.FootR: return HumanBodyBones.RightFoot;
                case ParelAvatarColliderSlot.FingerIndexL: return HumanBodyBones.LeftIndexDistal;
                case ParelAvatarColliderSlot.FingerIndexR: return HumanBodyBones.RightIndexDistal;
                case ParelAvatarColliderSlot.FingerMiddleL: return HumanBodyBones.LeftMiddleDistal;
                case ParelAvatarColliderSlot.FingerMiddleR: return HumanBodyBones.RightMiddleDistal;
                case ParelAvatarColliderSlot.FingerRingL: return HumanBodyBones.LeftRingDistal;
                case ParelAvatarColliderSlot.FingerRingR: return HumanBodyBones.RightRingDistal;
                case ParelAvatarColliderSlot.FingerLittleL: return HumanBodyBones.LeftLittleDistal;
                default: return HumanBodyBones.RightLittleDistal;
            }
        }

        public bool AllowUserScaling
        {
            get => allowUserScaling;
            set => allowUserScaling = value;
        }

        public float MinUserScale
        {
            get => Mathf.Clamp(minUserScale, 0.05f, 1f);
            set => minUserScale = value;
        }

        public float MaxUserScale
        {
            get => Mathf.Clamp(maxUserScale, 1f, 20f);
            set => maxUserScale = value;
        }

        /// <summary>The avatar's Blueprint ID, kept on the Pipeline Manager next to the descriptor.</summary>
        public string BlueprintId
        {
            get
            {
                ParelPipelineManager pipeline = GetComponent<ParelPipelineManager>();
                if (pipeline != null && !string.IsNullOrEmpty(pipeline.blueprintId)) return pipeline.blueprintId;
                return blueprintId ?? string.Empty;
            }
            set
            {
                ParelPipelineManager pipeline = GetComponent<ParelPipelineManager>();
                if (pipeline != null) pipeline.blueprintId = value ?? string.Empty;
                blueprintId = value ?? string.Empty;
            }
        }

        public bool IsHumanoid => avatarAnimator != null && avatarAnimator.isHuman;

        public Transform Head => headOverride != null ? headOverride : ResolveBone(HumanBodyBones.Head);
        public Transform LeftHand => leftHandOverride != null ? leftHandOverride : ResolveBone(HumanBodyBones.LeftHand);
        public Transform RightHand => rightHandOverride != null ? rightHandOverride : ResolveBone(HumanBodyBones.RightHand);

        private Transform ResolveBone(HumanBodyBones bone) => IsHumanoid ? avatarAnimator.GetBoneTransform(bone) : null;

        // =========================================================================================
        // Layout upgrade (SDK 1.0 -> VRChat-style layout)
        // =========================================================================================

        /// <summary>
        /// Copies an SDK 1.0 descriptor's settings into the current layout (Eye Look rotation states,
        /// Customize switches for layers and expressions, the Face Mesh for jaw flap). Safe to call
        /// repeatedly; does nothing once upgraded. Called by the editor; uploaded 1.0 avatars keep
        /// working through the compatibility properties without it.
        /// </summary>
        public void UpgradeLayout()
        {
            if (layoutUpgraded) return;

            // Eye Look.
            CustomEyeLookSettings s = EyeLookSettings;
            bool hadCustomEyes = customEyeLook;
            bool hadBlink = enableBlink && eyelidsSkinnedMesh != null && !string.IsNullOrEmpty(blinkBlendShape);
            s.eyeMovement = new EyeMovements { confidence = Mathf.Clamp01(eyeShyConfident), excitement = Mathf.Clamp01(eyeCalmExcited) };
            if (hadCustomEyes)
            {
                s.leftEye = leftEye;
                s.rightEye = rightEye;
            }
            if (hadCustomEyes || hadBlink)
            {
                enableEyeLook = true;
                ResetEyeRotationStates(eyeLookUp, eyeLookDown, eyeLookIn, eyeLookOut);
            }
            if (hadBlink)
            {
                s.eyelidType = EyelidType.Blendshapes;
                s.eyelidsSkinnedMesh = eyelidsSkinnedMesh;
                Mesh mesh = eyelidsSkinnedMesh.sharedMesh;
                s.eyelidsBlendshapes = new[]
                {
                    mesh != null && !string.IsNullOrEmpty(blinkBlendShape) ? mesh.GetBlendShapeIndex(blinkBlendShape) : -1,
                    mesh != null && !string.IsNullOrEmpty(lookingUpBlendShape) ? mesh.GetBlendShapeIndex(lookingUpBlendShape) : -1,
                    mesh != null && !string.IsNullOrEmpty(lookingDownBlendShape) ? mesh.GetBlendShapeIndex(lookingDownBlendShape) : -1,
                };
            }
            else if (!enableBlink)
            {
                enableEyeLook = true;
                s.eyelidType = EyelidType.None;
            }

            // Playable layers.
            customizeAnimationLayers = HasLegacyLayers();
            baseAnimationLayers = DefaultLayers(BaseLayerTypes);
            specialAnimationLayers = DefaultLayers(SpecialLayerTypes);
            if (customizeAnimationLayers)
            {
                foreach (ParelAvatarLayer type in BaseLayerTypes) CopyLegacy(baseAnimationLayers, type);
                foreach (ParelAvatarLayer type in SpecialLayerTypes) CopyLegacy(specialAnimationLayers, type);
            }

            // Expressions.
            customExpressions = expressionsMenu != null || expressionParameters != null;

            // Jaw flap blend shape now uses the Face Mesh.
            if (jawFlapMesh != null)
            {
                if (visemeSkinnedMesh == null || lipSync == LipSyncStyle.JawFlapBlendShape) visemeSkinnedMesh = jawFlapMesh;
                jawFlapMesh = null;
            }

            layoutUpgraded = true;
        }

        private void CopyLegacy(CustomAnimLayer[] list, ParelAvatarLayer type)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].type != type) continue;
                RuntimeAnimatorController controller = GetLegacyLayer(type);
                list[i].animatorController = controller;
                list[i].isDefault = controller == null;
            }
        }

        // =========================================================================================
        // Auto detection
        // =========================================================================================

        /// <summary>
        /// Fills in anything still unset from the rig itself: the Animator, the View Position (between
        /// the eyes, or just in front of the head), the face mesh + visemes and the blink shape.
        /// Never overwrites a field the creator has already set explicitly.
        /// </summary>
        public void AutoDetect()
        {
            if (avatarAnimator == null) avatarAnimator = GetComponentInChildren<Animator>();
            if (!viewPointInitialized) AutoDetectViewPoint();
            // Legacy field the old Build Kit tab still checks; the View Position above is what's used.
            if (viewPosition == null && Head != null) viewPosition = Head;
            AutoDetectLipSync(false);
            AutoDetectBlink(false);
        }

        /// <summary>Puts the View Position between the eyes (or in front of the head if the rig has no eye bones).</summary>
        public void AutoDetectViewPoint()
        {
            Vector3? world = null;
            if (IsHumanoid)
            {
                Transform left = avatarAnimator.GetBoneTransform(HumanBodyBones.LeftEye);
                Transform right = avatarAnimator.GetBoneTransform(HumanBodyBones.RightEye);
                if (left != null && right != null)
                {
                    world = Vector3.Lerp(left.position, right.position, 0.5f) + transform.forward * 0.02f;
                }
                else if (Head != null)
                {
                    float scale = Mathf.Max(0.01f, transform.lossyScale.y);
                    world = Head.position + transform.up * (0.07f * scale) + transform.forward * (0.08f * scale);
                }
            }

            if (world.HasValue) ViewPoint = transform.InverseTransformPoint(world.Value);
        }

        /// <summary>Finds the face mesh and maps the 15 visemes by blend shape name (VRChat's "Auto Detect!").</summary>
        public void AutoDetectLipSync(bool overwrite)
        {
            if (visemeSkinnedMesh == null || overwrite)
            {
                SkinnedMeshRenderer face = ParelVisemeAutoMapper.FindFaceMesh(gameObject);
                if (face != null) visemeSkinnedMesh = face;
            }

            if (visemeSkinnedMesh != null)
            {
                string[] detected = ParelVisemeAutoMapper.DetectVisemes(visemeSkinnedMesh.sharedMesh);
                string[] current = VisemeBlendShapes;
                int found = 0;
                for (int i = 0; i < current.Length; i++)
                {
                    if ((overwrite || string.IsNullOrEmpty(current[i])) && !string.IsNullOrEmpty(detected[i])) current[i] = detected[i];
                    if (!string.IsNullOrEmpty(current[i])) found++;
                }
                if (found > 2)
                {
                    if (lipSync == LipSyncStyle.Default || overwrite) lipSync = LipSyncStyle.VisemeBlendShape;
                    return;
                }
            }

            if (!overwrite) return;
            // No usable visemes: fall back like VRChat does -- jaw flap blend shape, then jaw bone.
            if (visemeSkinnedMesh != null && visemeSkinnedMesh.sharedMesh != null && visemeSkinnedMesh.sharedMesh.blendShapeCount > 0)
            {
                lipSync = LipSyncStyle.JawFlapBlendShape;
                return;
            }
            Transform jaw = ResolveBone(HumanBodyBones.Jaw);
            if (jaw != null)
            {
                lipSync = LipSyncStyle.JawFlapBone;
                jawBone = jaw;
            }
        }

        /// <summary>Finds the "both eyes closed" blend shape and sets Eye Look's eyelids to it.</summary>
        public void AutoDetectBlink(bool overwrite)
        {
            SkinnedMeshRenderer mesh = visemeSkinnedMesh != null ? visemeSkinnedMesh : ParelVisemeAutoMapper.FindFaceMesh(gameObject);
            if (mesh == null || mesh.sharedMesh == null) return;
            string blink = ParelVisemeAutoMapper.DetectBlink(mesh.sharedMesh);

            if (!layoutUpgraded)
            {
                if (eyelidsSkinnedMesh == null || overwrite) eyelidsSkinnedMesh = mesh;
                if ((overwrite || string.IsNullOrEmpty(blinkBlendShape)) && !string.IsNullOrEmpty(blink)) blinkBlendShape = blink;
                return;
            }

            CustomEyeLookSettings s = EyeLookSettings;
            if (string.IsNullOrEmpty(blink)) return;
            if (!overwrite && s.eyelidType == EyelidType.Blendshapes && s.eyelidsSkinnedMesh != null) return;
            s.eyelidType = EyelidType.Blendshapes;
            s.eyelidsSkinnedMesh = mesh;
            if (s.eyelidsBlendshapes == null || s.eyelidsBlendshapes.Length != 3) s.eyelidsBlendshapes = new[] { -1, -1, -1 };
            s.eyelidsBlendshapes[0] = mesh.sharedMesh.GetBlendShapeIndex(blink);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!layoutUpgraded) UpgradeLayout();
        }

        private void Reset()
        {
            layoutUpgraded = true;
            customizeAnimationLayers = false;
            customExpressions = false;
            baseAnimationLayers = DefaultLayers(BaseLayerTypes);
            specialAnimationLayers = DefaultLayers(SpecialLayerTypes);
            if (GetComponent<ParelPipelineManager>() == null) gameObject.AddComponent<ParelPipelineManager>();
        }
#endif

        private void OnDrawGizmosSelected()
        {
            Vector3 world = transform.TransformPoint(ViewPoint);
            Gizmos.color = new Color(0.55f, 0.35f, 1f, 0.9f);
            Gizmos.DrawWireSphere(world, 0.02f * Mathf.Max(0.01f, transform.lossyScale.y));
            Gizmos.DrawLine(world, world + transform.forward * 0.08f * Mathf.Max(0.01f, transform.lossyScale.y));
        }
    }
}
