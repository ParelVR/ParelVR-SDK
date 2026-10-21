using System;
using System.Collections.Generic;
using ParelVR.AvatarSDK;
using ParelVR.Volt.Players;
using ParelVR.Volt.Interaction;
using ParelVR.VoltHost;
using ParelVR.WorldSDK;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// The avatar under test, worn by the simulator's player the way ParelVR runs an SDK avatar: the Base layer is the
    /// Animator's own controller, Additive / Gesture / Action / FX / Sitting run on top as playable layers, Expression
    /// Parameters and the built-in parameters feed them, the state behaviours act, Motion Bones and contacts write their
    /// parameters here, and the Expressions Menu opens on the radial menu (R on desktop, the left menu button in VR).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    internal sealed class SimulatedAvatar : MonoBehaviour, IParelAvatarParameterSink, IVoltAvatarService, IParelAvatarBehaviourHandler
    {
        private const string SavedPrefix = "ParelVR.SDK.Simulator.";
        private const float SittingBlendSeconds = 0.25f;
        private const float ButtonHoldSeconds = 0.25f;

        private sealed class Slot
        {
            public ParelExpressionParameters.Parameter Def;
            public float Value;
        }

        private struct AnimParam
        {
            public int Hash;
            public AnimatorControllerParameterType Type;
        }

        private sealed class LayerState
        {
            public ParelAvatarLayer Layer;
            public AnimatorControllerPlayable Playable;
            public AnimationPlayableOutput Output;
            public readonly Dictionary<string, AnimParam> Params = new Dictionary<string, AnimParam>(StringComparer.Ordinal);
            public float Weight = 1f, Goal = 1f, Speed;
            public float[] InnerWeights, InnerGoals, InnerSpeeds;
        }

        private static readonly ParelAvatarLayer[] GraphLayers =
        {
            ParelAvatarLayer.Sitting, ParelAvatarLayer.Additive, ParelAvatarLayer.Gesture, ParelAvatarLayer.Action, ParelAvatarLayer.FX,
        };

        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _builtins = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _extras = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, AnimParam> _baseParams = new Dictionary<string, AnimParam>(StringComparer.Ordinal);
        private readonly List<LayerState> _layers = new List<LayerState>();
        private readonly List<SkinnedMeshRenderer> _skins = new List<SkinnedMeshRenderer>();

        private ParelAvatarDescriptor _descriptor;
        private Animator _animator;
        private PlayableGraph _graph;
        private bool _drivesBase;
        private SimulatedPlayer _player;
        private SimulatedRadialMenu _menu;
        private IParelAvatarBehaviourHandler _previousHandler;
        private string _key;
        private float _scale = 1f;
        private float _lastYaw;
        private bool _hasYaw;
        private int _gestureLeft, _gestureRight;
        private string _buttonParameter;
        private float _buttonRelease;

        // First person: the head is shrunk for the player's own view and shown again for mirrors.
        private Transform _headBone;
        private Vector3 _headScale;
        private bool _headHidden;

        // A humanoid without a Base layer is posed here, so it does not stand in its import pose.
        private HumanPoseHandler _poseHandler;
        private HumanPose _pose;
        private bool _proceduralBody;
        private float _walkPhase;
        private readonly Dictionary<string, int> _muscles = new Dictionary<string, int>(StringComparer.Ordinal);

        public float EyeHeight { get; private set; } = 1.6f;
        /// <summary>How far in front of the avatar's origin the eyes are.</summary>
        public float ViewForward { get; private set; }
        public string AvatarId { get; private set; } = "simulated";
        public bool MenuOpen => _menu != null && _menu.IsOpen;
        public bool LocomotionDisabled { get; private set; }
        public bool IsLocalAvatar => true;
        public ParelAvatarDescriptor Descriptor => _descriptor;

        public string Hint => MenuOpen
            ? "Move the mouse to pick    click choose    right click back    R close"
            : _testHand != null
                ? "Test hand: move the mouse to touch    hold click to grab Motion Bones    wheel nearer / further    right drag turn    T put away"
                : "R expressions    Shift + F1-F8 gestures    V third person    T test hand";

        /// <summary>The menu or the test hand has the mouse: no looking around, clicks go to them.</summary>
        public bool CapturesMouse => MenuOpen || _testHand != null;
        public bool ThirdPerson { get; private set; }

        private List<GameObject> _standardContacts = new List<GameObject>();
        private Action<bool, float, float> _previousHaptics;
        private SimulatedTestHand _testHand;

        // ── setup ────────────────────────────────────────────────────────────

        /// <summary>Puts the runtime on an avatar. The avatar keeps it until play mode ends.</summary>
        public static SimulatedAvatar Wear(ParelAvatarDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            SimulatedAvatar avatar = descriptor.GetComponent<SimulatedAvatar>();
            if (avatar == null) avatar = descriptor.gameObject.AddComponent<SimulatedAvatar>();
            avatar.Initialize(descriptor);
            return avatar;
        }

        private void Initialize(ParelAvatarDescriptor descriptor)
        {
            _descriptor = descriptor;
            _animator = descriptor.AvatarAnimator != null ? descriptor.AvatarAnimator : GetComponentInChildren<Animator>();
            _scale = Mathf.Max(0.0001f, transform.lossyScale.y);
            AvatarId = !string.IsNullOrEmpty(descriptor.BlueprintId) ? descriptor.BlueprintId : "simulated";
            _key = !string.IsNullOrEmpty(descriptor.BlueprintId) ? descriptor.BlueprintId : descriptor.gameObject.name;

            Vector3 view = descriptor.ViewPoint;
            if (view.y < 0.2f)
            {
                Transform head = descriptor.Head;
                view = head != null ? transform.InverseTransformPoint(head.position) + new Vector3(0f, 0.06f, 0.08f) : new Vector3(0f, 1.6f, 0.08f);
            }
            EyeHeight = view.y * _scale;
            ViewForward = Mathf.Clamp(view.z * _scale, -0.3f, 0.3f);

            if (_animator != null)
            {
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                // In ParelVR the Base layer runs as the Animator's own controller.
                if (descriptor.BaseLayer != null) _animator.runtimeAnimatorController = descriptor.BaseLayer;
                _proceduralBody = _animator.isHuman && _animator.runtimeAnimatorController == null;
                if (_proceduralBody) PrepareProceduralBody();
            }

            BuildSlots();
            SeedBuiltins();
            BuildLayers();
            BuildBaseParams();
            foreach (Slot slot in _slots.Values) Apply(slot.Def.name, slot.Value);
            foreach (KeyValuePair<string, float> pair in _builtins) Apply(pair.Key, pair.Value);

            _previousHandler = ParelAvatarBehaviourHooks.Handler;
            ParelAvatarBehaviourHooks.Handler = this;
            // Motion Bones and contacts that already looked for this avatar's parameters look again.
            ParelAvatarSinks.Changed();
            ParelContactSystem.ClearSinkCache();
            ParelAvatarDynamicsRunner.EnsureRunning();
            // The contacts every avatar carries in ParelVR (head, torso, hands, feet, fingers), for receivers that react to the wearer.
            if (_animator != null) _standardContacts = ParelStandardContacts.Create(descriptor, _animator, transform, true);

            _headBone = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Head) : descriptor.Head;
            if (_headBone != null) _headScale = _headBone.localScale;
            GetComponentsInChildren(true, _skins);
            // Each camera skins the meshes again, so the shrunk head shows in mirrors and not in your own view.
            foreach (SkinnedMeshRenderer skin in _skins) skin.forceMatrixRecalculationPerRender = true;
            ParelVRMirror.BeforeMirrorRender += ShowHead;
            ParelVRMirror.AfterMirrorRender += HideHead;

            _menu = SimulatedRadialMenu.Create(this, descriptor.ExpressionsMenu);
            Debug.Log("[Avatar Simulator] Wearing " + descriptor.gameObject.name + ": " + _slots.Count + " expression parameters, " + _layers.Count + " playable layers" +
                      (descriptor.BaseLayer == null ? " (no Base layer: the body is posed by the simulator)." : "."));
        }

        internal void Attach(SimulatedPlayer player)
        {
            _player = player;
            // The avatar stands where the player stands and turns with it.
            transform.SetParent(player.transform, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            if (_menu != null) _menu.Player = player;
            if (ParelContactSystem.HapticHandler != (Action<bool, float, float>)Buzz)
            {
                _previousHaptics = ParelContactSystem.HapticHandler;
                ParelContactSystem.HapticHandler = Buzz;
            }
            HideHead();
        }

        private void Buzz(bool left, float strength, float seconds) => _player?.PlayHaptics(left ? VoltHand.Left : VoltHand.Right, strength, seconds);

        internal void Detach()
        {
            ShowHead();
            if (_menu != null && _menu.IsOpen) _menu.Close();
            PutAwayTestHand();
            _player = null;
        }

        private void BuildSlots()
        {
            _slots.Clear();
            ParelExpressionParameters asset = _descriptor.ExpressionParameters;
            if (asset == null || asset.parameters == null) return;
            for (int i = 0; i < asset.parameters.Count && i < ParelExpressionParameters.MaxParameters; i++)
            {
                ParelExpressionParameters.Parameter def = asset.parameters[i];
                if (def == null || string.IsNullOrEmpty(def.name) || _slots.ContainsKey(def.name) || ParelAvatarBuiltinParameters.IsBuiltin(def.name)) continue;
                float value = ParelExpressionParameters.Sanitize(def.valueType, def.defaultValue);
                if (def.saved) value = ParelExpressionParameters.Sanitize(def.valueType, PlayerPrefs.GetFloat(SavedKey(def.name), value));
                _slots[def.name] = new Slot { Def = def, Value = value };
            }
        }

        private void SeedBuiltins()
        {
            _builtins.Clear();
            foreach (ParelAvatarBuiltinParameters.Info info in ParelAvatarBuiltinParameters.All) _builtins[info.Name] = 0f;
            _builtins["IsLocal"] = 1f;
            _builtins["AvatarVersion"] = 3f;
            _builtins["IsAnimatorEnabled"] = 1f;
            _builtins["Grounded"] = 1f;
            _builtins["Upright"] = 1f;
            _builtins["ScaleFactor"] = 1f;
            _builtins["ScaleFactorInverse"] = 1f;
            _builtins["TrackingType"] = 3f;
            _builtins["EyeHeightAsMeters"] = EyeHeight;
            _builtins["EyeHeightAsPercent"] = Mathf.InverseLerp(0.2f, 5f, EyeHeight);
        }

        private void BuildLayers()
        {
            _layers.Clear();
            if (_animator == null) return;
            bool any = false;
            foreach (ParelAvatarLayer layer in GraphLayers)
                if (_descriptor.GetLayer(layer) != null) any = true;
            if (!any) return;
            try
            {
                _graph = PlayableGraph.Create("ParelVR Avatar Simulator (" + name + ")");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                ushort order = 500;
                foreach (ParelAvatarLayer layer in GraphLayers)
                {
                    order += 10;
                    RuntimeAnimatorController controller = _descriptor.GetLayer(layer);
                    if (controller == null) continue;
                    var state = new LayerState { Layer = layer };
                    state.Playable = AnimatorControllerPlayable.Create(_graph, controller);
                    state.Output = AnimationPlayableOutput.Create(_graph, layer.ToString(), _animator);
                    state.Output.SetSourcePlayable(state.Playable);
                    // Each layer starts from what ran before it, so it only changes what its own clips animate.
                    state.Output.SetAnimationStreamSource(AnimationStreamSource.PreviousInputs);
                    state.Output.SetSortingOrder(order);
                    float initial = layer == ParelAvatarLayer.Action || layer == ParelAvatarLayer.Sitting ? 0f : 1f;
                    state.Weight = state.Goal = initial;
                    state.Output.SetWeight(initial);
                    int count = state.Playable.GetParameterCount();
                    for (int i = 0; i < count; i++)
                    {
                        AnimatorControllerParameter p = state.Playable.GetParameter(i);
                        state.Params[p.name] = new AnimParam { Hash = p.nameHash, Type = p.type };
                    }
                    int layers = state.Playable.GetLayerCount();
                    state.InnerWeights = new float[layers];
                    state.InnerGoals = new float[layers];
                    state.InnerSpeeds = new float[layers];
                    for (int i = 0; i < layers; i++) state.InnerWeights[i] = state.InnerGoals[i] = state.Playable.GetLayerWeight(i);
                    _layers.Add(state);
                }
                _graph.Play();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Avatar Simulator] The playable layers could not start: " + e.Message);
                if (_graph.IsValid()) _graph.Destroy();
                _layers.Clear();
            }
        }

        private void BuildBaseParams()
        {
            _baseParams.Clear();
            _drivesBase = _descriptor.BaseLayer != null && _animator != null && _animator.runtimeAnimatorController == _descriptor.BaseLayer;
            if (!_drivesBase) return;
            foreach (AnimatorControllerParameter p in _animator.parameters) _baseParams[p.name] = new AnimParam { Hash = p.nameHash, Type = p.type };
        }

        private string SavedKey(string parameter) => SavedPrefix + _key + "." + parameter;

        // ── parameters ───────────────────────────────────────────────────────

        public bool HasParameter(string parameter) => parameter != null && _slots.ContainsKey(parameter);

        public bool TryGetParameter(string parameter, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(parameter)) return false;
            if (_slots.TryGetValue(parameter, out Slot slot)) { value = slot.Value; return true; }
            if (_builtins.TryGetValue(parameter, out value)) return true;
            if (_extras.TryGetValue(parameter, out value)) return true;
            value = 0f;
            return TryFindAnimParam(parameter, out _);
        }

        public ParelParameterKind GetParameterKind(string parameter)
        {
            if (string.IsNullOrEmpty(parameter)) return ParelParameterKind.Unknown;
            if (_slots.TryGetValue(parameter, out Slot slot))
                return slot.Def.valueType == ParelExpressionParameters.ValueType.Bool ? ParelParameterKind.Bool
                    : slot.Def.valueType == ParelExpressionParameters.ValueType.Int ? ParelParameterKind.Int : ParelParameterKind.Float;
            foreach (ParelAvatarBuiltinParameters.Info info in ParelAvatarBuiltinParameters.All)
                if (info.Name == parameter)
                    return info.Type == ParelAvatarBuiltinParameters.Kind.Bool ? ParelParameterKind.Bool
                        : info.Type == ParelAvatarBuiltinParameters.Kind.Int ? ParelParameterKind.Int : ParelParameterKind.Float;
            if (!TryFindAnimParam(parameter, out AnimParam ap)) return ParelParameterKind.Unknown;
            return ap.Type == AnimatorControllerParameterType.Int ? ParelParameterKind.Int
                : ap.Type == AnimatorControllerParameterType.Float ? ParelParameterKind.Float : ParelParameterKind.Bool;
        }

        private bool TryFindAnimParam(string parameter, out AnimParam param)
        {
            foreach (LayerState layer in _layers)
                if (layer.Params.TryGetValue(parameter, out param)) return true;
            return _baseParams.TryGetValue(parameter, out param);
        }

        /// <summary>Drivers, contacts and Motion Bones write here. Built-ins are read-only.</summary>
        public void SetParameter(string parameter, float value, ParelParameterSource source)
        {
            if (string.IsNullOrEmpty(parameter) || ParelAvatarBuiltinParameters.IsBuiltin(parameter)) return;
            if (_slots.TryGetValue(parameter, out Slot slot))
            {
                SetSlot(slot, value);
                return;
            }
            ParelParameterKind kind = GetParameterKind(parameter);
            if (kind == ParelParameterKind.Unknown) return;
            float sanitized = kind == ParelParameterKind.Bool ? (value >= 0.5f ? 1f : 0f)
                : kind == ParelParameterKind.Int ? Mathf.Clamp(Mathf.Round(value), 0f, 255f)
                : float.IsNaN(value) ? 0f : Mathf.Clamp(value, -1f, 1f);
            if (_extras.TryGetValue(parameter, out float current) && Mathf.Approximately(current, sanitized)) return;
            _extras[parameter] = sanitized;
            Apply(parameter, sanitized);
        }

        /// <summary>Sets an Expression Parameter (the radial menu and scripts), or an animator-only one.</summary>
        public void SetValue(string parameter, float value)
        {
            if (parameter == null) return;
            if (_slots.TryGetValue(parameter, out Slot slot)) SetSlot(slot, value);
            else SetParameter(parameter, value, ParelParameterSource.Other);
        }

        public float GetValue(string parameter) => TryGetParameter(parameter, out float value) ? value : 0f;

        private void SetSlot(Slot slot, float value)
        {
            float sanitized = ParelExpressionParameters.Sanitize(slot.Def.valueType, value);
            if (Mathf.Approximately(slot.Value, sanitized)) return;
            slot.Value = sanitized;
            Apply(slot.Def.name, sanitized);
            if (slot.Def.saved) PlayerPrefs.SetFloat(SavedKey(slot.Def.name), sanitized);
        }

        private void SetBuiltin(string parameter, float value)
        {
            if (_builtins.TryGetValue(parameter, out float current) && Mathf.Abs(current - value) < 0.0001f) return;
            _builtins[parameter] = value;
            Apply(parameter, value);
        }

        /// <summary>Puts every Expression Parameter back to its default.</summary>
        public void ResetToDefaults()
        {
            foreach (Slot slot in _slots.Values) SetSlot(slot, slot.Def.defaultValue);
        }

        /// <summary>A Button control: the value holds briefly, then goes back to 0.</summary>
        public void PressButton(string parameter, float value)
        {
            if (string.IsNullOrEmpty(parameter)) return;
            SetValue(parameter, value);
            _buttonParameter = parameter;
            _buttonRelease = Time.unscaledTime + ButtonHoldSeconds;
        }

        private void Apply(string parameter, float value)
        {
            foreach (LayerState layer in _layers)
                if (layer.Params.TryGetValue(parameter, out AnimParam ap)) Write(layer.Playable, ap, value);
            if (_drivesBase && _animator != null && _baseParams.TryGetValue(parameter, out AnimParam bp))
            {
                switch (bp.Type)
                {
                    case AnimatorControllerParameterType.Float: _animator.SetFloat(bp.Hash, value); break;
                    case AnimatorControllerParameterType.Int: _animator.SetInteger(bp.Hash, Mathf.RoundToInt(value)); break;
                    case AnimatorControllerParameterType.Bool: _animator.SetBool(bp.Hash, value >= 0.5f); break;
                    case AnimatorControllerParameterType.Trigger:
                        if (value >= 0.5f) _animator.SetTrigger(bp.Hash);
                        else _animator.ResetTrigger(bp.Hash);
                        break;
                }
            }
        }

        private static void Write(AnimatorControllerPlayable playable, AnimParam ap, float value)
        {
            switch (ap.Type)
            {
                case AnimatorControllerParameterType.Float: playable.SetFloat(ap.Hash, value); break;
                case AnimatorControllerParameterType.Int: playable.SetInteger(ap.Hash, Mathf.RoundToInt(value)); break;
                case AnimatorControllerParameterType.Bool: playable.SetBool(ap.Hash, value >= 0.5f); break;
                case AnimatorControllerParameterType.Trigger:
                    if (value >= 0.5f) playable.SetTrigger(ap.Hash);
                    else playable.ResetTrigger(ap.Hash);
                    break;
            }
        }

        // ── state behaviours ─────────────────────────────────────────────────

        private bool Mine(Animator animator) => animator != null && (animator == _animator || animator.GetComponentInParent<SimulatedAvatar>() == this);

        public void OnTrackingControl(Animator animator, ParelAnimatorTrackingControl control)
        {
            // The simulator has no IK to hand over; the body is animated either way.
            if (!Mine(animator)) _previousHandler?.OnTrackingControl(animator, control);
        }

        public void OnLocomotionControl(Animator animator, bool disableLocomotion)
        {
            if (Mine(animator)) LocomotionDisabled = disableLocomotion;
            else _previousHandler?.OnLocomotionControl(animator, disableLocomotion);
        }

        public void OnPlayableLayerControl(Animator animator, ParelPlayableLayerControl control)
        {
            if (!Mine(animator)) { _previousHandler?.OnPlayableLayerControl(animator, control); return; }
            LayerState layer = FindLayer(ToLayer(control.layer));
            if (layer == null) return;
            layer.Goal = Mathf.Clamp01(control.goalWeight);
            layer.Speed = control.blendDuration > 0.0001f ? Mathf.Abs(layer.Goal - layer.Weight) / control.blendDuration : 0f;
            if (layer.Speed > 0f) return;
            layer.Weight = layer.Goal;
            layer.Output.SetWeight(layer.Weight);
        }

        public void OnAnimatorLayerControl(Animator animator, ParelAnimatorLayerControl control)
        {
            if (!Mine(animator)) { _previousHandler?.OnAnimatorLayerControl(animator, control); return; }
            LayerState layer = FindLayer(ToLayer(control.playable));
            int index = control.layer;
            if (layer == null || layer.InnerWeights == null || index < 0 || index >= layer.InnerWeights.Length) return;
            layer.InnerGoals[index] = Mathf.Clamp01(control.goalWeight);
            layer.InnerSpeeds[index] = control.blendDuration > 0.0001f ? Mathf.Abs(layer.InnerGoals[index] - layer.InnerWeights[index]) / control.blendDuration : 0f;
            if (layer.InnerSpeeds[index] > 0f) return;
            layer.InnerWeights[index] = layer.InnerGoals[index];
            layer.Playable.SetLayerWeight(index, layer.InnerWeights[index]);
        }

        public void OnTemporaryPoseSpace(Animator animator, ParelAnimatorTemporaryPoseSpace poseSpace)
        {
            if (!Mine(animator)) _previousHandler?.OnTemporaryPoseSpace(animator, poseSpace);
        }

        private static ParelAvatarLayer ToLayer(ParelBlendableLayer blendable)
        {
            switch (blendable)
            {
                case ParelBlendableLayer.FX: return ParelAvatarLayer.FX;
                case ParelBlendableLayer.Gesture: return ParelAvatarLayer.Gesture;
                case ParelBlendableLayer.Additive: return ParelAvatarLayer.Additive;
                default: return ParelAvatarLayer.Action;
            }
        }

        private LayerState FindLayer(ParelAvatarLayer layer)
        {
            foreach (LayerState state in _layers)
                if (state.Layer == layer) return state;
            return null;
        }

        // ── input ────────────────────────────────────────────────────────────

        /// <summary>Keys the avatar uses. True when the key was taken.</summary>
        internal bool KeyPressed(KeyCode key)
        {
            if (_menu == null) return false;
            if (key == KeyCode.R)
            {
                if (_menu.IsOpen) _menu.Close();
                else _menu.Open();
                return true;
            }
            if (_menu.IsOpen && key == KeyCode.Escape)
            {
                _menu.Close();
                return true;
            }
            if (key == KeyCode.V && !_player.InVRNow)
            {
                ThirdPerson = !ThirdPerson;
                if (ThirdPerson) ShowHead();
                else HideHead();
                return true;
            }
            if (key == KeyCode.T && !_player.InVRNow)
            {
                if (_testHand != null) PutAwayTestHand();
                else
                {
                    if (_menu.IsOpen) _menu.Close();
                    _testHand = SimulatedTestHand.Create(_player, this);
                    _player.ReleaseMouse();
                }
                return true;
            }
            if (_testHand != null && key == KeyCode.Escape)
            {
                PutAwayTestHand();
                return true;
            }
            if (key >= KeyCode.F1 && key <= KeyCode.F8 && _player != null)
            {
                int gesture = key - KeyCode.F1;
                bool left = _player.KeyHeld(KeyCode.LeftShift), right = _player.KeyHeld(KeyCode.RightShift);
                if (!left && !right) return false;
                if (left) _gestureLeft = gesture;
                if (right) _gestureRight = gesture;
                return true;
            }
            return false;
        }

        internal void MenuClick(int button) => _menu?.Click(button);

        /// <summary>Mouse buttons while the menu or the test hand has the mouse.</summary>
        internal void MouseButton(int button, bool down)
        {
            if (MenuOpen)
            {
                if (down) _menu.Click(button);
                return;
            }
            if (_testHand != null && button == 0) _testHand.Grabbing = down;
        }

        internal void Scroll(float notches) => _testHand?.Push(notches);

        private void PutAwayTestHand()
        {
            if (_testHand == null) return;
            _testHand.Destroy();
            _testHand = null;
            _player?.RestoreMouse();
        }

        /// <summary>Third person: the view moves behind the head, so you see the avatar as others do.</summary>
        internal void AdjustView(Transform view)
        {
            if (!ThirdPerson) return;
            Vector3 pivot = TryGetHead(out Vector3 head) ? head : view.position;
            float distance = Mathf.Clamp(EyeHeight * 1.1f, 0.8f, 4f);
            view.position = pivot - view.forward * distance + Vector3.up * 0.1f * EyeHeight;
        }

        // ── per frame ────────────────────────────────────────────────────────

        private void Update()
        {
            if (_player == null) return;
            float dt = Time.deltaTime;
            SampleBuiltins(dt);
            UpdateLayerWeights(dt);
            if (_buttonParameter != null && Time.unscaledTime >= _buttonRelease)
            {
                SetValue(_buttonParameter, 0f);
                _buttonParameter = null;
            }
            if (_player.InVRNow && _player.VRMenuToggle()) { if (_menu.IsOpen) _menu.Close(); else _menu.Open(); }
            _menu?.Tick(dt);
        }

        private void SampleBuiltins(float dt)
        {
            Vector3 velocity = _player.Velocity;
            Vector3 local = transform.InverseTransformDirection(velocity);
            SetBuiltin("VelocityX", local.x);
            SetBuiltin("VelocityY", local.y);
            SetBuiltin("VelocityZ", local.z);
            SetBuiltin("VelocityMagnitude", velocity.magnitude);
            SetBuiltin("Grounded", _player.Grounded || _player.Seated ? 1f : 0f);
            SetBuiltin("Seated", _player.Seated ? 1f : 0f);
            SetBuiltin("InStation", _player.Seated ? 1f : 0f);
            SetBuiltin("VRMode", _player.InVRNow ? 1f : 0f);
            float yaw = transform.eulerAngles.y;
            if (_hasYaw && dt > 0f) SetBuiltin("AngularY", Mathf.DeltaAngle(_lastYaw, yaw) / dt);
            _lastYaw = yaw;
            _hasYaw = true;

            if (_player.InVRNow)
            {
                float leftGrip = _player.GetGrip(VoltHand.Left), rightGrip = _player.GetGrip(VoltHand.Right);
                _gestureLeft = leftGrip > 0.5f ? 1 : _player.GetTrigger(VoltHand.Left) > 0.5f ? 3 : 0;
                _gestureRight = rightGrip > 0.5f ? 1 : _player.GetTrigger(VoltHand.Right) > 0.5f ? 3 : 0;
                SetBuiltin("GestureLeftWeight", _gestureLeft == 1 ? leftGrip : 0f);
                SetBuiltin("GestureRightWeight", _gestureRight == 1 ? rightGrip : 0f);
            }
            else
            {
                SetBuiltin("GestureLeftWeight", _gestureLeft == 1 ? 1f : 0f);
                SetBuiltin("GestureRightWeight", _gestureRight == 1 ? 1f : 0f);
            }
            SetBuiltin("GestureLeft", _gestureLeft);
            SetBuiltin("GestureRight", _gestureRight);
        }

        private void UpdateLayerWeights(float dt)
        {
            LayerState sitting = FindLayer(ParelAvatarLayer.Sitting);
            if (sitting != null)
            {
                float goal = GetValue("Seated") >= 0.5f ? 1f : 0f;
                if (!Mathf.Approximately(sitting.Goal, goal))
                {
                    sitting.Goal = goal;
                    sitting.Speed = 1f / SittingBlendSeconds;
                }
            }
            foreach (LayerState layer in _layers)
            {
                if (!Mathf.Approximately(layer.Weight, layer.Goal))
                {
                    layer.Weight = layer.Speed > 0f ? Mathf.MoveTowards(layer.Weight, layer.Goal, layer.Speed * dt) : layer.Goal;
                    layer.Output.SetWeight(layer.Weight);
                }
                if (layer.InnerWeights == null) continue;
                for (int i = 0; i < layer.InnerWeights.Length; i++)
                {
                    if (Mathf.Approximately(layer.InnerWeights[i], layer.InnerGoals[i])) continue;
                    layer.InnerWeights[i] = layer.InnerSpeeds[i] > 0f ? Mathf.MoveTowards(layer.InnerWeights[i], layer.InnerGoals[i], layer.InnerSpeeds[i] * dt) : layer.InnerGoals[i];
                    layer.Playable.SetLayerWeight(i, layer.InnerWeights[i]);
                }
            }
        }

        // After animation, before Motion Bones (they run last in the frame).
        private void LateUpdate()
        {
            if (_player == null) return;
            if (_proceduralBody) PoseProceduralBody(Time.deltaTime);
            if (!_player.InVRNow) LookWithHead(_player.Pitch);
            if (!_headHidden && !ThirdPerson) HideHead();
            _testHand?.Follow();
        }

        /// <summary>On desktop the view's up and down turn the neck and head, so mirrors show where you look.</summary>
        private void LookWithHead(float pitch)
        {
            if (_animator == null || !_animator.isHuman) return;
            Transform neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
            Transform head = _animator.GetBoneTransform(HumanBodyBones.Head);
            Vector3 axis = transform.right;
            float amount = Mathf.Clamp(pitch, -70f, 70f);
            if (neck != null) neck.rotation = Quaternion.AngleAxis(amount * 0.4f, axis) * neck.rotation;
            if (head != null) head.rotation = Quaternion.AngleAxis(amount * (neck != null ? 0.6f : 1f), axis) * head.rotation;
        }

        private void HideHead()
        {
            if (_headBone == null || _player == null || ThirdPerson) return;
            _headBone.localScale = _headScale * 0.0001f;
            _headHidden = true;
        }

        private void ShowHead()
        {
            if (_headBone == null) return;
            _headBone.localScale = _headScale;
            _headHidden = false;
        }

        // ── a body for humanoids without a Base layer ────────────────────────

        private void PrepareProceduralBody()
        {
            _poseHandler = new HumanPoseHandler(_animator.avatar, _animator.transform);
            for (int i = 0; i < HumanTrait.MuscleCount; i++) _muscles[HumanTrait.MuscleName[i]] = i;
            _pose = new HumanPose();
        }

        private void Muscle(string muscle, float value)
        {
            if (_muscles.TryGetValue(muscle, out int index)) _pose.muscles[index] = value;
        }

        /// <summary>Arms down at rest, a simple walk while moving.</summary>
        private void PoseProceduralBody(float dt)
        {
            if (_poseHandler == null) return;
            _poseHandler.GetHumanPose(ref _pose);
            Vector3 local = transform.InverseTransformDirection(_player.Velocity);
            float speed = new Vector2(local.x, local.z).magnitude;
            _walkPhase += dt * Mathf.Lerp(0f, 7f, Mathf.Clamp01(speed / 2f));
            float stride = Mathf.Clamp01(speed / 2.5f) * 0.55f * Mathf.Sign(local.z >= -0.1f ? 1f : -1f);
            float swing = Mathf.Sin(_walkPhase);
            Muscle("Left Arm Down-Up", -0.55f);
            Muscle("Right Arm Down-Up", -0.55f);
            Muscle("Left Forearm Stretch", 0.85f);
            Muscle("Right Forearm Stretch", 0.85f);
            Muscle("Left Arm Front-Back", 0.1f - swing * stride * 0.6f);
            Muscle("Right Arm Front-Back", 0.1f + swing * stride * 0.6f);
            Muscle("Left Upper Leg Front-Back", swing * stride);
            Muscle("Right Upper Leg Front-Back", -swing * stride);
            Muscle("Left Lower Leg Stretch", 1f - Mathf.Max(0f, -swing) * stride * 1.4f);
            Muscle("Right Lower Leg Stretch", 1f - Mathf.Max(0f, swing) * stride * 1.4f);
            _poseHandler.SetHumanPose(ref _pose);
        }

        // ── what the simulator's player reads ────────────────────────────────

        internal bool TryGetHead(out Vector3 position)
        {
            Transform head = _descriptor != null ? _descriptor.Head : null;
            position = head != null ? head.position : transform.position + Vector3.up * EyeHeight;
            return head != null;
        }

        internal bool TryGetTracking(VoltTrackingPoint point, out Vector3 position, out Quaternion rotation)
        {
            Transform bone = null;
            if (point == VoltTrackingPoint.LeftHand) bone = _descriptor.LeftHand;
            else if (point == VoltTrackingPoint.RightHand) bone = _descriptor.RightHand;
            // In VR the controllers are the hands; on desktop the avatar's hands are.
            if (bone == null || (_player != null && _player.InVRNow))
            {
                position = default;
                rotation = Quaternion.identity;
                return false;
            }
            position = bone.position;
            rotation = bone.rotation;
            return true;
        }

        internal bool TryGetBone(VoltBone bone, out Vector3 position, out Quaternion rotation)
        {
            Transform t = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(ToHuman(bone)) : null;
            if (t == null)
            {
                position = transform.position;
                rotation = transform.rotation;
                return false;
            }
            position = t.position;
            rotation = t.rotation;
            return true;
        }

        private static HumanBodyBones ToHuman(VoltBone bone)
        {
            switch (bone)
            {
                case VoltBone.Hips: return HumanBodyBones.Hips;
                case VoltBone.Spine: return HumanBodyBones.Spine;
                case VoltBone.Chest: return HumanBodyBones.Chest;
                case VoltBone.Neck: return HumanBodyBones.Neck;
                case VoltBone.Head: return HumanBodyBones.Head;
                case VoltBone.LeftShoulder: return HumanBodyBones.LeftShoulder;
                case VoltBone.LeftUpperArm: return HumanBodyBones.LeftUpperArm;
                case VoltBone.LeftLowerArm: return HumanBodyBones.LeftLowerArm;
                case VoltBone.LeftHand: return HumanBodyBones.LeftHand;
                case VoltBone.RightShoulder: return HumanBodyBones.RightShoulder;
                case VoltBone.RightUpperArm: return HumanBodyBones.RightUpperArm;
                case VoltBone.RightLowerArm: return HumanBodyBones.RightLowerArm;
                case VoltBone.RightHand: return HumanBodyBones.RightHand;
                case VoltBone.LeftUpperLeg: return HumanBodyBones.LeftUpperLeg;
                case VoltBone.LeftLowerLeg: return HumanBodyBones.LeftLowerLeg;
                case VoltBone.LeftFoot: return HumanBodyBones.LeftFoot;
                case VoltBone.LeftToes: return HumanBodyBones.LeftToes;
                case VoltBone.RightUpperLeg: return HumanBodyBones.RightUpperLeg;
                case VoltBone.RightLowerLeg: return HumanBodyBones.RightLowerLeg;
                case VoltBone.RightFoot: return HumanBodyBones.RightFoot;
                default: return HumanBodyBones.RightToes;
            }
        }

        // ── what world scripts read (VoltPlayer avatar members) ──────────────

        public bool TryGetFingerCurl(VoltPlayerHandle player, VoltHand hand, int finger, out float curl)
        {
            curl = 0f;
            if (player == null || !player.IsLocal || finger < 0 || finger > 4) return false;
            int gesture = hand == VoltHand.Left ? _gestureLeft : _gestureRight;
            // Neutral, Fist, Open, Point, Victory, RockNRoll, Gun, ThumbsUp; thumb first.
            switch (gesture)
            {
                case 1: curl = 1f; break;
                case 2: curl = 0f; break;
                case 3: curl = finger == 1 ? 0f : 1f; break;
                case 4: curl = finger == 1 || finger == 2 ? 0f : 1f; break;
                case 5: curl = finger == 1 || finger == 4 ? 0f : 1f; break;
                case 6: curl = finger == 0 || finger == 1 ? 0f : 1f; break;
                case 7: curl = finger == 0 ? 0f : 1f; break;
                default: curl = 0.2f; break;
            }
            return true;
        }

        public bool TryGetParameter(VoltPlayerHandle player, string parameter, out float value)
        {
            value = 0f;
            return player != null && player.IsLocal && TryGetParameter(parameter, out value);
        }

        public bool SetParameter(string parameter, float value)
        {
            if (!HasParameter(parameter)) return false;
            SetValue(parameter, value);
            return true;
        }

        // ── teardown ─────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            ParelVRMirror.BeforeMirrorRender -= ShowHead;
            ParelVRMirror.AfterMirrorRender -= HideHead;
            ShowHead();
            PutAwayTestHand();
            foreach (GameObject go in _standardContacts) if (go != null) Destroy(go);
            if (ParelContactSystem.HapticHandler == (Action<bool, float, float>)Buzz) ParelContactSystem.HapticHandler = _previousHaptics;
            if (ReferenceEquals(ParelAvatarBehaviourHooks.Handler, this)) ParelAvatarBehaviourHooks.Handler = _previousHandler;
            if (_graph.IsValid()) _graph.Destroy();
            _poseHandler?.Dispose();
            if (_menu != null) _menu.Destroy();
            PlayerPrefs.Save();
        }
    }

    /// <summary>
    /// Someone else's hand for testing on desktop: it follows the mouse at a distance you set with the wheel, touches
    /// the avatar's receivers as another player's hand and fingers would, bumps Motion Bones, and grabs them while the
    /// mouse button is held. Best used in third person (V).
    /// </summary>
    internal sealed class SimulatedTestHand : IParelMotionBoneGrabber
    {
        private const float Radius = 0.045f;

        public bool Grabbing;
        private SimulatedPlayer _player;
        private GameObject _hand, _someoneElse;
        private ParelContactSender _sender;
        private ParelMotionBoneCollider _collider;
        private Material _material;
        private float _depth;

        public static SimulatedTestHand Create(SimulatedPlayer player, SimulatedAvatar avatar)
        {
            var hand = new SimulatedTestHand { _player = player };
            // An owner that is not the avatar, so receivers and Motion Bones treat the hand as another person's.
            hand._someoneElse = new GameObject("[Test Hand Owner]") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(hand._someoneElse);
            hand._hand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hand._hand.name = "[Test Hand]";
            UnityEngine.Object.Destroy(hand._hand.GetComponent<Collider>());
            UnityEngine.Object.DontDestroyOnLoad(hand._hand);
            hand._hand.transform.localScale = Vector3.one * Radius * 2f;
            Shader shader = Shader.Find(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? "Universal Render Pipeline/Unlit" : "Unlit/Color");
            if (shader != null)
            {
                hand._material = new Material(shader) { color = new Color(1f, 0.55f, 0.75f), hideFlags = HideFlags.DontSave };
                if (hand._material.HasProperty("_BaseColor")) hand._material.SetColor("_BaseColor", hand._material.color);
                hand._hand.GetComponent<Renderer>().sharedMaterial = hand._material;
            }
            hand._sender = hand._hand.AddComponent<ParelContactSender>();
            hand._sender.radius = 0.5f; // of the sphere's own scale: Radius in metres
            hand._sender.collisionTags = new List<string> { "Hand", "HandR", "Finger", "FingerR", "FingerIndex", "FingerIndexR" };
            hand._sender.OwnerRootOverride = hand._someoneElse.transform;
            hand._collider = hand._hand.AddComponent<ParelMotionBoneCollider>();
            hand._collider.shapeType = ParelMotionBoneCollider.ShapeType.Sphere;
            hand._collider.radius = 0.5f;
            hand._collider.OwnerRootOverride = hand._someoneElse.transform;
            ParelMotionBoneSystem.RegisterGlobalCollider(hand._collider);
            ParelMotionBoneSystem.RegisterGrabber(hand);
            hand._depth = Mathf.Clamp(avatar.ThirdPerson ? avatar.EyeHeight * 1.1f : 0.6f, 0.3f, 4f);
            hand.Follow();
            return hand;
        }

        public void Push(float notches) => _depth = Mathf.Clamp(_depth - notches * 0.05f, 0.15f, 6f);

        public void Follow()
        {
            if (_hand == null || _player == null || _player.View == null) return;
            Ray ray = _player.View.ScreenPointToRay(_player.MousePosition());
            _hand.transform.position = ray.origin + ray.direction * _depth;
        }

        public Transform OwnerRoot => _someoneElse != null ? _someoneElse.transform : null;
        public Vector3 Position => _hand != null ? _hand.transform.position : Vector3.zero;
        float IParelMotionBoneGrabber.Radius => Radius;
        public bool IsGrabbing => Grabbing;
        public bool IsPosing => false;
        public bool IsValid => _hand != null;

        public void Destroy()
        {
            ParelMotionBoneSystem.UnregisterGrabber(this);
            if (_collider != null) ParelMotionBoneSystem.UnregisterGlobalCollider(_collider);
            if (_hand != null) UnityEngine.Object.Destroy(_hand);
            if (_someoneElse != null) UnityEngine.Object.Destroy(_someoneElse);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _hand = null;
        }
    }
}
