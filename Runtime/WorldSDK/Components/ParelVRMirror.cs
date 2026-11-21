using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// A mirror for worlds. Put it on a flat mesh (a quad) whose front faces the room; it shows what is in front of
    /// it, in VR one picture per eye. It renders itself, so it works the same in ParelVR and in the editor's play mode.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshRenderer))]
    [AddComponentMenu("ParelVR/World SDK/ParelVR Mirror")]
    public sealed class ParelVRMirror : MonoBehaviour
    {
        [Tooltip("Layers the mirror shows.")]
        public LayerMask reflectLayers = ~0;
        [Tooltip("Pixels along each side of each eye's picture. Higher is sharper and costs more.")]
        [Range(256, 4096)] public int resolution = 1024;
        [Tooltip("Smooths edges: 1 (off), 2, 4 or 8 samples.")]
        [Range(1, 8)] public int antiAliasing = 2;
        [Tooltip("Farther than this (metres) from the mirror, it stops updating.")]
        public float maxDistance = 30f;
        [Tooltip("How far into the reflection things are drawn (metres).")]
        public float farClipPlane = 100f;
        [Tooltip("Colour the reflection is multiplied by (when the material has a _Tint colour).")]
        public Color tint = Color.white;
        [Tooltip("Optional: your own material, for a custom mirror look (frames, tints, distortion, fades). Its shader receives the " +
                 "reflection as _ReflectionTexLeft and _ReflectionTexRight; sample them at the screen position flipped on X, as the " +
                 "SDK Mirror shader does. Empty: the plain SDK mirror.")]
        public Material customMaterial;
        [SerializeField, HideInInspector] private Shader shader;

        /// <summary>Raised around every mirror render, so the platform can show the local player's head (hidden in first person) in the reflection.</summary>
        public static event Action BeforeMirrorRender, AfterMirrorRender;

        private const string ShaderName = "ParelVR/SDK Mirror";
        private const float ClipPlaneOffset = 0.001f;
        private static readonly int LeftId = Shader.PropertyToID("_ReflectionTexLeft");
        private static readonly int RightId = Shader.PropertyToID("_ReflectionTexRight");
        private static readonly int EyeLeftId = Shader.PropertyToID("_ParelEyePosLeft");
        private static readonly int EyeRightId = Shader.PropertyToID("_ParelEyePosRight");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static bool _rendering;

        private Renderer _renderer;
        private Material _material, _original;
        private Camera _leftCamera, _rightCamera;
        private RenderTexture _leftTexture, _rightTexture;
        private Vector4 _leftEye, _rightEye;
        private int _size, _samples;

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            _renderer = GetComponent<Renderer>();
            if (shader == null) shader = Shader.Find(ShaderName);
            if (_renderer == null || (customMaterial == null && shader == null))
            {
                Debug.LogWarning("[ParelVR Mirror] The mirror shader is missing, so " + name + " shows its own material.");
                return;
            }
            _original = _renderer.sharedMaterial;
            // A copy: every mirror gets its own reflection textures even when several share one material.
            _material = customMaterial != null ? new Material(customMaterial) : new Material(shader);
            _material.name = "Mirror (" + name + ")";
            _material.hideFlags = HideFlags.DontSave;
            _renderer.sharedMaterial = _material;
            Application.onBeforeRender += Render;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= Render;
            if (_renderer != null && _material != null && _renderer.sharedMaterial == _material) _renderer.sharedMaterial = _original;
            Release();
            if (_material != null) Destroy(_material);
            _material = null;
        }

        private void Release()
        {
            if (_leftCamera != null) Destroy(_leftCamera.gameObject);
            if (_rightCamera != null) Destroy(_rightCamera.gameObject);
            if (_leftTexture != null) { _leftTexture.Release(); Destroy(_leftTexture); }
            if (_rightTexture != null) { _rightTexture.Release(); Destroy(_rightTexture); }
            _leftCamera = _rightCamera = null;
            _leftTexture = _rightTexture = null;
        }

        // After the game moves the head and the avatar to this frame's pose (they run in onBeforeRender at order 0):
        // in the order they were added, a mirror already in the world when the player loaded in drew from last
        // frame's head, and the reflection swam until it was turned off and on.
        [BeforeRenderOrder(1000)]
        private void Render()
        {
            if (_rendering || _material == null || _renderer == null || !_renderer.isVisible) return;
            Camera view = Camera.main;
            if (view == null || (view.transform.position - transform.position).sqrMagnitude > maxDistance * maxDistance) return;

            bool stereo = view.stereoEnabled;
            Prepare(view, stereo);
            _rendering = true;
            try
            {
                BeforeMirrorRender?.Invoke();
                view.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
                if (stereo)
                {
                    RenderEye(view, Camera.MonoOrStereoscopicEye.Left, _leftCamera, position, rotation, ref _leftEye);
                    RenderEye(view, Camera.MonoOrStereoscopicEye.Right, _rightCamera, position, rotation, ref _rightEye);
                }
                else
                {
                    RenderEye(view, Camera.MonoOrStereoscopicEye.Mono, _leftCamera, position, rotation, ref _leftEye);
                    _rightEye = _leftEye;
                }
                _material.SetTexture(LeftId, _leftTexture);
                _material.SetTexture(RightId, stereo ? _rightTexture : _leftTexture);
                _material.SetVector(EyeLeftId, _leftEye);
                _material.SetVector(EyeRightId, _rightEye);
                if (_material.HasProperty(TintId)) _material.SetColor(TintId, tint);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
            finally
            {
                try { AfterMirrorRender?.Invoke(); }
                catch (Exception e) { Debug.LogException(e, this); }
                _rendering = false;
            }
        }

        private void Prepare(Camera view, bool stereo)
        {
            int size = Mathf.Clamp(resolution, 256, 4096);
            int samples = Mathf.ClosestPowerOfTwo(Mathf.Clamp(antiAliasing, 1, 8));
            if (size != _size || samples != _samples) Release();
            _size = size;
            _samples = samples;
            if (_leftCamera == null) _leftCamera = MakeCamera(view, "Left", out _leftTexture);
            if (stereo && _rightCamera == null) _rightCamera = MakeCamera(view, "Right", out _rightTexture);
        }

        private Camera MakeCamera(Camera view, string eye, out RenderTexture texture)
        {
            var description = new RenderTextureDescriptor(_size, _size, RenderTextureFormat.Default, 24)
            {
                msaaSamples = _samples,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                useMipMap = false,
                vrUsage = VRTextureUsage.None,
                dimension = TextureDimension.Tex2D
            };
            texture = new RenderTexture(description) { name = "Mirror " + eye, hideFlags = HideFlags.DontSave };
            texture.Create();

            var go = new GameObject("Mirror Camera " + eye) { hideFlags = HideFlags.HideAndDontSave };
            var camera = go.AddComponent<Camera>();
            camera.CopyFrom(view);
            // A copied headset camera would take its pose and projection from the headset on Render(), throwing away
            // the reflected ones set below; these cameras draw one plain picture into a texture.
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.enabled = false;
            // The player's own screen (HUD, notifications, menu: the UI and OverlayUI layers) is never reflected.
            int layers = reflectLayers;
            int ui = LayerMask.NameToLayer("UI"), overlay = LayerMask.NameToLayer("OverlayUI");
            if (ui >= 0) layers &= ~(1 << ui);
            if (overlay >= 0) layers &= ~(1 << overlay);
            camera.cullingMask = layers;
            camera.useOcclusionCulling = false;
            camera.farClipPlane = farClipPlane;
            camera.targetTexture = texture;
            return camera;
        }

        private void RenderEye(Camera view, Camera.MonoOrStereoscopicEye eye, Camera camera, Vector3 viewPosition, Quaternion viewRotation, ref Vector4 eyePosition)
        {
            if (camera == null) return;
            Vector3 origin;
            Matrix4x4 projection;
            if (eye == Camera.MonoOrStereoscopicEye.Mono)
            {
                origin = viewPosition;
                projection = view.projectionMatrix;
            }
            else
            {
                var stereoEye = (Camera.StereoscopicEye)eye;
                // Not GetStereoViewMatrix: on URP it still holds last frame's eyes here (before rendering), so
                // the reflection trailed the head by a frame and drifted in VR.
                EyePose.Get(view, stereoEye, out origin, out viewRotation);
                projection = view.GetStereoProjectionMatrix(stereoEye);
            }
            eyePosition = origin;

            // Reflect the eye across the mirror's plane (its local XY plane).
            transform.GetPositionAndRotation(out Vector3 planePosition, out Quaternion planeRotation);
            Quaternion inverse = Quaternion.Inverse(planeRotation);
            Vector3 localEye = Vector3.Reflect(inverse * (origin - planePosition), Vector3.forward);
            Vector3 localForward = Vector3.Reflect(inverse * (viewRotation * Vector3.forward), Vector3.forward);
            Vector3 localUp = Vector3.Reflect(inverse * (viewRotation * Vector3.up), Vector3.forward);
            camera.transform.SetPositionAndRotation(planePosition + planeRotation * localEye,
                Quaternion.LookRotation(planeRotation * localForward, planeRotation * localUp));

            // Nothing behind the mirror may show: clip the projection at the mirror's plane.
            Vector3 normal = (planeRotation * Vector3.back).normalized;
            Matrix4x4 worldToCamera = camera.worldToCameraMatrix;
            Vector3 planeInCamera = worldToCamera.MultiplyPoint(planePosition + normal * ClipPlaneOffset);
            Vector3 normalInCamera = worldToCamera.MultiplyVector(normal);
            var clip = new Vector4(normalInCamera.x, normalInCamera.y, normalInCamera.z, -Vector3.Dot(planeInCamera, normalInCamera));
            clip.x *= -1f; // the projection is flipped below
            Matrix4x4 plain = projection;
            Oblique(ref projection, clip);

            // Flipped on X to keep triangle winding after the reflection; the shader flips the picture back.
            // Unity draws the sky of an oblique projection with the last NON-oblique projection the camera was given
            // (Built-in and URP alike): the plain one goes first, or the reflected sky keeps a stale framing.
            Matrix4x4 flip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            camera.farClipPlane = farClipPlane;
            camera.projectionMatrix = flip * plain * flip;
            camera.projectionMatrix = flip * projection * flip;
            camera.cullingMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;
            camera.Render();
        }

        private static void Oblique(ref Matrix4x4 projection, Vector4 clipPlane)
        {
            Vector4 corner = projection.inverse * new Vector4(Math.Sign(clipPlane.x), Math.Sign(clipPlane.y), 1f, 1f);
            float dot = Vector4.Dot(clipPlane, corner);
            if (Mathf.Approximately(dot, 0f)) return;
            Vector4 c = clipPlane * (2f / dot);
            projection[2] = c.x - projection[3];
            projection[6] = c.y - projection[7];
            projection[10] = c.z - projection[11];
            projection[14] = c.w - projection[15];
        }

        /// <summary>
        /// The current pose of a headset camera's eye. On a render pipeline the camera's eye matrices are written
        /// while it renders (URP: UniversalRenderPipeline.UpdateCameraStereoMatrices), so before rendering they are
        /// last frame's. This keeps each eye's pose relative to its camera from the last render and puts it on the
        /// camera's current transform.
        /// </summary>
        private static class EyePose
        {
            private struct Eyes { public Vector3 lp, rp; public Quaternion lr, rr; }

            private static readonly Dictionary<int, Eyes> Offsets = new Dictionary<int, Eyes>();
            private static bool _hooked;

            public static void Get(Camera camera, Camera.StereoscopicEye eye, out Vector3 position, out Quaternion rotation)
            {
                if (!_hooked)
                {
                    _hooked = true;
                    RenderPipelineManager.endCameraRendering += OnEndCamera;
                }
                Transform t = camera.transform;
                if (Offsets.TryGetValue(camera.GetInstanceID(), out Eyes eyes))
                {
                    bool left = eye == Camera.StereoscopicEye.Left;
                    position = t.TransformPoint(left ? eyes.lp : eyes.rp);
                    rotation = t.rotation * (left ? eyes.lr : eyes.rr);
                    return;
                }
                // Built-in RP, or nothing rendered yet: the camera's own matrices.
                if (!FromView(camera.GetStereoViewMatrix(eye), out position, out rotation)) rotation = t.rotation;
            }

            // Right after the pipeline drew the camera its eye matrices and its transform belong to the same moment.
            private static void OnEndCamera(ScriptableRenderContext context, Camera camera)
            {
                if (camera == null || !camera.stereoEnabled) return;
                Transform t = camera.transform;
                Quaternion inverse = Quaternion.Inverse(t.rotation);
                if (!FromView(camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left), out Vector3 lp, out Quaternion lr)) return;
                if (!FromView(camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right), out Vector3 rp, out Quaternion rr)) return;
                Offsets[camera.GetInstanceID()] = new Eyes
                {
                    lp = t.InverseTransformPoint(lp),
                    rp = t.InverseTransformPoint(rp),
                    lr = inverse * lr,
                    rr = inverse * rr,
                };
            }

            // A view matrix looks down -Z; its inverse's columns are the eye's right, up and back in world space.
            private static bool FromView(Matrix4x4 view, out Vector3 position, out Quaternion rotation)
            {
                Matrix4x4 eyeToWorld = view.inverse;
                position = eyeToWorld.GetColumn(3);
                Vector3 forward = -(Vector3)eyeToWorld.GetColumn(2);
                Vector3 up = eyeToWorld.GetColumn(1);
                bool valid = forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f;
                rotation = valid ? Quaternion.LookRotation(forward, up) : Quaternion.identity;
                return valid;
            }
        }

#if UNITY_EDITOR
        private const string ShaderPath = "Packages/com.parelvrsdk.pvr/Runtime/WorldSDK/Shaders/ParelVRSDKMirror.shader";

        // The shader is referenced, not looked up by name, so it travels in the world's bundle.
        private void Reset() => shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);

        private void OnValidate()
        {
            if (shader == null) shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        }

        [MenuItem("GameObject/ParelVR/Mirror", false, 10)]
        private static void Create(MenuCommand command)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Mirror";
            DestroyImmediate(go.GetComponent<Collider>());
            go.transform.localScale = new Vector3(2f, 2.5f, 1f);
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            if (command.context == null && SceneView.lastActiveSceneView != null)
                go.transform.position = SceneView.lastActiveSceneView.pivot + Vector3.up * 1.25f;
            go.AddComponent<ParelVRMirror>();
            Undo.RegisterCreatedObjectUndo(go, "Create Mirror");
            Selection.activeObject = go;
        }
#endif
    }
}
