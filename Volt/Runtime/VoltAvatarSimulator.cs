using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ParelVR.AvatarSDK;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Volt.Simulator
{
    using Scene = UnityEngine.SceneManagement.Scene;

    /// <summary>
    /// Tests an avatar in play mode the way it is worn in ParelVR: the avatar becomes the simulator's player, in a world
    /// from the backend (the ParelVR Home Hub) with that world's scripts running, its Expressions Menu on the radial menu,
    /// and its playable layers, parameters, state behaviours, Motion Bones and contacts all running.
    /// The editor fetches the world and starts this; see the SDK menu ParelVR SDK > Avatar Simulator.
    /// </summary>
    public static class VoltAvatarSimulator
    {
        public sealed class Request
        {
            /// <summary>The avatar to wear: an object with a ParelAvatarDescriptor in the open scene.</summary>
            public GameObject Avatar;
            /// <summary>The world's bundle for this platform. Null or missing: a plain floor is used instead.</summary>
            public string BundlePath;
            /// <summary>The world's validated scripts. Null: the world runs without them.</summary>
            public byte[] Module;
            public uint Capabilities;
            public string[] WebDomains;
            public string WorldName = "ParelVR Home Hub";
            public string PlayerName;
            public bool VR;
        }

        private sealed class Runner : MonoBehaviour { }

        private static Runner _runner;
        private static AssetBundle _bundle;

        public static bool IsRunning { get; private set; }
        public static ParelAvatarDescriptor Avatar { get; private set; }

        /// <summary>Starts the test. Call it in play mode.</summary>
        public static void Begin(Request request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!Application.isPlaying) throw new InvalidOperationException("The avatar simulator runs in play mode.");
            if (_runner == null)
            {
                var go = new GameObject("[Avatar Simulator]") { hideFlags = HideFlags.HideInHierarchy };
                UnityEngine.Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<Runner>();
            }
            _runner.StartCoroutine(Run(request));
        }

        private static IEnumerator Run(Request request)
        {
            ParelAvatarDescriptor descriptor = request.Avatar != null ? request.Avatar.GetComponentInParent<ParelAvatarDescriptor>() : null;
            if (descriptor == null)
            {
                Debug.LogError("[Avatar Simulator] No avatar to wear: the object needs a ParelVR Avatar Descriptor.");
                yield break;
            }
            IsRunning = true;
            Avatar = descriptor;
            GameObject avatar = descriptor.gameObject;
            avatar.transform.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(avatar);

            // The scenes that were open (the avatar's own) make way for the world.
            var previous = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++) previous.Add(SceneManager.GetSceneAt(i));

            Scene world = default;
            if (!string.IsNullOrEmpty(request.BundlePath) && File.Exists(request.BundlePath))
            {
                AssetBundleCreateRequest load = AssetBundle.LoadFromFileAsync(request.BundlePath);
                yield return load;
                _bundle = load.assetBundle;
                string[] scenes = _bundle != null ? _bundle.GetAllScenePaths() : null;
                if (scenes == null || scenes.Length == 0)
                {
                    Debug.LogWarning("[Avatar Simulator] The world's bundle could not be opened (it may be for another platform). Using a plain floor.");
                    if (_bundle != null) _bundle.Unload(true);
                    _bundle = null;
                }
                else
                {
                    AsyncOperation open = SceneManager.LoadSceneAsync(scenes[0], LoadSceneMode.Additive);
                    if (open != null) yield return open;
                    world = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
                }
            }
            if (!world.IsValid() || !world.isLoaded) world = PlainFloor();
            SceneManager.SetActiveScene(world);
            foreach (Scene scene in previous)
                if (scene.IsValid() && scene.isLoaded && scene != world)
                {
                    AsyncOperation close = SceneManager.UnloadSceneAsync(scene);
                    if (close != null) yield return close;
                }
            _bundle?.Unload(false);
            _bundle = null;

            SimulatedAvatar body = SimulatedAvatar.Wear(descriptor);
            try
            {
                VoltSimulator.Start(new VoltSimulatorOptions
                {
                    Module = request.Module,
                    Scene = world,
                    Capabilities = request.Capabilities,
                    WebDomains = request.WebDomains,
                    WorldName = request.WorldName,
                    PlayerName = request.PlayerName,
                    Avatar = body,
                    Body = body,
                    VR = request.VR
                });
            }
            catch (Exception e)
            {
                Debug.LogError("[Avatar Simulator] The world's scripts could not start, so the world runs without them: " + e.Message);
                VoltSimulator.Start(new VoltSimulatorOptions { Scene = world, WorldName = request.WorldName, PlayerName = request.PlayerName, Avatar = body, Body = body, VR = request.VR });
            }
            Debug.Log("[Avatar Simulator] " + descriptor.gameObject.name + " is in " + request.WorldName + ". " + body.Hint + ".");
        }

        /// <summary>When there is no world to load: a floor, a light and a sky, so the avatar can still be tried.</summary>
        private static Scene PlainFloor()
        {
            Scene scene = SceneManager.CreateScene("Avatar Simulator");
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(4f, 1f, 4f);
            SceneManager.MoveGameObjectToScene(floor, scene);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            SceneManager.MoveGameObjectToScene(light.gameObject, scene);
            return scene;
        }
    }
}
