using System;
using System.Collections.Generic;
using System.IO;
using ParelVR.Volt.Core;
using ParelVR.Volt.Core.Net;
using ParelVR.Volt.Core.Runtime;
using ParelVR.Volt.Input;
using ParelVR.Volt.Interaction;
using ParelVR.Volt.Players;
using ParelVR.Volt.Storage;
using ParelVR.VoltHost;
using ParelVR.WorldSDK;
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
        /// <summary>The compiled module. Null runs the player without scripts (a world that has none).</summary>
        public byte[] Module;
        /// <summary>The world's scene. The active scene when it is left unset.</summary>
        public Scene Scene;
        /// <summary>The local player's name; "Editor Player", or "Player n" in multi-client play mode, when unset.</summary>
        public string PlayerName;
        /// <summary>Play together with the other clients of this world on this machine. Null plays alone.</summary>
        public VoltMultiClientOptions MultiClient;
        /// <summary>Avatar parameters and fingers for scripts that read them (set by the avatar simulator).</summary>
        public IVoltAvatarService Avatar;
        /// <summary>The avatar the local player wears instead of the stand-in (the avatar simulator).</summary>
        internal SimulatedAvatar Body;
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
        /// <summary>True while the stand-in player is in play mode, with or without scripts.</summary>
        public static bool HasPlayer => _player != null;
        public static VoltWorld World { get; private set; }
        public static VoltSimulatorOptions Options { get; private set; }
        /// <summary>Set before play mode starts by tooling that wants to attach a debugger to the next run.</summary>
        public static VoltDebugger NextDebugger;
        /// <summary>Set by the editor from the Volt settings: play mode joins the other clients of the world on this machine.</summary>
        public static VoltMultiClientOptions MultiClientDefaults;
        /// <summary>Set by tooling that starts the simulator itself (the avatar simulator), so play mode does not start it for the open scene.</summary>
        public static bool SuppressAutoStart;
        public static event Action Started;
        public static event Action Stopped;
        /// <summary>Set by the editor: asset paths of the world's scenes in Build Profiles, MainScene first. VoltScene.Switch moves between them.</summary>
        public static string[] WorldScenes;
        /// <summary>Set by the editor: loads a scene by asset path in play mode, in place of the one playing.</summary>
        public static Func<string, AsyncOperation> LoadScene;
        /// <summary>Set by the editor: prepares a scene that loaded in play mode the way a built world's scene is, if it was not already.</summary>
        public static Action<Scene> PrepareLoadedScene;

        /// <summary>Starts the scripts of a scene (the active one unless the options name another). Call it in play mode.</summary>
        public static void Start(VoltSimulatorOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!Application.isPlaying) throw new InvalidOperationException("The Volt simulator runs in play mode.");
            Stop();

            Scene scene = options.Scene.IsValid() ? options.Scene : SceneManager.GetActiveScene();
            SimulatedNetwork network = null;
            if (options.MultiClient != null)
            {
                try
                {
                    network = SimulatedNetwork.Connect(options.MultiClient, options.PlayerName);
                    if (!string.IsNullOrEmpty(network.RemoteModuleHash) && !string.IsNullOrEmpty(options.MultiClient.ModuleHash) &&
                        !string.Equals(network.RemoteModuleHash, options.MultiClient.ModuleHash, StringComparison.OrdinalIgnoreCase))
                        Debug.LogWarning("[Volt] Multi-client: the other clients run a different build of the scripts. Save the scene and the scripts, then enter play mode again in every window.");
                    Debug.Log("[Volt] Multi-client: joined as player " + network.LocalId + (network.IsRelayHost ? " (this window holds the meeting point)." : "."));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Volt] Multi-client play mode could not connect on port " + options.MultiClient.Port + " (" + e.Message + "). This client plays alone.");
                    network = null;
                }
            }
            int localId = network != null ? network.LocalId : 1;
            string name = !string.IsNullOrEmpty(options.PlayerName) ? options.PlayerName : network != null ? "Player " + localId : "Editor Player";

            var go = new GameObject("[Volt Player]");
            UnityEngine.Object.DontDestroyOnLoad(go); // outside the world's scene, so scripts cannot reach it, as in the client
            _player = go.AddComponent<SimulatedPlayer>();
            _player.Setup(scene, options, localId, name, network);

            var services = new VoltPlatformServices
            {
                Players = _player,
                Info = _player,
                Input = _player,
                Interaction = _player,
                Dialogs = new SimulatedDialogs(),
                Nameplates = _player,
                Scenes = SimulatedScenes.Instance,
                Avatar = options.Avatar,
                Transport = network != null ? (IVoltNetTransport)network : new VoltOfflineTransport(),
                Storage = new SimulatedStorage(options.StorageFolder ?? Path.Combine(Application.dataPath, "..", "Library", "ParelVR", "VoltStorage"))
            };
            Options = options;
            _services = services;
            if (options.Module != null && options.Module.Length > 0)
            {
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
                    _player.ShutdownNetwork();
                    _player.ReleaseBody();
                    UnityEngine.Object.Destroy(go);
                    _player = null;
                    throw;
                }
                if (options.Error != null) World.ErrorReported += options.Error;
                if (options.Log != null) World.Logged += (level, text, context) => options.Log((int)level, text, context);
                _player.World = World;
                World.PlayerJoined(_player.LocalPlayer);
            }
            _player.JoinExisting();
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
                Scene = Options.Scene.IsValid() ? Options.Scene : SceneManager.GetActiveScene(),
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
            _player.RejoinRemotes();
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
            if (_player != null)
            {
                // Now, not at the end of the frame: a new start right after must be able to take the port.
                _player.ShutdownNetwork();
                _player.ReleaseBody();
                UnityEngine.Object.Destroy(_player.gameObject);
            }
            _player = null;
        }

        /// <summary>
        /// Play mode starts the simulator by itself when the scene carries a module, which is what the SDK's
        /// scene processing leaves behind when the scripts compiled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!Application.isEditor || SuppressAutoStart) return;
            // With domain reload off in the Enter Play Mode settings, the last session's world is still here.
            try { Stop(); } catch (Exception) { }
            try { VoltWorld.Current?.Unload(); } catch (Exception) { }
            World = null;
            _player = null;
            Scene active = SceneManager.GetActiveScene();
            // Enter Play Mode settings without a scene reload skip the scene processing, so the scripts are put in here.
            if (FindWorldScene(active) == null)
            {
                try { PrepareLoadedScene?.Invoke(active); }
                catch (Exception e) { Debug.LogWarning("[Volt] The scene could not be prepared for play mode: " + e.Message); }
            }
            // A world without scripts still gets the stand-in player, as it would in ParelVR.
            StartActiveScene(NextDebugger, IsWorld(active));
        }

        private static VoltWorldScene FindWorldScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                VoltWorldScene data = root.GetComponentInChildren<VoltWorldScene>(true);
                if (data != null) return data;
            }
            return null;
        }

        /// <summary>A ParelVR world scene: it has the world descriptor or a ParelVR Spawn Point.</summary>
        private static bool IsWorld(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return false;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.GetComponentInChildren<ParelVRSpawnPoint>(true) != null) return true;
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour != null && behaviour.GetType().Name == "ParelWorldDescriptor") return true;
            }
            return false;
        }

        /// <summary>
        /// Starts the scripts the active scene was prepared with. A subroom without scripts still gets the stand-in
        /// player when <paramref name="playerWithoutScripts"/> is set (the player walked into it with VoltScene.Switch).
        /// </summary>
        internal static void StartActiveScene(VoltDebugger debugger, bool playerWithoutScripts, VoltSimulatorOptions before = null)
        {
            VoltWorldScene data = FindWorldScene(SceneManager.GetActiveScene());
            bool scripts = data != null && data.editorModule != null && data.editorModule.Length > 0;
            if (!scripts && !playerWithoutScripts) return;
            try
            {
                VoltMultiClientOptions multi = MultiClientDefaults;
                if (multi != null && scripts)
                    multi = new VoltMultiClientOptions { Port = multi.Port, LatencyMs = multi.LatencyMs, LossPercent = multi.LossPercent, ModuleHash = data.moduleSha256 };
                Start(scripts
                    ? new VoltSimulatorOptions { Module = data.editorModule, Capabilities = data.editorCapabilities, WebDomains = data.editorWebDomains, Debugger = debugger, VR = data.editorVR, MultiClient = multi, PlayerName = before?.PlayerName }
                    : new VoltSimulatorOptions { Debugger = debugger, MultiClient = multi, VR = before != null && before.VR, PlayerName = before?.PlayerName });
            }
            catch (VoltFormatException e)
            {
                Debug.LogError("[Volt] The compiled scripts were not accepted by the VM, so the world runs without scripts: " + e.Message);
                StartWithoutScripts(debugger, before);
            }
            catch (Exception e)
            {
                // Whatever went wrong, play mode goes on without scripts instead of taking the editor down with it.
                Debug.LogError("[Volt] The simulator could not start, so the world runs without scripts: " + e);
                StartWithoutScripts(debugger, before);
            }
            Application.quitting -= Stop;
            Application.quitting += Stop;
        }

        /// <summary>The scripts failed: the player still spawns, so the rest of the world can be walked and tested.</summary>
        private static void StartWithoutScripts(VoltDebugger debugger, VoltSimulatorOptions before)
        {
            try { Stop(); } catch (Exception) { }
            try { Start(new VoltSimulatorOptions { Debugger = debugger, VR = before != null && before.VR, PlayerName = before?.PlayerName }); }
            catch (Exception e)
            {
                Debug.LogError("[Volt] The stand-in player could not spawn: " + e);
                try { Stop(); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// The stand-in player: a capsule you walk around with on desktop, or a headset and two controllers when the world
    /// is tested in VR (Volt Settings), and every service that concerns the player.
    /// </summary>
    internal sealed class SimulatedPlayer : MonoBehaviour, IVoltPlayerService, IVoltWorldInfo, IVoltInputService, IVoltInteractionService, IVoltNameplateService
    {
        private float EyeHeight = 1.6f;
        // As in the client: from the head, a desktop player reaches about an arm's length past what a hand could touch.
        private const float DesktopReach = 0.9f, RayLength = 15f, HoldNearest = 0.35f, HoldFurthest = 3f, HoldStep = 0.15f;
        private const float PoseInterval = 0.05f;

        internal VoltWorld World;
        internal SimulatedAvatar Body;
        private SimulatedNetwork _network;
        private VoltPlayerHandle _master;
        private float _poseClock;
        private readonly Dictionary<int, SimulatedRemotePlayer> _remotes = new Dictionary<int, SimulatedRemotePlayer>();
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

        public void Setup(Scene scene, VoltSimulatorOptions options, int localId, string displayName, SimulatedNetwork network)
        {
            _options = options;
            _network = network;
            _local = new VoltPlayerHandle(localId, "editor-" + localId, displayName, true) { PlatformObject = this };
            _players.Add(_local);
            _master = _local;
            Body = options.Body;
            if (Body != null) EyeHeight = Mathf.Clamp(Body.EyeHeight, 0.2f, 5f);

            FindSpawn(scene, out _spawnPosition, out _spawnRotation);

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
            _wantLock = !InVR;
            // The avatar under test shows itself; the loading avatar stands in otherwise. The nameplate is always there.
            _look = SimulatedStandIn.Build(transform, displayName, new Color(0.62f, 0.66f, 0.76f), Body == null, EyeHeight);
            Body?.Attach(this);
            SimulatedUIPointer.EventSystemFor();
            SimulatedVideoResolver.InstallIfMissing();
        }

        // ── the local player's look ──────────────────────────────────────────

        private SimulatedStandIn _look;

        internal Camera View => _camera;
        internal bool KeyHeld(KeyCode key) => _keys.Contains(key);
        internal bool Seated => _seat != null;
        internal Vector3 Velocity => _body != null ? _body.velocity : Vector3.zero;
        internal float Pitch => _pitch;
        internal bool Grounded => _body != null && _body.isGrounded;
        internal bool InVRNow => InVR;

        private void UpdateAvatar()
        {
            if (_look == null) return;
            Transform eye = _camera.transform;
            Vector3 head = eye.position;
            Quaternion headRotation = eye.rotation;
            if (Body != null && Body.TryGetHead(out Vector3 avatarHead)) head = new Vector3(head.x, Mathf.Max(avatarHead.y, transform.position.y + 0.2f), head.z);
            bool hasLeft = TryGetTracking(_local, VoltTrackingPoint.LeftHand, out Vector3 left, out _);
            bool hasRight = TryGetTracking(_local, VoltTrackingPoint.RightHand, out Vector3 right, out _);
            _look.Pose(transform.position, head, headRotation, hasLeft, left, hasRight, right);
        }

        public bool TryGetNameplateTop(VoltPlayerHandle player, out Vector3 top)
        {
            if (player != null && player.PlatformObject is SimulatedRemotePlayer remote) return remote.TryGetPlateTop(out top);
            top = default;
            return player == _local && _look != null && _look.TryGetPlateTop(out top);
        }

        // ── other clients (multi-client play mode) ───────────────────────────

        internal bool IsMultiClient => _network != null;

        private static SimulatedRemotePlayer RemoteOf(VoltPlayerHandle player) => player?.PlatformObject as SimulatedRemotePlayer;

        /// <summary>Players that were in the instance before this client: they join the world once it is loaded.</summary>
        internal void JoinExisting()
        {
            if (_network == null) return;
            foreach (KeyValuePair<int, string> existing in _network.Existing) AddRemote(existing.Key, existing.Value);
            VoltPlayerHandle master = Find(_network.MasterPlayerId);
            _master = master ?? _local; // who was master already: not a change scripts hear about
        }

        /// <summary>After a hot reload the new world meets the other players again.</summary>
        internal void RejoinRemotes()
        {
            foreach (SimulatedRemotePlayer remote in _remotes.Values) World?.PlayerJoined(remote.Handle);
        }

        internal void ShutdownNetwork()
        {
            _network?.Dispose();
        }

        /// <summary>The worn avatar is not the player's to destroy: it is handed back to the scene root first.</summary>
        internal void ReleaseBody()
        {
            if (Body == null) return;
            Body.Detach();
            if (Body != null && Body.transform.parent == transform)
            {
                Body.transform.SetParent(null, true);
                DontDestroyOnLoad(Body.gameObject);
            }
            Body = null;
        }

        private void PollNetwork()
        {
            if (_network == null) return;
            _network.Poll(OnNetwork);
        }

        private void OnNetwork(SimulatedNetwork.Incoming item)
        {
            switch (item.Kind)
            {
                case SimulatedNetwork.Kind.Joined:
                    AddRemote(item.Player, item.Name);
                    break;
                case SimulatedNetwork.Kind.Left:
                    RemoveRemote(item.Player);
                    break;
                case SimulatedNetwork.Kind.Data:
                    if (_remotes.ContainsKey(item.Player) && item.Data != null) World?.NetworkReceive(item.Player, item.Data, 0, item.Data.Length);
                    break;
                case SimulatedNetwork.Kind.Pose:
                    if (_remotes.TryGetValue(item.Player, out SimulatedRemotePlayer remote)) remote.Receive(item.Pose);
                    break;
                case SimulatedNetwork.Kind.Lost:
                    Debug.LogWarning("[Volt] Multi-client: the meeting point closed (the window that held it left play mode). This client plays alone now.");
                    foreach (int id in new List<int>(_remotes.Keys)) RemoveRemote(id);
                    break;
            }
            UpdateMaster();
        }

        private void AddRemote(int id, string displayName)
        {
            if (id == _local.Id || _remotes.ContainsKey(id)) return;
            var go = new GameObject("[Volt Remote Player " + id + "]");
            DontDestroyOnLoad(go);
            var remote = go.AddComponent<SimulatedRemotePlayer>();
            var handle = new VoltPlayerHandle(id, "editor-" + id, string.IsNullOrEmpty(displayName) ? "Player " + id : displayName, false) { PlatformObject = remote };
            remote.Setup(handle);
            remote.PassThrough = _body; // players walk through each other, as in the client
            _remotes.Add(id, remote);
            _players.Add(handle);
            World?.PlayerJoined(handle);
        }

        private void RemoveRemote(int id)
        {
            if (!_remotes.TryGetValue(id, out SimulatedRemotePlayer remote)) return;
            _remotes.Remove(id);
            _players.Remove(remote.Handle);
            if (_held != null && World != null && !World.IsHeldLocally(_held)) _held = null;
            World?.PlayerLeft(remote.Handle);
            remote.Handle.IsValid = false;
            if (remote != null) Destroy(remote.gameObject);
        }

        private void UpdateMaster()
        {
            if (_network == null) return;
            VoltPlayerHandle master = Find(_network.MasterPlayerId) ?? _local;
            if (master == _master) return;
            _master = master;
            World?.MasterChanged(master);
        }

        private void SendPose()
        {
            if (_network == null || !_network.Online) return;
            _poseClock -= Time.unscaledDeltaTime;
            if (_poseClock > 0f) return;
            _poseClock = PoseInterval;
            TryGetTracking(_local, VoltTrackingPoint.Head, out Vector3 head, out Quaternion headRotation);
            TryGetTracking(_local, VoltTrackingPoint.LeftHand, out Vector3 left, out Quaternion leftRotation);
            TryGetTracking(_local, VoltTrackingPoint.RightHand, out Vector3 right, out Quaternion rightRotation);
            _network.SendPose(new SimulatedPose
            {
                Feet = transform.position,
                Yaw = GetRotation(_local).eulerAngles.y,
                Velocity = Velocity,
                Head = head,
                HeadRotation = headRotation,
                LeftHand = left,
                LeftRotation = leftRotation,
                RightHand = right,
                RightRotation = rightRotation,
                EyeHeight = EyeHeight,
                Grounded = Grounded,
                InVR = InVR,
                Fps = GetFps(_local)
            });
        }

        // ── world UI and the keyboard ────────────────────────────────────────

        private readonly SimulatedUIPointer _ui = new SimulatedUIPointer(-1);
        private readonly SimulatedKeyboard _keyboard = new SimulatedKeyboard();
        private bool _uiKeyWas;
        private Vector2 _scroll;

        private bool Typing => _keyboard.Field != null;

        /// <summary>
        /// Points at world UI. Keys of the on-screen keyboard are pressed here rather than through the EventSystem, so
        /// the text field keeps its focus.
        /// </summary>
        private void PointUI(SimulatedUIPointer pointer, Ray ray, bool down, ref bool keyWas, Vector2 scroll)
        {
            pointer.Cast(ray, RayLength, out GameObject target, out _, out _);
            if (_keyboard.Owns(target))
            {
                _keyboard.Hover(target);
                if (down && !keyWas) _keyboard.Press(target);
                keyWas = down;
                pointer.Process(ray, false, RayLength, Vector2.zero);
                return;
            }
            keyWas = down;
            pointer.Process(ray, down, RayLength, scroll);
        }

        /// <summary>
        /// Where the client would put the player: the enabled ParelVR Spawn Point with the highest priority, preferring
        /// the world's own scene; then the game's SpawnPoint or an object named Spawn; then the scene origin.
        /// </summary>
        private static void FindSpawn(Scene scene, out Vector3 position, out Quaternion rotation)
        {
            ParelVRSpawnPoint best = null;
            bool bestHere = false;
            foreach (ParelVRSpawnPoint marker in FindObjectsByType<ParelVRSpawnPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (marker == null || !marker.EnabledForRuntime) continue;
                bool here = marker.gameObject.scene == scene;
                if (best == null || (here && !bestHere) || (here == bestHere && marker.Priority > best.Priority))
                {
                    best = marker;
                    bestHere = here;
                }
            }
            Transform point = best != null ? best.transform : null;
            if (point == null)
            {
                foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (behaviour != null && behaviour.GetType().Name == "SpawnPoint") { point = behaviour.transform; break; }
            }
            if (point == null)
            {
                GameObject named = GameObject.Find("Spawn") ?? GameObject.Find("SpawnPoint");
                if (named != null) point = named.transform;
            }
            if (point == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return;
            }
            position = point.position;
            float yaw = point.eulerAngles.y;
            if (best != null && best.FacingMode == ParelVRSpawnFacingMode.FaceWorldForward) yaw = 0f;
            else if (best != null && best.FacingMode == ParelVRSpawnFacingMode.FaceWorldCenter)
            {
                Vector3 toCenter = -new Vector3(position.x, 0f, position.z);
                if (toCenter.sqrMagnitude > 0.0001f) yaw = Quaternion.LookRotation(toCenter).eulerAngles.y;
            }
            rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // ── mouse look ───────────────────────────────────────────────────────

        // Play mode takes the mouse, as in the client: no dragging to look around. Esc gives it back, a click takes it again.
        private const float LookDegrees = 2f;
        private bool _wantLock, _locked, _legacyMouseMissing;
        private Func<Vector2> _inputSystemDelta;

        private void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _locked = true;
        }

        private void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _locked = false;
        }

        private bool _relock;
        private Func<Vector2> _inputSystemPosition;

        /// <summary>A menu takes the mouse: the cursor shows and the view stops turning.</summary>
        internal void ReleaseMouse()
        {
            _relock = _locked || _wantLock;
            _wantLock = false;
            UnlockCursor();
        }

        /// <summary>The menu is closed: the mouse looks around again if it did before.</summary>
        internal void RestoreMouse()
        {
            if (_relock && !InVR) LockCursor();
            _relock = false;
        }

        /// <summary>The cursor in screen pixels (origin bottom left), from whichever input backend the project uses.</summary>
        internal Vector2 MousePosition()
        {
            if (!_legacyMouseMissing)
            {
                try { return UnityEngine.Input.mousePosition; }
                catch (InvalidOperationException) { _legacyMouseMissing = true; }
            }
            if (_inputSystemPosition == null)
            {
                Type mouse = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
                System.Reflection.PropertyInfo current = mouse?.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                System.Reflection.PropertyInfo position = mouse?.GetProperty("position");
                System.Reflection.MethodInfo read = position?.PropertyType.GetMethod("ReadValue", Type.EmptyTypes);
                _inputSystemPosition = current == null || read == null ? (Func<Vector2>)(() => new Vector2(Screen.width / 2f, Screen.height / 2f)) : () =>
                {
                    object device = current.GetValue(null);
                    object control = device != null ? position.GetValue(device) : null;
                    return control != null ? (Vector2)read.Invoke(control, null) : new Vector2(Screen.width / 2f, Screen.height / 2f);
                };
            }
            return _inputSystemPosition();
        }

        /// <summary>Mouse movement this frame in the old input manager's units, read from whichever input backend the project uses.</summary>
        private Vector2 MouseDelta()
        {
            if (!_legacyMouseMissing)
            {
                try { return new Vector2(UnityEngine.Input.GetAxisRaw("Mouse X"), UnityEngine.Input.GetAxisRaw("Mouse Y")); }
                catch (InvalidOperationException) { _legacyMouseMissing = true; } // "Input System Package (New)" only
                catch (ArgumentException) { _legacyMouseMissing = true; }         // the axes were removed
            }
            if (_inputSystemDelta == null) _inputSystemDelta = InputSystemDelta();
            return _inputSystemDelta();
        }

        // The Input System package, found by name so the simulator works in projects without it.
        private static Func<Vector2> InputSystemDelta()
        {
            Type mouse = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
            System.Reflection.PropertyInfo current = mouse?.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            System.Reflection.PropertyInfo delta = mouse?.GetProperty("delta");
            System.Reflection.MethodInfo read = delta?.PropertyType.GetMethod("ReadValue", Type.EmptyTypes);
            if (current == null || read == null) return () => Vector2.zero;
            return () =>
            {
                object device = current.GetValue(null);
                object control = device != null ? delta.GetValue(device) : null;
                return control != null ? (Vector2)read.Invoke(control, null) * 0.1f : Vector2.zero; // pixels to the old units
            };
        }

        private void UpdateMouseLook()
        {
            if (Body != null && Body.CapturesMouse) return; // the mouse points in the menu or moves the test hand
            // At the start the Game view may not have focus yet; the lock waits for it.
            if (_wantLock && Application.isFocused)
            {
                _wantLock = false;
                LockCursor();
            }
            // The editor frees the cursor on Esc and when the Game view loses focus.
            if (_locked && Cursor.lockState != CursorLockMode.Locked) UnlockCursor();
            if (!_locked) return;
            Vector2 look = MouseDelta();
            if (float.IsNaN(look.x) || float.IsNaN(look.y)) return;
            _yaw += look.x * LookDegrees;
            _pitch = Mathf.Clamp(_pitch - look.y * LookDegrees, -89f, 89f);
        }

        // ── players ──────────────────────────────────────────────────────────

        public VoltPlayerHandle LocalPlayer => _local;
        public VoltPlayerHandle Master => _master ?? _local;
        public IReadOnlyList<VoltPlayerHandle> Players => _players;

        public VoltPlayerHandle Find(int playerId)
        {
            foreach (VoltPlayerHandle player in _players)
                if (player.Id == playerId) return player;
            return null;
        }

        public VoltPlayerHandle PlayerOf(Collider collider)
        {
            if (collider == null) return null;
            if (collider == _body) return _local;
            foreach (SimulatedRemotePlayer remote in _remotes.Values)
                if (remote != null && remote.Collider == collider) return remote.Handle;
            return null;
        }

        public Vector3 GetPosition(VoltPlayerHandle player) => RemoteOf(player) is SimulatedRemotePlayer r ? r.Pose.Feet : transform.position;
        public Quaternion GetRotation(VoltPlayerHandle player) =>
            RemoteOf(player) is SimulatedRemotePlayer r ? Quaternion.Euler(0f, r.Pose.Yaw, 0f) : Quaternion.Euler(0f, InVR ? _camera.transform.eulerAngles.y : _yaw, 0f);
        public Vector3 GetVelocity(VoltPlayerHandle player) => RemoteOf(player) is SimulatedRemotePlayer r ? r.Pose.Velocity : _body != null ? _body.velocity : Vector3.zero;
        public bool IsGrounded(VoltPlayerHandle player) => RemoteOf(player) is SimulatedRemotePlayer r ? r.Pose.Grounded : _body != null && _body.isGrounded;
        public bool IsInVR(VoltPlayerHandle player) => RemoteOf(player) is SimulatedRemotePlayer r ? r.Pose.InVR : InVR;
        public float GetAvatarEyeHeight(VoltPlayerHandle player) => RemoteOf(player) is SimulatedRemotePlayer r ? r.Pose.EyeHeight : EyeHeight;
        public string GetAvatarId(VoltPlayerHandle player) => Body != null && player == _local ? Body.AvatarId : "simulated";
        public int GetFps(VoltPlayerHandle player) =>
            RemoteOf(player) is SimulatedRemotePlayer r ? r.Pose.Fps : Time.smoothDeltaTime > 0f ? Mathf.RoundToInt(1f / Time.smoothDeltaTime) : 0;
        public int GetPingMs(VoltPlayerHandle player) => _network != null ? _network.PingMs : 0;

        public bool TryGetTracking(VoltPlayerHandle player, VoltTrackingPoint point, out Vector3 position, out Quaternion rotation)
        {
            if (RemoteOf(player) is SimulatedRemotePlayer remote) return remote.TryGetTracking(point, out position, out rotation);
            if (Body != null && Body.TryGetTracking(point, out position, out rotation)) return true;
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
            if (Body != null && player == _local && Body.TryGetBone(bone, out position, out rotation)) return true;
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
        public string InstanceId => _network != null ? "editor-multi-client" : "editor";
        public int Capacity => _network != null ? 16 : 1;
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
        private VoltInteractionTarget Aim(Vector3 origin, Vector3 direction, float extraReach, out Vector3 point, float limit = float.PositiveInfinity)
        {
            point = origin + direction * 2f;
            int count = Physics.RaycastNonAlloc(origin, direction, _hits, RayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            Array.Sort(_hits, 0, count, ByDistance);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.distance > limit) break; // UI in front of it gets the pointer
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
            PollNetwork();
#if VOLT_SIM_XR
            if (InVR) ReadVR();
#endif
            bool still = _immobile || (Body != null && Body.LocomotionDisabled);
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
                Vector2 axis = still ? Vector2.zero : GetAxis(VoltInputAction.Movement);
                float heading = InVR ? _camera.transform.eulerAngles.y : _yaw;
                Vector3 move = Quaternion.Euler(0f, heading, 0f) * Vector3.ClampMagnitude(new Vector3(axis.x, 0f, axis.y), 1f) * speed + _velocity;
                _velocity = Vector3.Lerp(_velocity, Vector3.zero, 5f * Time.deltaTime);
                if (_body.isGrounded)
                {
                    if (_vertical < 0f) _vertical = -1f;
                    if (_jumpQueued && !still) _vertical = JumpImpulse;
                }
                _jumpQueued = false;
                _vertical += Physics.gravity.y * GravityStrength * Time.deltaTime;
                Vector3 step = (move + Vector3.up * _vertical) * Time.deltaTime;
                if (Finite(step)) _body.Move(step);
                if (transform.position.y < -100f) Respawn();
            }
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            SendPose();

#if VOLT_SIM_XR
            if (InVR)
            {
                PoseHead();
                UpdateHand(_leftHand, 1);
                UpdateHand(_rightHand, 2);
                _aim = null;
                World?.SetHover(0, null);
                _ui.Process(new Ray(_camera.transform.position, _camera.transform.forward), false, 0f, Vector2.zero);
                UpdateAvatar();
                _keyboard.Update(_camera.transform);
                return;
            }
            HideRays();
#endif
            UpdateMouseLook();
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            _camera.transform.localPosition = new Vector3(0f, EyeHeight, Body != null ? Body.ViewForward : 0f);
            _camera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
            Body?.AdjustView(_camera.transform);
            // The pickup may have been taken away or dropped by a script.
            if (_held != null && (World == null || !World.IsLoaded || !World.IsHeldLocally(_held))) _held = null;
            Transform eye = _camera.transform;
            var ray = new Ray(eye.position, eye.forward);
            bool menu = Body != null && Body.CapturesMouse;
            // World UI in front of what the crosshair would use gets the mouse instead.
            float ui = _held == null && !menu ? _ui.Cast(ray, RayLength, out _, out _, out _) : float.PositiveInfinity;
            _aim = _held == null && !menu ? Aim(eye.position, eye.forward, DesktopReach, out _, ui) : null;
            bool onUI = _aim == null && !float.IsInfinity(ui);
            PointUI(_ui, ray, onUI && _mouseLeft, ref _uiKeyWas, onUI ? _scroll : Vector2.zero);
            _scroll = Vector2.zero;
            World?.SetHover(0, _aim != null ? _aim.Object : null);
            UpdateAvatar();
            _keyboard.Update(eye);
            if (Typing) _keys.Clear(); // keys typed into a field do not walk
        }

        // IMGUI events work with either input backend, so the simulator needs nothing from the project's input settings.
        private void OnGUI()
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.None) break;
                    if (Typing)
                    {
                        if (e.keyCode == KeyCode.Escape && UnityEngine.EventSystems.EventSystem.current != null)
                            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
                        break;
                    }
                    if (_keys.Add(e.keyCode)) Pressed(e.keyCode);
                    break;
                case EventType.KeyUp:
                    if (_keys.Remove(e.keyCode) && e.keyCode == KeyCode.F && _held != null) World?.PickupUse(_held, false);
                    break;
                case EventType.MouseDown:
                    if (Body != null && Body.CapturesMouse)
                    {
                        Body.MouseButton(e.button, true);
                        if (Body.MenuOpen || e.button != 1) break; // the right button still turns the view around the test hand
                    }
                    // A click on the Game view while the mouse is free only takes the mouse back.
                    if (e.button == 0 && !_locked && !InVR)
                    {
                        LockCursor();
                        break;
                    }
                    if (e.button == 0)
                    {
                        _mouseLeft = true;
                        if (_held == null) _held = Use(_aim, VoltHand.Right);
                        else World?.PickupUse(_held, true);
                    }
                    if (e.button == 1) _mouseRight = true;
                    break;
                case EventType.MouseUp:
                    if (Body != null && Body.CapturesMouse) Body.MouseButton(e.button, false);
                    if (e.button == 0)
                    {
                        _mouseLeft = false;
                        if (_held != null) World?.PickupUse(_held, false);
                    }
                    if (e.button == 1) _mouseRight = false;
                    break;
                case EventType.MouseDrag:
                    if (e.button != 1 || (Body != null && Body.MenuOpen)) break;
                    _yaw += e.delta.x * 0.2f;
                    _pitch = Mathf.Clamp(_pitch + e.delta.y * 0.2f, -89f, 89f);
                    break;
                case EventType.ScrollWheel:
                    if (Body != null && Body.CapturesMouse)
                    {
                        if (!Body.MenuOpen) Body.Scroll(e.delta.y / 3f);
                        break;
                    }
                    // Over UI the wheel scrolls it (three IMGUI units a notch); with a held object it pushes or pulls it, as in the client.
                    if (_held == null) _scroll += new Vector2(e.delta.x, -e.delta.y) / 3f;
                    if (_held == null || World == null || Mathf.Abs(e.delta.y) < 0.01f) break;
                    Transform eye = _camera.transform;
                    World.PushHeld(_held, eye.position, eye.forward, -Mathf.Sign(e.delta.y) * HoldStep, HoldNearest, HoldFurthest);
                    break;
            }
            if (e.type != EventType.Repaint) return;

            if (_network != null)
            {
                string who = _local.DisplayName + (Master == _local ? " (master)" : string.Empty);
                GUI.Label(new Rect(10f, 8f, Screen.width - 20f, 20f), _network.Online
                    ? "Multi-client: " + who + "    " + _players.Count + (_players.Count == 1 ? " player" : " players") + (_network.IsRelayHost ? "    meeting point" : string.Empty)
                    : "Multi-client: connection closed, " + who + " plays alone");
            }
            var hint = new Rect(10f, Screen.height - 26f, Screen.width - 20f, 20f);
            string mouse = _locked ? "Esc free mouse" : "click to look around";
            string avatar = Body != null ? "    " + Body.Hint : string.Empty;
            GUI.Label(hint, Body != null && Body.CapturesMouse ? Body.Hint
                : InVR ? "VR: grip grab    trigger use    left stick move    right stick turn    A jump" + avatar
                : _held != null ? "Click / F use    G drop    wheel push / pull    WASD move    " + mouse
                : _seat != null ? "Space stand up    " + mouse + avatar
                : "WASD move    Space jump    Shift run    click / E use and pick up    " + mouse + avatar);
            if (InVR || (Body != null && Body.CapturesMouse)) return;
            GUI.Label(new Rect(Screen.width / 2f - 4f, Screen.height / 2f - 10f, 20f, 20f), "+");
            if (_aim != null)
            {
                string text = SafeText(_aim);
                GUI.Label(new Rect(Screen.width / 2f - 150f, Screen.height / 2f + 16f, 300f, 22f), text, new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
            }
        }

        private void Pressed(KeyCode key)
        {
            if (Body != null && Body.KeyPressed(key)) return;
            if (key == KeyCode.Escape) UnlockCursor();
            else if (key == KeyCode.Space) _jumpQueued = true;
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
            if (_locked) UnlockCursor();
            _keyboard.Destroy();
            _look?.Destroy();
            foreach (SimulatedRemotePlayer remote in _remotes.Values)
                if (remote != null) Destroy(remote.gameObject);
            _remotes.Clear();
            ShutdownNetwork();
            Body?.Detach();
#if VOLT_SIM_XR
            StopVR();
#endif
        }

        // ── VR ───────────────────────────────────────────────────────────────

#if !VOLT_SIM_XR
        private bool InVR => false;
        internal bool VRMenuToggle() => false;
        internal void VRMenuInput(out Vector2 stick, out bool choose, out bool back) { stick = Vector2.zero; choose = back = false; }
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
            public SimulatedUIPointer UI;
            public bool KeyWas, OnUI;
            public LineRenderer Ray;
        }

        private readonly Hand _leftHand = new Hand { Side = VoltHand.Left, Node = XRNode.LeftHand, UI = new SimulatedUIPointer(-2) };
        private readonly Hand _rightHand = new Hand { Side = VoltHand.Right, Node = XRNode.RightHand, UI = new SimulatedUIPointer(-3) };
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
            bool menu = Body != null && Body.MenuOpen;
            if (_rightHand.Primary && !_rightHand.PrimaryWas && !menu) _jumpQueued = true;
            // Snap turn around the head, so the view does not swing sideways. The open menu has the right stick.
            float x = _rightHand.OnUI || menu ? 0f : _rightHand.Stick.x;
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

        private bool _menuButtonWas;

        /// <summary>The left menu button was pressed this frame: it opens and closes the avatar's radial menu.</summary>
        internal bool VRMenuToggle()
        {
            bool pressed = _leftHand.Menu && !_menuButtonWas;
            _menuButtonWas = _leftHand.Menu;
            return pressed;
        }

        /// <summary>The right stick points in the menu, the trigger chooses, A goes back.</summary>
        internal void VRMenuInput(out Vector2 stick, out bool choose, out bool back)
        {
            stick = _rightHand.Stick;
            choose = _rightHand.Trigger && !_rightHand.TriggerWas;
            back = _rightHand.Primary && !_rightHand.PrimaryWas;
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
                var ray = new Ray(hand.Position, hand.Rotation * Vector3.forward);
                float ui = hand.UI.Cast(ray, RayLength, out _, out _, out _);
                hand.Aim = Aim(hand.Position, ray.direction, 0f, out end, ui);
                bool grabbed = hand.Grip && !hand.GripWas, pressed = hand.Trigger && !hand.TriggerWas;
                if (hand.Aim != null && (grabbed || pressed))
                {
                    if (hand.Aim.Kind == VoltInteractionKind.Pickup) hand.Held = Use(hand.Aim, hand.Side);
                    else if (pressed) Use(hand.Aim, hand.Side);
                }
                // The trigger clicks UI the hand points at; the right stick scrolls it.
                hand.OnUI = hand.Aim == null && !float.IsInfinity(ui);
                PointUI(hand.UI, hand.OnUI ? ray : new Ray(ray.origin, Vector3.zero), hand.OnUI && hand.Trigger, ref hand.KeyWas,
                    hand.OnUI && hand.Side == VoltHand.Right ? new Vector2(0f, hand.Stick.y) * 0.25f : Vector2.zero);
                if (hand.OnUI) end = hand.UI.Point;
            }
            else hand.Aim = null;
            if (!hand.Tracked || hand.Held != null)
            {
                hand.OnUI = false;
                hand.UI.Process(new Ray(hand.Position, Vector3.zero), false, 0f, Vector2.zero);
            }
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
            bool pointing = hand.Aim != null || hand.OnUI;
            Color colour = pointing ? new Color(0.45f, 0.75f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.25f);
            hand.Ray.startColor = hand.Ray.endColor = colour;
            hand.Ray.SetPosition(0, hand.Position);
            hand.Ray.SetPosition(1, pointing ? end : hand.Position + hand.Rotation * Vector3.forward * 2f);
        }

        private void HideRays()
        {
            if (_leftHand.Ray != null) _leftHand.Ray.enabled = false;
            if (_rightHand.Ray != null) _rightHand.Ray.enabled = false;
        }
#endif
    }

    /// <summary>
    /// VoltScene.Switch in play mode: the scene playing is swapped for the subroom (a scene from Build Profiles), the
    /// scripts stop, and the subroom's own scripts and the stand-in player start there, as in the game.
    /// </summary>
    internal sealed class SimulatedScenes : MonoBehaviour, IVoltSceneService
    {
        private static SimulatedScenes _instance;
        private bool _switching;

        public static SimulatedScenes Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("[Volt Subrooms]") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                return _instance = go.AddComponent<SimulatedScenes>();
            }
        }

        private static string NameOf(string path) => Path.GetFileNameWithoutExtension(path ?? string.Empty);

        public string Switch(string sceneName)
        {
            string path = null;
            foreach (string candidate in VoltSimulator.WorldScenes ?? Array.Empty<string>())
                if (string.Equals(NameOf(candidate), sceneName, StringComparison.OrdinalIgnoreCase)) { path = candidate; break; }
            if (path == null) return "You're calling \"" + sceneName + "\", but \"" + sceneName + "\" isn't in Build Profiles.";
            if (VoltSimulator.LoadScene == null) return "Subrooms switch in play mode only.";
            if (string.Equals(SceneManager.GetActiveScene().path, path, StringComparison.OrdinalIgnoreCase) || _switching) return null;
            _switching = true;
            StartCoroutine(Run(path));
            return null;
        }

        private System.Collections.IEnumerator Run(string path)
        {
            try
            {
                // The script that asked finishes its frame first.
                yield return null;
                VoltSimulatorOptions before = VoltSimulator.Options;
                VoltDebugger debugger = before?.Debugger;
                try { VoltSimulator.Stop(); }
                catch (Exception e) { Debug.LogException(e); }
                AsyncOperation load = null;
                try { load = VoltSimulator.LoadScene(path); }
                catch (Exception e) { Debug.LogError("[Volt] Could not open the subroom " + NameOf(path) + ": " + e.Message); }
                if (load != null) yield return load;
                yield return null;
                try { VoltSimulator.PrepareLoadedScene?.Invoke(SceneManager.GetActiveScene()); }
                catch (Exception e) { Debug.LogException(e); }
                Debug.Log("[Volt] Switched to the subroom " + NameOf(path) + ".");
                VoltSimulator.StartActiveScene(debugger, true, before);
            }
            finally
            {
                _switching = false;
            }
        }
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
