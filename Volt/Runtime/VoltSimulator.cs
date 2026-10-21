using System;
using System.Collections.Generic;
using System.IO;
using ParelVR.Volt.Core;
using ParelVR.Volt.Core.Runtime;
using ParelVR.Volt.Input;
using ParelVR.Volt.Interaction;
using ParelVR.Volt.Players;
using ParelVR.Volt.Storage;
using ParelVR.VoltHost;
using UnityEngine;
using UnityEngine.SceneManagement;
#if VOLT_SIM_XR
using UnityEngine.XR;
#endif
using VoltInstancePrivacy = ParelVR.Volt.Instance.VoltInstancePrivacy;

namespace ParelVR.Volt.Simulator
{
    using Scene = UnityEngine.SceneManagement.Scene;
    using Physics = UnityEngine.Physics;

    public sealed class VoltSimulatorOptions
    {
        /// <summary>The compiled module.</summary>
        public byte[] Module;
        /// <summary>VoltCapability bits the world is granted.</summary>
        public uint Capabilities;
        public string[] WebDomains;
        public string WorldName = "Editor World";
        /// <summary>Where simulated storage is kept. Defaults to the project's Library folder.</summary>
        public string StorageFolder;
        public Action<VoltErrorReport> Error;
        /// <summary>Level 0 info, 1 warning, 2 error.</summary>
        public Action<int, string, UnityEngine.Object> Log;
        public VoltDebugger Debugger;
        /// <summary>Test with a headset through XR Plug-in Management; without one the test runs on desktop.</summary>
        public bool VR;
    }

    /// <summary>
    /// Runs a world's scripts in the editor's play mode the way ParelVR runs them: the same VM, the same API
    /// implementation, with a stand-in player you move with the keyboard and mouse, an offline connection and
    /// storage in a local file. What works here works in ParelVR; what the simulator cannot show (other players,
    /// VR hands) is listed in the documentation.
    /// </summary>
    public static class VoltSimulator
    {
        private static SimulatedPlayer _player;
        private static VoltPlatformServices _services;

        public static bool IsRunning => World != null && World.IsLoaded;
        public static VoltWorld World { get; private set; }
        public static VoltSimulatorOptions Options { get; private set; }
        /// <summary>Set before play mode starts by tooling that wants to attach a debugger to the next run.</summary>
        public static VoltDebugger NextDebugger;
        public static event Action Started;
        public static event Action Stopped;

        /// <summary>Starts the scripts of the active scene. Call it in play mode.</summary>
        public static void Start(VoltSimulatorOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!Application.isPlaying) throw new InvalidOperationException("The Volt simulator runs in play mode.");
            Stop();

            Scene scene = SceneManager.GetActiveScene();
            var go = new GameObject("[Volt Player]");
            UnityEngine.Object.DontDestroyOnLoad(go); // outside the world's scene, so scripts cannot reach it, as in the client
            _player = go.AddComponent<SimulatedPlayer>();
            _player.Setup(scene, options);

            var services = new VoltPlatformServices
            {
                Players = _player,
                Info = _player,
                Input = _player,
                Interaction = _player,
                Dialogs = new SimulatedDialogs(),
                Transport = new VoltOfflineTransport(),
                Storage = new SimulatedStorage(options.StorageFolder ?? Path.Combine(Application.dataPath, "..", "Library", "ParelVR", "VoltStorage"))
            };
            Options = options;
            _services = services;
            try
            {
                World = VoltWorld.Load(new VoltWorldOptions
                {
                    Module = options.Module,
                    Scene = scene,
                    Services = services,
                    Granted = (ParelVR.Volt.Core.VoltCapability)options.Capabilities,
                    WebDomains = options.WebDomains ?? Array.Empty<string>(),
                    IsEditor = true,
                    PlatformName = "editor",
                    Debugger = options.Debugger
                });
            }
            catch
            {
                UnityEngine.Object.Destroy(go);
                _player = null;
                throw;
            }
            if (options.Error != null) World.ErrorReported += options.Error;
            if (options.Log != null) World.Logged += (level, text, context) => options.Log((int)level, text, context);
            _player.World = World;
            World.PlayerJoined(_player.LocalPlayer);
            Started?.Invoke();
        }

        /// <summary>
        /// Swaps in a new build of the scripts while play mode goes on (hot reload): the world loads again on the same
        /// scene and player, and fields that still exist get their values back. Returns how many were kept, -1 when
        /// the simulator is not running.
        /// </summary>
        public static int Reload(byte[] module)
        {
            if (World == null || Options == null || _player == null || _services == null || module == null) return -1;
            var saved = World.CaptureFields();
            World.Unload();
            World = null;
            Options.Module = module;
            World = VoltWorld.Load(new VoltWorldOptions
            {
                Module = module,
                Scene = SceneManager.GetActiveScene(),
                Services = _services,
                Granted = (ParelVR.Volt.Core.VoltCapability)Options.Capabilities,
                WebDomains = Options.WebDomains ?? Array.Empty<string>(),
                IsEditor = true,
                PlatformName = "editor",
                Debugger = Options.Debugger
            });
            VoltSimulatorOptions options = Options;
            if (options.Error != null) World.ErrorReported += options.Error;
            if (options.Log != null) World.Logged += (level, text, context) => options.Log((int)level, text, context);
            int kept = World.RestoreFields(saved);
            _player.World = World;
            World.PlayerJoined(_player.LocalPlayer);
            return kept;
        }

        public static void Stop()
        {
            VoltWorld world = World;
            World = null;
            if (world != null)
            {
                world.Unload();
                Stopped?.Invoke();
            }
            if (_player != null) UnityEngine.Object.Destroy(_player.gameObject);
            _player = null;
        }

        /// <summary>
        /// Play mode starts the simulator by itself when the scene carries a module, which is what the SDK's
        /// scene processing leaves behind when the scripts compiled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!Application.isEditor) return;
            // With domain reload off in the Enter Play Mode settings, the last session's world is still here.
            try { Stop(); } catch (Exception) { }
            try { VoltWorld.Current?.Unload(); } catch (Exception) { }
            World = null;
            _player = null;
            VoltWorldScene data = null;
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                data = root.GetComponentInChildren<VoltWorldScene>(true);
                if (data != null) break;
            }
            if (data == null || data.editorModule == null || data.editorModule.Length == 0) return;
            VoltDebugger debugger = NextDebugger;
            try
            {
                Start(new VoltSimulatorOptions { Module = data.editorModule, Capabilities = data.editorCapabilities, WebDomains = data.editorWebDomains, Debugger = debugger, VR = data.editorVR });
            }
            catch (VoltFormatException e)
            {
                Debug.LogError("[Volt] The compiled scripts were not accepted by the VM: " + e.Message);
                Stop();
            }
            catch (Exception e)
            {
                // Whatever went wrong, play mode goes on without scripts instead of taking the editor down with it.
                Debug.LogError("[Volt] The simulator could not start, so the world runs without scripts: " + e);
                try { Stop(); } catch (Exception) { }
            }
            Application.quitting -= Stop;
            Application.quitting += Stop;
        }
    }

    /// <summary>
    /// The stand-in player: a capsule you walk around with on desktop, or a headset and two controllers when the world
    /// is tested in VR (Volt Settings), and every service that concerns the player.
    /// </summary>
    internal sealed class SimulatedPlayer : MonoBehaviour, IVoltPlayerService, IVoltWorldInfo, IVoltInputService, IVoltInteractionService
    {
        private const float EyeHeight = 1.6f;
        // As in the client: from the head, a desktop player reaches about an arm's length past what a hand could touch.
        private const float DesktopReach = 0.9f, RayLength = 15f, HoldNearest = 0.35f, HoldFurthest = 3f, HoldStep = 0.15f;

        internal VoltWorld World;
        private readonly List<VoltPlayerHandle> _players = new List<VoltPlayerHandle>();
        private readonly List<VoltInteractionTarget> _targets = new List<VoltInteractionTarget>();
        private readonly HashSet<KeyCode> _keys = new HashSet<KeyCode>();
        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private static readonly IComparer<RaycastHit> ByDistance = Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));
        private VoltPlayerHandle _local;
        private VoltSimulatorOptions _options;
        private CharacterController _body;
        private Camera _camera;
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation = Quaternion.identity;
        private Vector3 _velocity;
        private float _yaw, _pitch, _vertical;
        private bool _immobile, _mouseLeft, _mouseRight, _jumpQueued;
        private Transform _seat, _exit;
        private VoltInteractionTarget _aim;
        private VoltPickup _held;

        public void Setup(Scene scene, VoltSimulatorOptions options)
        {
            _options = options;
            _local = new VoltPlayerHandle(1, "editor", "Editor Player", true) { PlatformObject = this };
            _players.Add(_local);

            GameObject spawn = GameObject.Find("Spawn") ?? GameObject.Find("SpawnPoint");
            _spawnPosition = spawn != null ? spawn.transform.position : Vector3.zero;
            _spawnRotation = spawn != null ? Quaternion.Euler(0f, spawn.transform.eulerAngles.y, 0f) : Quaternion.identity;

            _body = gameObject.AddComponent<CharacterController>();
            _body.height = 1.8f;
            _body.radius = 0.3f;
            _body.center = new Vector3(0f, 0.9f, 0f);
            var cameraObject = new GameObject("Camera");
            cameraObject.transform.SetParent(transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.nearClipPlane = 0.05f;
            _camera.depth = 100f;
            _camera.tag = "MainCamera";
            // As in the client (CameraOcclusionCullingDisabler): worlds carry whatever occlusion bake was last made, and a
            // stale one hides geometry as the camera moves, so objects vanish and come back depending on where you look.
            _camera.useOcclusionCulling = false;
            if (FindAnyObjectByType<AudioListener>() == null) cameraObject.AddComponent<AudioListener>();
#if VOLT_SIM_XR
            if (options.VR) StartVR();
#endif
            Teleport(_spawnPosition, _spawnRotation);
        }

        // ── players ──────────────────────────────────────────────────────────

        public VoltPlayerHandle LocalPlayer => _local;
        public VoltPlayerHandle Master => _local;
        public IReadOnlyList<VoltPlayerHandle> Players => _players;
        public VoltPlayerHandle Find(int playerId) => playerId == _local.Id ? _local : null;
        public VoltPlayerHandle PlayerOf(Collider collider) => collider != null && collider == _body ? _local : null;

        public Vector3 GetPosition(VoltPlayerHandle player) => transform.position;
        public Quaternion GetRotation(VoltPlayerHandle player) => Quaternion.Euler(0f, InVR ? _camera.transform.eulerAngles.y : _yaw, 0f);
        public Vector3 GetVelocity(VoltPlayerHandle player) => _body != null ? _body.velocity : Vector3.zero;
        public bool IsGrounded(VoltPlayerHandle player) => _body != null && _body.isGrounded;
        public bool IsInVR(VoltPlayerHandle player) => InVR;
        public float GetAvatarEyeHeight(VoltPlayerHandle player) => EyeHeight;
        public string GetAvatarId(VoltPlayerHandle player) => "simulated";
        public int GetFps(VoltPlayerHandle player) => Time.smoothDeltaTime > 0f ? Mathf.RoundToInt(1f / Time.smoothDeltaTime) : 0;
        public int GetPingMs(VoltPlayerHandle player) => 0;

        public bool TryGetTracking(VoltPlayerHandle player, VoltTrackingPoint point, out Vector3 position, out Quaternion rotation)
        {
            Transform eye = _camera.transform;
            rotation = eye.rotation;
#if VOLT_SIM_XR
            if (InVR && (point == VoltTrackingPoint.LeftHand || point == VoltTrackingPoint.RightHand))
            {
                Hand hand = point == VoltTrackingPoint.LeftHand ? _leftHand : _rightHand;
                if (hand.Tracked)
                {
                    position = hand.Position;
                    rotation = hand.Rotation;
                    return true;
                }
            }
#endif
            switch (point)
            {
                case VoltTrackingPoint.Head: position = eye.position; return true;
                case VoltTrackingPoint.RightHand: position = eye.position + eye.forward * 0.6f + eye.right * 0.25f - eye.up * 0.25f; return true;
                case VoltTrackingPoint.LeftHand: position = eye.position + eye.forward * 0.6f - eye.right * 0.25f - eye.up * 0.25f; return true;
                case VoltTrackingPoint.Origin: position = transform.position; rotation = transform.rotation; return true;
                default: position = transform.position; return false;
            }
        }

        public bool TryGetBone(VoltPlayerHandle player, VoltBone bone, out Vector3 position, out Quaternion rotation)
        {
            if (bone == VoltBone.Head) return TryGetTracking(player, VoltTrackingPoint.Head, out position, out rotation);
            if (bone == VoltBone.RightHand) return TryGetTracking(player, VoltTrackingPoint.RightHand, out position, out rotation);
            if (bone == VoltBone.LeftHand) return TryGetTracking(player, VoltTrackingPoint.LeftHand, out position, out rotation);
            position = transform.position;
            rotation = Quaternion.identity;
            return false;
        }

        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            // A NaN reaching the character controller breaks physics for the rest of the session.
            if (!Finite(position)) return;
            _body.enabled = false;
            transform.position = position;
            float yaw = rotation.eulerAngles.y;
            _yaw = float.IsNaN(yaw) ? _yaw : yaw;
            _vertical = 0f;
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _body.enabled = true;
        }

        public void SetVelocity(Vector3 velocity)
        {
            if (!Finite(velocity)) return;
            _velocity = new Vector3(velocity.x, 0f, velocity.z);
            _vertical = velocity.y;
        }

        public void SetImmobile(bool immobile) => _immobile = immobile;
        public void Respawn()
        {
            Teleport(_spawnPosition, _spawnRotation);
            World?.PlayerRespawned(_local);
        }
        public float WalkSpeed { get; set; } = 2f;
        public float RunSpeed { get; set; } = 4f;
        public float JumpImpulse { get; set; } = 3f;
        public float GravityStrength { get; set; } = 1f;

        public void PlayHaptics(VoltHand hand, float strength, float seconds)
        {
#if VOLT_SIM_XR
            if (!InVR) return;
            InputDevice device = InputDevices.GetDeviceAtXRNode(hand == VoltHand.Left ? XRNode.LeftHand : XRNode.RightHand);
            if (device.isValid) device.SendHapticImpulse(0, Mathf.Clamp01(strength), Mathf.Clamp(seconds, 0f, 2f));
#endif
        }

        public void SetSeat(Transform seat, Transform exit, bool immobilize)
        {
            if (seat != null)
            {
                _seat = seat;
                _exit = exit;
                _immobile = immobilize;
                return;
            }
            _seat = null;
            _immobile = false;
            if (exit != null) Teleport(exit.position, Quaternion.Euler(0f, exit.eulerAngles.y, 0f));
        }

        // ── world info ───────────────────────────────────────────────────────

        public string WorldId => "editor";
        public string WorldName => _options.WorldName ?? "Editor World";
        public string WorldVersion => "editor";
        public string InstanceId => "editor";
        public int Capacity => 1;
        public VoltInstancePrivacy Privacy => VoltInstancePrivacy.Unknown;
        public string GetInstanceProperty(string key) => null;
        public void ReturnToHub() => Debug.Log("[Volt] The world asked to return to the hub. In the editor nothing happens.");

        // ── input ────────────────────────────────────────────────────────────

        public bool IsVR => InVR;
        private bool Key(KeyCode key) => _keys.Contains(key);

        public bool Get(VoltInputAction action)
        {
#if VOLT_SIM_XR
            if (InVR)
            {
                switch (action)
                {
                    case VoltInputAction.Movement: return _leftHand.Stick.sqrMagnitude > 0.04f;
                    case VoltInputAction.Jump: return _rightHand.Primary;
                    case VoltInputAction.Interact:
                    case VoltInputAction.Primary:
                    case VoltInputAction.Use:
                    case VoltInputAction.Trigger: return _rightHand.Trigger;
                    case VoltInputAction.Grab:
                    case VoltInputAction.Secondary:
                    case VoltInputAction.Grip: return _rightHand.Grip;
                    case VoltInputAction.Menu: return _leftHand.Menu;
                    case VoltInputAction.Run: return _leftHand.StickClick;
                    default: return false;
                }
            }
#endif
            switch (action)
            {
                case VoltInputAction.Movement: return Key(KeyCode.W) || Key(KeyCode.A) || Key(KeyCode.S) || Key(KeyCode.D);
                case VoltInputAction.Jump: return Key(KeyCode.Space);
                case VoltInputAction.Interact: return Key(KeyCode.E);
                case VoltInputAction.Grab:
                case VoltInputAction.Primary:
                case VoltInputAction.Trigger: return _mouseLeft;
                case VoltInputAction.Use: return Key(KeyCode.F);
                case VoltInputAction.Secondary:
                case VoltInputAction.Grip: return _mouseRight;
                case VoltInputAction.Menu: return Key(KeyCode.Tab);
                case VoltInputAction.Run: return Key(KeyCode.LeftShift);
                case VoltInputAction.Crouch: return Key(KeyCode.C);
                default: return false;
            }
        }

        public float GetValue(VoltInputAction action)
        {
#if VOLT_SIM_XR
            if (InVR && (action == VoltInputAction.Trigger || action == VoltInputAction.Use || action == VoltInputAction.Primary)) return _rightHand.TriggerValue;
            if (InVR && (action == VoltInputAction.Grip || action == VoltInputAction.Grab)) return _rightHand.GripValue;
#endif
            return Get(action) ? 1f : 0f;
        }

        public Vector2 GetAxis(VoltInputAction action)
        {
            if (action != VoltInputAction.Movement) return Vector2.zero;
#if VOLT_SIM_XR
            if (InVR) return _leftHand.Stick;
#endif
            return new Vector2((Key(KeyCode.D) ? 1f : 0f) - (Key(KeyCode.A) ? 1f : 0f), (Key(KeyCode.W) ? 1f : 0f) - (Key(KeyCode.S) ? 1f : 0f));
        }

        public float GetTrigger(VoltHand hand)
        {
#if VOLT_SIM_XR
            if (InVR) return (hand == VoltHand.Left ? _leftHand : _rightHand).TriggerValue;
#endif
            return hand == VoltHand.Right && _mouseLeft ? 1f : 0f;
        }

        public float GetGrip(VoltHand hand)
        {
#if VOLT_SIM_XR
            if (InVR) return (hand == VoltHand.Left ? _leftHand : _rightHand).GripValue;
#endif
            return hand == VoltHand.Right && _mouseRight ? 1f : 0f;
        }

        public Vector2 GetStick(VoltHand hand)
        {
#if VOLT_SIM_XR
            if (InVR) return (hand == VoltHand.Left ? _leftHand : _rightHand).Stick;
#endif
            return hand == VoltHand.Left ? GetAxis(VoltInputAction.Movement) : Vector2.zero;
        }

        // ── interaction ──────────────────────────────────────────────────────

        public void Register(VoltInteractionTarget target) { if (target != null) _targets.Add(target); }
        public void Unregister(VoltInteractionTarget target) => _targets.Remove(target);

        private VoltInteractionTarget TargetOf(Transform hit)
        {
            for (Transform t = hit; t != null; t = t.parent)
                foreach (VoltInteractionTarget target in _targets)
                    if (target.Object == t.gameObject) return target;
            return null;
        }

        /// <summary>
        /// What a ray points at: the nearest hit that belongs to an interactable, pickup or seat in reach. Trigger
        /// volumes that are not targets are looked through (a player trigger zone must not swallow every click); the
        /// first solid collider ends the search.
        /// </summary>
        private VoltInteractionTarget Aim(Vector3 origin, Vector3 direction, float extraReach, out Vector3 point)
        {
            point = origin + direction * 2f;
            int count = Physics.RaycastNonAlloc(origin, direction, _hits, RayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            Array.Sort(_hits, 0, count, ByDistance);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.collider == null || hit.collider == _body) continue;
                VoltInteractionTarget target = TargetOf(hit.collider.transform);
                if (target != null && SafeEnabled(target) && hit.distance <= Mathf.Max(0.1f, SafeProximity(target)) + extraReach)
                {
                    point = hit.point;
                    return target;
                }
                if (!hit.collider.isTrigger)
                {
                    point = hit.point;
                    return null;
                }
            }
            return null;
        }

        private static bool SafeEnabled(VoltInteractionTarget target)
        {
            try { return target.Enabled == null || target.Enabled(); }
            catch (Exception) { return false; }
        }

        private static float SafeProximity(VoltInteractionTarget target)
        {
            try { return target.Proximity != null ? target.Proximity() : 2f; }
            catch (Exception) { return 2f; }
        }

        private static string SafeText(VoltInteractionTarget target)
        {
            try { return target.Text != null ? target.Text() ?? string.Empty : string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>Uses a target with a hand: a pickup is taken into it, anything else is used. The pickup taken, if any.</summary>
        private VoltPickup Use(VoltInteractionTarget target, VoltHand hand)
        {
            if (target == null || World == null) return null;
            switch (target.Kind)
            {
                case VoltInteractionKind.Interact: World.Interact(target.Object); break;
                case VoltInteractionKind.Pickup:
                    var pickup = (VoltPickup)target.Component;
                    if (World.Grab(pickup, hand)) return pickup;
                    break;
                case VoltInteractionKind.Station: World.UseStation((VoltStation)target.Component); break;
            }
            return null;
        }

        // ── the player itself ────────────────────────────────────────────────

        private void Update()
        {
#if VOLT_SIM_XR
            if (InVR) ReadVR();
#endif
            if (_seat != null)
            {
                _body.enabled = false;
                transform.position = _seat.position;
                _body.enabled = true;
                if (_jumpQueued) World?.ExitStation();
                _jumpQueued = false;
            }
            else
            {
                float speed = Get(VoltInputAction.Run) ? RunSpeed : WalkSpeed;
                Vector2 axis = _immobile ? Vector2.zero : GetAxis(VoltInputAction.Movement);
                float heading = InVR ? _camera.transform.eulerAngles.y : _yaw;
                Vector3 move = Quaternion.Euler(0f, heading, 0f) * Vector3.ClampMagnitude(new Vector3(axis.x, 0f, axis.y), 1f) * speed + _velocity;
                _velocity = Vector3.Lerp(_velocity, Vector3.zero, 5f * Time.deltaTime);
                if (_body.isGrounded)
                {
                    if (_vertical < 0f) _vertical = -1f;
                    if (_jumpQueued && !_immobile) _vertical = JumpImpulse;
                }
                _jumpQueued = false;
                _vertical += Physics.gravity.y * GravityStrength * Time.deltaTime;
                Vector3 step = (move + Vector3.up * _vertical) * Time.deltaTime;
                if (Finite(step)) _body.Move(step);
                if (transform.position.y < -100f) Respawn();
            }
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

#if VOLT_SIM_XR
            if (InVR)
            {
                PoseHead();
                UpdateHand(_leftHand, 1);
                UpdateHand(_rightHand, 2);
                _aim = null;
                World?.SetHover(0, null);
                return;
            }
            HideRays();
#endif
            _camera.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
            _camera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            // The pickup may have been taken away or dropped by a script.
            if (_held != null && (World == null || !World.IsLoaded || !World.IsHeldLocally(_held))) _held = null;
            Transform eye = _camera.transform;
            _aim = _held == null ? Aim(eye.position, eye.forward, DesktopReach, out _) : null;
            World?.SetHover(0, _aim != null ? _aim.Object : null);
        }

        // IMGUI events work with either input backend, so the simulator needs nothing from the project's input settings.
        private void OnGUI()
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.None) break;
                    if (_keys.Add(e.keyCode)) Pressed(e.keyCode);
                    break;
                case EventType.KeyUp:
                    if (_keys.Remove(e.keyCode) && e.keyCode == KeyCode.F && _held != null) World?.PickupUse(_held, false);
                    break;
                case EventType.MouseDown:
                    if (e.button == 0)
                    {
                        _mouseLeft = true;
                        if (_held == null) _held = Use(_aim, VoltHand.Right);
                        else World?.PickupUse(_held, true);
                    }
                    if (e.button == 1) _mouseRight = true;
                    break;
                case EventType.MouseUp:
                    if (e.button == 0)
                    {
                        _mouseLeft = false;
                        if (_held != null) World?.PickupUse(_held, false);
                    }
                    if (e.button == 1) _mouseRight = false;
                    break;
                case EventType.MouseDrag:
                    if (e.button != 1) break;
                    _yaw += e.delta.x * 0.2f;
                    _pitch = Mathf.Clamp(_pitch + e.delta.y * 0.2f, -89f, 89f);
                    break;
                case EventType.ScrollWheel:
                    // The wheel pushes a held object away or pulls it in, as in the client.
                    if (_held == null || World == null || Mathf.Abs(e.delta.y) < 0.01f) break;
                    Transform eye = _camera.transform;
                    World.PushHeld(_held, eye.position, eye.forward, -Mathf.Sign(e.delta.y) * HoldStep, HoldNearest, HoldFurthest);
                    break;
            }
            if (e.type != EventType.Repaint) return;

            var hint = new Rect(10f, Screen.height - 26f, Screen.width - 20f, 20f);
            GUI.Label(hint, InVR ? "VR: grip grab    trigger use    left stick move    right stick turn    A jump"
                : _held != null ? "Click / F use    G drop    wheel push / pull    WASD move    right mouse look"
                : _seat != null ? "Space stand up    right mouse look"
                : "WASD move    Space jump    Shift run    right mouse look    click / E use and pick up");
            if (InVR) return;
            GUI.Label(new Rect(Screen.width / 2f - 4f, Screen.height / 2f - 10f, 20f, 20f), "+");
            if (_aim != null)
            {
                string text = SafeText(_aim);
                GUI.Label(new Rect(Screen.width / 2f - 150f, Screen.height / 2f + 16f, 300f, 22f), text, new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private void Pressed(KeyCode key)
        {
            if (key == KeyCode.Space) _jumpQueued = true;
            else if (key == KeyCode.E && _held == null) _held = Use(_aim, VoltHand.Right);
            else if (key == KeyCode.F && _held != null) World?.PickupUse(_held, true);
            else if (key == KeyCode.G && _held != null)
            {
                World?.Release(_held);
                _held = null;
            }
        }

        private void OnApplicationFocus(bool focus)
        {
            if (focus) return;
            // Key releases are lost while the window is not focused.
            _keys.Clear();
            _mouseLeft = _mouseRight = false;
        }

        private void OnDestroy()
        {
#if VOLT_SIM_XR
            StopVR();
#endif
        }

        // ── VR ───────────────────────────────────────────────────────────────

#if !VOLT_SIM_XR
        private bool InVR => false;
#else
        private sealed class Hand
        {
            public VoltHand Side;
            public XRNode Node;
            public bool Tracked, Grip, Trigger, GripWas, TriggerWas, Primary, PrimaryWas, Menu, StickClick;
            public float GripValue, TriggerValue;
            public Vector2 Stick;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;
            public VoltInteractionTarget Aim;
            public VoltPickup Held;
            public LineRenderer Ray;
        }

        private readonly Hand _leftHand = new Hand { Side = VoltHand.Left, Node = XRNode.LeftHand };
        private readonly Hand _rightHand = new Hand { Side = VoltHand.Right, Node = XRNode.RightHand };
        private bool _vrWanted, _xrStartedHere, _turned;
        private Material _rayMaterial;

        private bool InVR => _vrWanted && XRSettings.isDeviceActive;

        private static object XRManager()
        {
            Type type = Type.GetType("UnityEngine.XR.Management.XRGeneralSettings, Unity.XR.Management");
            object settings = type?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            return settings?.GetType().GetProperty("Manager")?.GetValue(settings);
        }

        /// <summary>
        /// Starts the headset through XR Plug-in Management, unless "Initialize XR on Startup" already did. Without a
        /// headset the test goes on on desktop.
        /// </summary>
        private void StartVR()
        {
            _vrWanted = true;
            try
            {
                if (!XRSettings.isDeviceActive)
                {
                    object manager = XRManager();
                    if (manager == null)
                    {
                        Debug.LogWarning("[Volt] Testing in VR needs the XR Plug-in Management package with a plug-in such as OpenXR enabled for this platform. Running on desktop.");
                        _vrWanted = false;
                        return;
                    }
                    Type type = manager.GetType();
                    if (type.GetProperty("activeLoader")?.GetValue(manager) == null)
                    {
                        type.GetMethod("InitializeLoaderSync")?.Invoke(manager, null);
                        if (type.GetProperty("activeLoader")?.GetValue(manager) == null)
                        {
                            Debug.LogWarning("[Volt] No headset answered (is it connected, and is the XR runtime such as SteamVR or Quest Link running?). Running on desktop.");
                            _vrWanted = false;
                            return;
                        }
                        _xrStartedHere = true;
                    }
                    type.GetMethod("StartSubsystems")?.Invoke(manager, null);
                }
                var inputs = new List<XRInputSubsystem>();
                SubsystemManager.GetSubsystems(inputs);
                foreach (XRInputSubsystem input in inputs) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                _camera.stereoTargetEye = StereoTargetEyeMask.Both;
                Application.onBeforeRender += PoseHead;
                Debug.Log("[Volt] Testing in VR. Grip grabs, trigger uses, left stick moves, right stick turns.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Volt] VR could not start, running on desktop: " + (e.InnerException ?? e).Message);
                _vrWanted = false;
            }
        }

        private void StopVR()
        {
            Application.onBeforeRender -= PoseHead;
            if (_rayMaterial != null) Destroy(_rayMaterial);
            if (!_xrStartedHere) return;
            _xrStartedHere = false;
            try
            {
                object manager = XRManager();
                if (manager == null) return;
                manager.GetType().GetMethod("StopSubsystems")?.Invoke(manager, null);
                manager.GetType().GetMethod("DeinitializeLoader")?.Invoke(manager, null);
            }
            catch (Exception e) { Debug.LogWarning("[Volt] VR did not stop cleanly: " + (e.InnerException ?? e).Message); }
        }

        // The rig's origin is the player's feet; the headset and controllers are placed relative to it.
        private void PoseHead()
        {
            if (!InVR || _camera == null) return;
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) && Finite(position)) _camera.transform.localPosition = position;
            if (head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation)) _camera.transform.localRotation = rotation;
        }

        private void ReadVR()
        {
            ReadHand(_leftHand);
            ReadHand(_rightHand);
            if (_rightHand.Primary && !_rightHand.PrimaryWas) _jumpQueued = true;
            // Snap turn around the head, so the view does not swing sideways.
            float x = _rightHand.Stick.x;
            if (Mathf.Abs(x) < 0.3f) _turned = false;
            else if (!_turned && Mathf.Abs(x) > 0.7f && _seat == null)
            {
                _turned = true;
                Vector3 head = _camera.transform.position;
                _yaw += Mathf.Sign(x) * 30f;
                _body.enabled = false;
                transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
                transform.position += head - _camera.transform.position;
                _body.enabled = true;
            }
        }

        private void ReadHand(Hand hand)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(hand.Node);
            hand.GripWas = hand.Grip;
            hand.TriggerWas = hand.Trigger;
            hand.PrimaryWas = hand.Primary;
            bool hasPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) && Finite(position);
            bool hasRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation);
            hand.Tracked = device.isValid && hasPosition && hasRotation;
            if (hand.Tracked)
            {
                hand.Position = transform.TransformPoint(position);
                hand.Rotation = transform.rotation * rotation;
            }
            hand.Grip = device.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip;
            hand.Trigger = device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger) && trigger;
            hand.Primary = device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary) && primary;
            hand.Menu = device.TryGetFeatureValue(CommonUsages.menuButton, out bool menu) && menu;
            hand.StickClick = device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click) && click;
            hand.GripValue = device.TryGetFeatureValue(CommonUsages.grip, out float gripValue) ? gripValue : hand.Grip ? 1f : 0f;
            hand.TriggerValue = device.TryGetFeatureValue(CommonUsages.trigger, out float triggerValue) ? triggerValue : hand.Trigger ? 1f : 0f;
            hand.Stick = device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick) ? stick : Vector2.zero;
        }

        /// <summary>A controller points with a ray: grip takes a pickup, trigger uses what it points at or what it holds.</summary>
        private void UpdateHand(Hand hand, int hoverSource)
        {
            Vector3 end = hand.Position;
            if (hand.Held != null)
            {
                if (World == null || !World.IsHeldLocally(hand.Held)) hand.Held = null;
                else
                {
                    if (hand.Trigger && !hand.TriggerWas) World.PickupUse(hand.Held, true);
                    if (!hand.Trigger && hand.TriggerWas) World.PickupUse(hand.Held, false);
                    if (!hand.Grip && hand.GripWas)
                    {
                        World.Release(hand.Held);
                        hand.Held = null;
                    }
                }
                hand.Aim = null;
            }
            else if (hand.Tracked)
            {
                hand.Aim = Aim(hand.Position, hand.Rotation * Vector3.forward, 0f, out end);
                bool grabbed = hand.Grip && !hand.GripWas, pressed = hand.Trigger && !hand.TriggerWas;
                if (hand.Aim != null && (grabbed || pressed))
                {
                    if (hand.Aim.Kind == VoltInteractionKind.Pickup) hand.Held = Use(hand.Aim, hand.Side);
                    else if (pressed) Use(hand.Aim, hand.Side);
                }
            }
            else hand.Aim = null;
            World?.SetHover(hoverSource, hand.Aim != null ? hand.Aim.Object : null);
            DrawRay(hand, end);
        }

        private void DrawRay(Hand hand, Vector3 end)
        {
            if (hand.Ray == null)
            {
                if (_rayMaterial == null) _rayMaterial = new Material(Shader.Find("Sprites/Default"));
                var go = new GameObject(hand.Side + " Ray");
                go.transform.SetParent(transform, false);
                hand.Ray = go.AddComponent<LineRenderer>();
                hand.Ray.sharedMaterial = _rayMaterial;
                hand.Ray.positionCount = 2;
                hand.Ray.widthMultiplier = 0.004f;
                hand.Ray.useWorldSpace = true;
                hand.Ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                hand.Ray.receiveShadows = false;
            }
            bool show = hand.Tracked && hand.Held == null;
            hand.Ray.enabled = show;
            if (!show) return;
            Color colour = hand.Aim != null ? new Color(0.45f, 0.75f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.25f);
            hand.Ray.startColor = hand.Ray.endColor = colour;
            hand.Ray.SetPosition(0, hand.Position);
            hand.Ray.SetPosition(1, hand.Aim != null ? end : hand.Position + hand.Rotation * Vector3.forward * 2f);
        }

        private void HideRays()
        {
            if (_leftHand.Ray != null) _leftHand.Ray.enabled = false;
            if (_rightHand.Ray != null) _rightHand.Ray.enabled = false;
        }
#endif
    }

    /// <summary>The editor has no HUD: notifications go to the console and a question is answered with its confirm button.</summary>
    internal sealed class SimulatedDialogs : IVoltDialogService
    {
        public void Notify(string title, string text) => Debug.Log("[Volt] Notification: " + title + " - " + text);

        public void Ask(string title, string message, string confirm, string cancel, Action<bool> answered)
        {
            Debug.Log("[Volt] Dialog, answered \"" + confirm + "\" by the simulator: " + title + " - " + message);
            answered?.Invoke(true);
        }
    }

    /// <summary>Storage in a file per scope, so that what a script saved is still there the next time you press Play.</summary>
    internal sealed class SimulatedStorage : IVoltStorageService
    {
        [Serializable]
        private sealed class Table
        {
            public List<string> keys = new List<string>();
            public List<string> values = new List<string>();
        }

        private readonly string _folder;

        public SimulatedStorage(string folder) { _folder = folder; }

        private string PathOf(VoltStorageScope scope) => Path.Combine(_folder, scope.ToString().ToLowerInvariant() + ".json");

        private Table Read(VoltStorageScope scope)
        {
            try
            {
                string path = PathOf(scope);
                if (File.Exists(path)) return JsonUtility.FromJson<Table>(File.ReadAllText(path)) ?? new Table();
            }
            catch (Exception e) { Debug.LogWarning("[Volt] Simulated storage could not be read: " + e.Message); }
            return new Table();
        }

        private void Write(VoltStorageScope scope, Table table)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(PathOf(scope), JsonUtility.ToJson(table));
        }

        public void Get(VoltStorageScope scope, string key, Action<VoltStorageResult> done)
        {
            Table table = Read(scope);
            int at = table.keys.IndexOf(key);
            done(new VoltStorageResult { Exists = at >= 0, Value = at >= 0 ? table.values[at] : null });
        }

        public void Set(VoltStorageScope scope, string key, string value, Action<VoltStorageResult> done)
        {
            try
            {
                Table table = Read(scope);
                int at = table.keys.IndexOf(key);
                if (at >= 0) table.values[at] = value;
                else if (table.keys.Count >= 256) { done(new VoltStorageResult { Error = VoltError.TooLarge }); return; }
                else { table.keys.Add(key); table.values.Add(value); }
                Write(scope, table);
                done(new VoltStorageResult { Exists = true, Value = value });
            }
            catch (Exception) { done(new VoltStorageResult { Error = VoltError.Unavailable }); }
        }

        public void Delete(VoltStorageScope scope, string key, Action<VoltStorageResult> done)
        {
            try
            {
                Table table = Read(scope);
                int at = table.keys.IndexOf(key);
                if (at >= 0)
                {
                    table.keys.RemoveAt(at);
                    table.values.RemoveAt(at);
                    Write(scope, table);
                }
                done(new VoltStorageResult { Exists = false });
            }
            catch (Exception) { done(new VoltStorageResult { Error = VoltError.Unavailable }); }
        }
    }
}
