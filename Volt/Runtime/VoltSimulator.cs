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
                Transport = new VoltOfflineTransport(),
                Storage = new SimulatedStorage(options.StorageFolder ?? Path.Combine(Application.dataPath, "..", "Library", "ParelVR", "VoltStorage"))
            };
            Options = options;
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
                Start(new VoltSimulatorOptions { Module = data.editorModule, Capabilities = data.editorCapabilities, WebDomains = data.editorWebDomains, Debugger = debugger });
            }
            catch (VoltFormatException e)
            {
                Debug.LogError("[Volt] The compiled scripts were not accepted by the VM: " + e.Message);
            }
            Application.quitting -= Stop;
            Application.quitting += Stop;
        }
    }

    /// <summary>The stand-in player: a capsule you walk around with, and every service that concerns the player.</summary>
    internal sealed class SimulatedPlayer : MonoBehaviour, IVoltPlayerService, IVoltWorldInfo, IVoltInputService, IVoltInteractionService
    {
        private const float EyeHeight = 1.6f;

        internal VoltWorld World;
        private readonly List<VoltPlayerHandle> _players = new List<VoltPlayerHandle>();
        private readonly List<VoltInteractionTarget> _targets = new List<VoltInteractionTarget>();
        private readonly HashSet<KeyCode> _keys = new HashSet<KeyCode>();
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
            if (FindAnyObjectByType<AudioListener>() == null) cameraObject.AddComponent<AudioListener>();
            Teleport(_spawnPosition, _spawnRotation);
        }

        // ── players ──────────────────────────────────────────────────────────

        public VoltPlayerHandle LocalPlayer => _local;
        public VoltPlayerHandle Master => _local;
        public IReadOnlyList<VoltPlayerHandle> Players => _players;
        public VoltPlayerHandle Find(int playerId) => playerId == _local.Id ? _local : null;
        public VoltPlayerHandle PlayerOf(Collider collider) => collider != null && collider == _body ? _local : null;

        public Vector3 GetPosition(VoltPlayerHandle player) => transform.position;
        public Quaternion GetRotation(VoltPlayerHandle player) => Quaternion.Euler(0f, _yaw, 0f);
        public Vector3 GetVelocity(VoltPlayerHandle player) => _body != null ? _body.velocity : Vector3.zero;
        public bool IsGrounded(VoltPlayerHandle player) => _body != null && _body.isGrounded;
        public bool IsInVR(VoltPlayerHandle player) => false;
        public float GetAvatarEyeHeight(VoltPlayerHandle player) => EyeHeight;
        public string GetAvatarId(VoltPlayerHandle player) => "simulated";

        public bool TryGetTracking(VoltPlayerHandle player, VoltTrackingPoint point, out Vector3 position, out Quaternion rotation)
        {
            Transform eye = _camera.transform;
            rotation = eye.rotation;
            switch (point)
            {
                case VoltTrackingPoint.Head: position = eye.position; return true;
                case VoltTrackingPoint.RightHand: position = eye.position + eye.forward * 0.6f + eye.right * 0.25f - eye.up * 0.25f; return true;
                case VoltTrackingPoint.LeftHand: position = eye.position + eye.forward * 0.6f - eye.right * 0.25f - eye.up * 0.25f; return true;
                case VoltTrackingPoint.Origin: position = transform.position; rotation = Quaternion.Euler(0f, _yaw, 0f); return true;
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

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            _body.enabled = false;
            transform.position = position;
            _yaw = rotation.eulerAngles.y;
            _vertical = 0f;
            _body.enabled = true;
        }

        public void SetVelocity(Vector3 velocity) { _velocity = new Vector3(velocity.x, 0f, velocity.z); _vertical = velocity.y; }
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
        public void PlayHaptics(VoltHand hand, float strength, float seconds) { }

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

        public bool IsVR => false;
        private bool Key(KeyCode key) => _keys.Contains(key);

        public bool Get(VoltInputAction action)
        {
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

        public float GetValue(VoltInputAction action) => Get(action) ? 1f : 0f;
        public Vector2 GetAxis(VoltInputAction action) =>
            action == VoltInputAction.Movement ? new Vector2((Key(KeyCode.D) ? 1f : 0f) - (Key(KeyCode.A) ? 1f : 0f), (Key(KeyCode.W) ? 1f : 0f) - (Key(KeyCode.S) ? 1f : 0f)) : Vector2.zero;
        public float GetTrigger(VoltHand hand) => hand == VoltHand.Right && _mouseLeft ? 1f : 0f;
        public float GetGrip(VoltHand hand) => hand == VoltHand.Right && _mouseRight ? 1f : 0f;
        public Vector2 GetStick(VoltHand hand) => hand == VoltHand.Left ? GetAxis(VoltInputAction.Movement) : Vector2.zero;

        // ── interaction ──────────────────────────────────────────────────────

        public void Register(VoltInteractionTarget target) { if (target != null) _targets.Add(target); }
        public void Unregister(VoltInteractionTarget target) => _targets.Remove(target);

        private VoltInteractionTarget Aim()
        {
            Transform eye = _camera.transform;
            if (!Physics.Raycast(eye.position, eye.forward, out RaycastHit hit, 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)) return null;
            for (Transform t = hit.collider.transform; t != null; t = t.parent)
            {
                foreach (VoltInteractionTarget target in _targets)
                {
                    if (target.Object != t.gameObject) continue;
                    if (!target.Enabled() || hit.distance > Mathf.Max(0.1f, target.Proximity())) continue;
                    return target;
                }
            }
            return null;
        }

        private void Use(VoltInteractionTarget target)
        {
            if (target == null || World == null) return;
            switch (target.Kind)
            {
                case VoltInteractionKind.Interact: World.Interact(target.Object); break;
                case VoltInteractionKind.Pickup:
                    if (World.Grab((VoltPickup)target.Component, VoltHand.Right)) _held = (VoltPickup)target.Component;
                    break;
                case VoltInteractionKind.Station: World.UseStation((VoltStation)target.Component); break;
            }
        }

        // ── the player itself ────────────────────────────────────────────────

        private void Update()
        {
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
                float speed = Key(KeyCode.LeftShift) ? RunSpeed : WalkSpeed;
                Vector2 axis = _immobile ? Vector2.zero : GetAxis(VoltInputAction.Movement);
                Vector3 move = Quaternion.Euler(0f, _yaw, 0f) * new Vector3(axis.x, 0f, axis.y).normalized * speed + _velocity;
                _velocity = Vector3.Lerp(_velocity, Vector3.zero, 5f * Time.deltaTime);
                if (_body.isGrounded)
                {
                    if (_vertical < 0f) _vertical = -1f;
                    if (_jumpQueued && !_immobile) _vertical = JumpImpulse;
                }
                _jumpQueued = false;
                _vertical += Physics.gravity.y * GravityStrength * Time.deltaTime;
                _body.Move((move + Vector3.up * _vertical) * Time.deltaTime);
                if (transform.position.y < -100f) Respawn();
            }
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _camera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            _aim = _held == null ? Aim() : null;
            if (_held == null) return;
            // The pickup may have been taken away or dropped by a script.
            VoltPickup held = _held;
            if (held == null || World == null || !World.IsLoaded) _held = null;
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
                    if (e.button == 0) { _mouseLeft = true; if (_held == null) Use(_aim); }
                    if (e.button == 1) _mouseRight = true;
                    break;
                case EventType.MouseUp:
                    if (e.button == 0) _mouseLeft = false;
                    if (e.button == 1) _mouseRight = false;
                    break;
                case EventType.MouseDrag:
                    if (e.button != 1) break;
                    _yaw += e.delta.x * 0.2f;
                    _pitch = Mathf.Clamp(_pitch + e.delta.y * 0.2f, -89f, 89f);
                    break;
            }
            if (e.type != EventType.Repaint) return;

            var hint = new Rect(10f, Screen.height - 26f, Screen.width - 20f, 20f);
            GUI.Label(hint, _held != null ? "F use    G drop    WASD move    right mouse look"
                : _seat != null ? "Space stand up    right mouse look"
                : "WASD move    Space jump    Shift run    right mouse look    E / click use");
            GUI.Label(new Rect(Screen.width / 2f - 4f, Screen.height / 2f - 10f, 20f, 20f), "+");
            if (_aim != null)
            {
                string text = _aim.Text() ?? string.Empty;
                GUI.Label(new Rect(Screen.width / 2f - 150f, Screen.height / 2f + 16f, 300f, 22f), text, new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private void Pressed(KeyCode key)
        {
            if (key == KeyCode.Space) _jumpQueued = true;
            else if (key == KeyCode.E && _held == null) Use(_aim);
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
