using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Screen-space volumetric fog for PC (Hidden/ParelVR/VolumetricFog): after the opaque world and the sky are drawn,
    /// each camera marches its view rays through the world's fog at half resolution: light shafts through the sun's
    /// shadows (ParelVR renders its own sun shadow map around the player), point and spot lights glowing in the fog,
    /// local fog boxes, and the far fog worked out in one go. Everything opaque is covered, avatars included;
    /// transparent surfaces drawn afterwards fog themselves. Works in mono, multi-pass and single-pass instanced VR.
    /// Standalone headsets (and any camera this can't run on) keep the in-shader fog.
    /// </summary>
    public static class ParelVolumetricFog
    {
        public const int MaxLights = 8;
        private const CameraEvent Event = CameraEvent.BeforeForwardAlpha; // after the opaque world and the sky, before transparent surfaces

        private static ParelFogSettings _settings;
        private static Func<Light> _sun;
        private static Runner _runner;
        private static Material _monoMaterial, _arrayMaterial;
        private static Shader _shadowShader;
        private static Camera _shadowCamera;
        private static RenderTexture _shadowMap;
        private static bool _shadowValid;
        private static readonly Dictionary<Camera, CameraState> Cameras = new Dictionary<Camera, CameraState>();

        private static readonly int ModeId = Shader.PropertyToID("_ParelFogMode");
        private static readonly int ParamsId = Shader.PropertyToID("_ParelFogParams");
        private static readonly int VolParamsId = Shader.PropertyToID("_ParelVolParams");
        private static readonly int InvViewProjId = Shader.PropertyToID("_ParelInvViewProj");
        private static readonly int EyePosId = Shader.PropertyToID("_ParelEyePos");
        private static readonly int EyeId = Shader.PropertyToID("_ParelEye");
        private static readonly int MarchId = Shader.PropertyToID("_ParelFogMarch");
        private static readonly int MarchTexelId = Shader.PropertyToID("_ParelFogMarch_TexelSize");
        private static readonly int ShadowMapId = Shader.PropertyToID("_ParelSunShadowMap");
        private static readonly int ShadowCenterId = Shader.PropertyToID("_ParelSunShadowCenter");
        private static readonly int ShadowRightId = Shader.PropertyToID("_ParelSunShadowRight");
        private static readonly int ShadowUpId = Shader.PropertyToID("_ParelSunShadowUp");
        private static readonly int ShadowForwardId = Shader.PropertyToID("_ParelSunShadowForward");
        private static readonly int ShadowOriginId = Shader.PropertyToID("_ParelSunShadowOrigin");
        private static readonly int LightCountId = Shader.PropertyToID("_ParelFogLightCount");
        private static readonly int LightPosId = Shader.PropertyToID("_ParelFogLightPos");
        private static readonly int LightColorId = Shader.PropertyToID("_ParelFogLightColor");
        private static readonly int LightDirId = Shader.PropertyToID("_ParelFogLightDir");
        private static readonly int LightConeId = Shader.PropertyToID("_ParelFogLightCone");
        private static readonly int[] MarchRt = { Shader.PropertyToID("_ParelFogMarchL"), Shader.PropertyToID("_ParelFogMarchR") };

        private static readonly Matrix4x4[] InvViewProj = new Matrix4x4[2];
        private static readonly Vector4[] EyePos = new Vector4[2];
        private static readonly Vector4[] LightPos = new Vector4[MaxLights];
        private static readonly Vector4[] LightColor = new Vector4[MaxLights];
        private static readonly Vector4[] LightDir = new Vector4[MaxLights];
        private static readonly Vector4[] LightCone = new Vector4[MaxLights];

        /// <summary>Cameras the pass is attached to (checks).</summary>
        public static int CameraCount => Cameras.Count;

        /// <summary>True while a world's volumetric fog is on.</summary>
        public static bool Active => _settings != null;

        /// <summary>Whether this device draws the screen-space pass (PC). Elsewhere the in-shader fog is used.</summary>
        public static bool Supported => !Application.isMobilePlatform && SystemInfo.graphicsShaderLevel >= 35 && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);

        /// <summary>Editor checks: the preview scene the sun shadow camera renders (default: the loaded scenes).</summary>
        public static UnityEngine.SceneManagement.Scene? ShadowScene;

        /// <summary>Quality: march steps (8..64). Adaptive Quality may lower it.</summary>
        public static int Steps = 32;

        /// <summary>Turns the screen-space fog on for a world. <paramref name="sun"/> is asked each frame (may return null).</summary>
        public static void Enable(ParelFogSettings settings, Func<Light> sun)
        {
            if (settings == null || !settings.Enabled) { Disable(); return; }
            _settings = settings;
            _sun = sun;
            if (!Supported) return;
            if (_monoMaterial == null)
            {
                Shader s = Resources.Load<Shader>("ParelVR/Rendering/ParelVolumetricFog") ?? Shader.Find("Hidden/ParelVR/VolumetricFog");
                if (s == null || !s.isSupported)
                {
                    RenderingLog.Warning("Volumetric fog shader missing or unsupported here; the in-shader fog is used.");
                    return;
                }
                _monoMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
                _arrayMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
                _arrayMaterial.EnableKeyword("PARELVR_STEREO_ARRAY");
            }
            if (_runner == null)
            {
                var go = new GameObject("[ParelVR Volumetric Fog]") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<Runner>();
            }
            Camera.onPreCull -= OnPreCull;
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPreRender += OnPreRender;
        }

        /// <summary>Turns it off (world closed, or a world without fog).</summary>
        public static void Disable()
        {
            _settings = null;
            _sun = null;
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            foreach (KeyValuePair<Camera, CameraState> pair in Cameras)
                if (pair.Key != null) pair.Key.RemoveCommandBuffer(Event, pair.Value.Buffer);
            foreach (CameraState s in Cameras.Values) s.Buffer.Release();
            Cameras.Clear();
            Shader.SetGlobalVector(LightCountId, Vector4.zero);
            if (_runner != null) Kill(_runner.gameObject);
            _runner = null;
            if (_shadowCamera != null) Kill(_shadowCamera.gameObject);
            _shadowCamera = null;
            if (_shadowMap != null) { _shadowMap.Release(); Kill(_shadowMap); }
            _shadowMap = null;
            _shadowValid = false;
        }

        private static void Kill(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        private sealed class CameraState
        {
            public CommandBuffer Buffer;
            public int Width, Height;
            public bool Array;
            public int Eyes;
        }

        private static bool Eligible(Camera cam)
        {
            if (cam == null || cam == _shadowCamera || cam.cameraType != CameraType.Game) return false;
            // Cameras that only draw UI or overlays on top of another camera clear depth only: not the world view.
            return cam.clearFlags == CameraClearFlags.Skybox || cam.clearFlags == CameraClearFlags.SolidColor;
        }

        private static bool UsesArray(Camera cam)
        {
            if (!cam.stereoEnabled) return false;
            return XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced
                || XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview;
        }

        private static void OnPreCull(Camera cam)
        {
            if (_settings == null) return;
            bool screen = _monoMaterial != null && Eligible(cam);
            // Shaders from before SDK 1.16 read _ParelFogParams.x; newer ones _ParelFogMode (2: the pass fogs opaque).
            Vector4 p = Shader.GetGlobalVector(ParamsId);
            p.x = screen ? 0f : 1f;
            Shader.SetGlobalVector(ParamsId, p);
            Shader.SetGlobalVector(ModeId, new Vector4(screen ? 2f : 1f, 0f, 0f, 0f));
            if (!screen)
            {
                if (cam != null && Cameras.TryGetValue(cam, out CameraState old))
                {
                    cam.RemoveCommandBuffer(Event, old.Buffer);
                    old.Buffer.Release();
                    Cameras.Remove(cam);
                }
                return;
            }

            cam.depthTextureMode |= DepthTextureMode.Depth;
            bool array = UsesArray(cam);
            int eyes = array ? 2 : 1;
            int w = cam.stereoEnabled ? XRSettings.eyeTextureWidth : cam.pixelWidth;
            int h = cam.stereoEnabled ? XRSettings.eyeTextureHeight : cam.pixelHeight;
            w = Mathf.Max(1, w / 2);
            h = Mathf.Max(1, h / 2);
            if (!Cameras.TryGetValue(cam, out CameraState state))
            {
                state = new CameraState { Buffer = new CommandBuffer { name = "ParelVR volumetric fog" } };
                Cameras[cam] = state;
                cam.AddCommandBuffer(Event, state.Buffer);
                ParelOpaqueCapture.MoveToEnd(cam);
            }
            if (state.Width != w || state.Height != h || state.Array != array || state.Eyes != eyes)
            {
                state.Width = w; state.Height = h; state.Array = array; state.Eyes = eyes;
                Build(state);
            }
            Shader.SetGlobalVector(VolParamsId, new Vector4(Mathf.Max(1f, _settings.VolumetricRange), Mathf.Clamp(Steps, 8, 64), Time.frameCount % 64, _shadowValid ? 1f : 0f));
        }

        private static void Build(CameraState s)
        {
            CommandBuffer cmd = s.Buffer;
            cmd.Clear();
            if (s.Array) cmd.SetSinglePassStereo(SinglePassStereoMode.None);
            Material m = s.Array ? _arrayMaterial : _monoMaterial;
            for (int eye = 0; eye < s.Eyes; eye++)
            {
                int rt = MarchRt[eye];
                cmd.GetTemporaryRT(rt, s.Width, s.Height, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                cmd.SetGlobalFloat(EyeId, eye);
                cmd.SetRenderTarget(rt);
                cmd.DrawProcedural(Matrix4x4.identity, m, 0, MeshTopology.Triangles, 3, 1);
                cmd.SetGlobalTexture(MarchId, rt);
                cmd.SetGlobalVector(MarchTexelId, new Vector4(1f / s.Width, 1f / s.Height, s.Width, s.Height));
                if (s.Array) cmd.SetRenderTarget(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget, 0, CubemapFace.Unknown, eye));
                else cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cmd.DrawProcedural(Matrix4x4.identity, m, 1, MeshTopology.Triangles, 3, 1);
                cmd.ReleaseTemporaryRT(rt);
            }
            if (s.Array) cmd.SetSinglePassStereo(SinglePassStereoMode.Instancing);
        }

        // Per eye (multi-pass renders each eye on its own, single-pass instanced both at once): the view rays.
        private static void OnPreRender(Camera cam)
        {
            if (_settings == null || !Cameras.ContainsKey(cam)) return;
            if (cam.stereoEnabled && UsesArray(cam))
            {
                Set(0, cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left), cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left));
                Set(1, cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right), cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right));
            }
            else if (cam.stereoEnabled && cam.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono)
            {
                var eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                Set(0, cam.GetStereoViewMatrix(eye), cam.GetStereoProjectionMatrix(eye));
            }
            else Set(0, cam.worldToCameraMatrix, cam.projectionMatrix);
            Shader.SetGlobalMatrixArray(InvViewProjId, InvViewProj);
            Shader.SetGlobalVectorArray(EyePosId, EyePos);
        }

        private static void Set(int i, Matrix4x4 view, Matrix4x4 proj)
        {
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(proj, false);
            InvViewProj[i] = (gpu * view).inverse;
            Vector3 p = view.inverse.GetColumn(3);
            EyePos[i] = new Vector4(p.x, p.y, p.z, 1f);
        }

        // ---- per frame: sun shadow map and the lamps lighting the fog ------------------------------------------------

        private static readonly List<Light> AllLights = new List<Light>();
        private static readonly List<Light> Picked = new List<Light>();
        private static float _nextLightSearch;

        private static Vector3 Focus()
        {
            Camera main = Camera.main;
            if (main != null) return main.transform.position;
            foreach (Camera c in Cameras.Keys) if (c != null && c.isActiveAndEnabled) return c.transform.position;
            return Vector3.zero;
        }

        /// <summary>Renders the sun shadow map and picks the lamps now (normally once per frame, after Update).</summary>
        public static void UpdateNow() => Tick();

        private static void Tick()
        {
            if (_settings == null || _monoMaterial == null) return;
            Vector3 focus = Focus();
            RenderSunShadow(focus);
            UpdateLights(focus);
        }

        private static void RenderSunShadow(Vector3 focus)
        {
            Light sun = _sun?.Invoke();
            _shadowValid = false;
            if (sun == null || !sun.isActiveAndEnabled || sun.shadows == LightShadows.None || _settings.SunScattering <= 0f || _settings.VolumetricRange < 1f) return;
            if (_shadowShader == null) _shadowShader = Resources.Load<Shader>("ParelVR/Rendering/ParelSunShadowDepth") ?? Shader.Find("Hidden/ParelVR/SunShadowDepth");
            if (_shadowShader == null || !_shadowShader.isSupported) return;
            if (_shadowMap == null)
            {
                _shadowMap = new RenderTexture(2048, 2048, 16, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                    { name = "ParelVR sun shadow (fog)", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                _shadowMap.Create();
            }
            if (_shadowCamera == null)
            {
                var go = new GameObject("[ParelVR Sun Shadow Camera]") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
                _shadowCamera = go.AddComponent<Camera>();
                _shadowCamera.enabled = false;
                _shadowCamera.orthographic = true;
                _shadowCamera.clearFlags = CameraClearFlags.SolidColor;
                _shadowCamera.backgroundColor = Color.white;
                _shadowCamera.allowHDR = false;
                _shadowCamera.allowMSAA = false;
                _shadowCamera.useOcclusionCulling = false;
                _shadowCamera.renderingPath = RenderingPath.Forward;
                _shadowCamera.depthTextureMode = DepthTextureMode.None;
                _shadowCamera.aspect = 1f;
                _shadowCamera.targetTexture = _shadowMap;
            }

            float half = Mathf.Clamp(_settings.VolumetricRange * 0.6f, 30f, 100f);
            const float far = 500f;
            Transform st = _shadowCamera.transform;
            st.rotation = sun.transform.rotation;
            Vector3 right = st.right, up = st.up, fwd = st.forward;
            // Snap to whole shadow texels so the shafts don't shimmer while walking.
            float texel = 2f * half / _shadowMap.width;
            float r = Mathf.Round(Vector3.Dot(focus, right) / texel) * texel;
            float u = Mathf.Round(Vector3.Dot(focus, up) / texel) * texel;
            float f = Vector3.Dot(focus, fwd);
            Vector3 centre = right * r + up * u + fwd * f;
            Vector3 origin = centre - fwd * (far * 0.5f);
            st.position = origin;
            _shadowCamera.orthographicSize = half;
            _shadowCamera.nearClipPlane = 0.1f;
            _shadowCamera.farClipPlane = far;
            _shadowCamera.cullingMask = sun.cullingMask & ~(1 << 5); // not the UI layer
            if (ShadowScene.HasValue && ShadowScene.Value.IsValid()) _shadowCamera.scene = ShadowScene.Value;
            _shadowCamera.RenderWithShader(_shadowShader, "RenderType");

            Shader.SetGlobalTexture(ShadowMapId, _shadowMap);
            Shader.SetGlobalVector(ShadowCenterId, new Vector4(centre.x, centre.y, centre.z, 1f / (2f * half)));
            Shader.SetGlobalVector(ShadowRightId, right);
            Shader.SetGlobalVector(ShadowUpId, up);
            Shader.SetGlobalVector(ShadowForwardId, new Vector4(fwd.x, fwd.y, fwd.z, 1f / far));
            Shader.SetGlobalVector(ShadowOriginId, new Vector4(origin.x, origin.y, origin.z, 0.6f / far));
            _shadowValid = true;
        }

        private static void UpdateLights(Vector3 focus)
        {
            if (Time.unscaledTime >= _nextLightSearch)
            {
                _nextLightSearch = Time.unscaledTime + 1f;
                AllLights.Clear();
                AllLights.AddRange(UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None));
                if (ShadowScene.HasValue && ShadowScene.Value.IsValid())
                    foreach (GameObject root in ShadowScene.Value.GetRootGameObjects()) AllLights.AddRange(root.GetComponentsInChildren<Light>());
            }
            Picked.Clear();
            foreach (Light l in AllLights)
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f || l.range <= 0f) continue;
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                Picked.Add(l);
            }
            // The lamps whose light reaches nearest to the player.
            Picked.Sort((a, b) => (Vector3.Distance(a.transform.position, focus) - a.range).CompareTo(Vector3.Distance(b.transform.position, focus) - b.range));
            int n = Mathf.Min(Picked.Count, MaxLights);
            for (int i = 0; i < n; i++)
            {
                Light l = Picked[i];
                Vector3 p = l.transform.position;
                LightPos[i] = new Vector4(p.x, p.y, p.z, 1f / (l.range * l.range));
                // The same scale as the sun: HDRP's phase function carries 1/4π, Built-in light values already 1/π.
                Color c = l.color.linear * l.intensity * _settings.SunScattering * 0.25f;
                LightColor[i] = new Vector4(c.r, c.g, c.b, l.type == LightType.Spot ? 1f : 0f);
                Vector3 d = l.transform.forward;
                float cosOuter = Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad);
                float cosInner = Mathf.Cos(Mathf.Max(l.innerSpotAngle, 0.01f) * 0.5f * Mathf.Deg2Rad);
                LightDir[i] = new Vector4(d.x, d.y, d.z, cosOuter);
                LightCone[i] = new Vector4(1f / Mathf.Max(cosInner - cosOuter, 1e-3f), 0f, 0f, 0f);
            }
            Shader.SetGlobalVectorArray(LightPosId, LightPos);
            Shader.SetGlobalVectorArray(LightColorId, LightColor);
            Shader.SetGlobalVectorArray(LightDirId, LightDir);
            Shader.SetGlobalVectorArray(LightConeId, LightCone);
            Shader.SetGlobalVector(LightCountId, new Vector4(n, 0f, 0f, 0f));
        }

        private sealed class Runner : MonoBehaviour
        {
            private void LateUpdate() => Tick();
        }
    }
}
