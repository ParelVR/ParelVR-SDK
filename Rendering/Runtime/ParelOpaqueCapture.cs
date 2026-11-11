using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace ParelVR.Rendering
{
    /// <summary>
    /// The picture of the opaque world (and sky, fog included) each camera has drawn before its transparent surfaces,
    /// as _ParelOpaqueTexture with mipmaps, plus the camera depth texture. ParelVR water reads it for refraction and
    /// what's under the surface, refractive HDRP materials for what's behind them, and screen-space reflections for
    /// what surfaces reflect. Like HDRP's colour pyramid. Asked for by whatever needs it (counted), PC only;
    /// elsewhere _ParelOpaqueOn is 0 and those shaders use their fallbacks. Mono, multi-pass and single-pass instanced.
    /// </summary>
    public static class ParelOpaqueCapture
    {
        private const CameraEvent Event = CameraEvent.BeforeForwardAlpha;
        private static int _users;
        private static readonly Dictionary<Camera, State> Cameras = new Dictionary<Camera, State>();
        private static readonly int TextureId = Shader.PropertyToID("_ParelOpaqueTexture");
        private static readonly int OnId = Shader.PropertyToID("_ParelOpaqueOn");
        private static readonly int PrevViewProjId = Shader.PropertyToID("_ParelPrevViewProj");

        private sealed class State
        {
            public CommandBuffer Buffer;
            public RenderTexture Texture;
            public bool Array;
            public Matrix4x4[] PrevViewProj = { Matrix4x4.identity, Matrix4x4.identity };
        }

        public static bool Supported => !Application.isMobilePlatform && SystemInfo.graphicsShaderLevel >= 35;

        /// <summary>Starts capturing (one more user). Pair with <see cref="Release"/>.</summary>
        public static void Request()
        {
            _users++;
            if (_users != 1 || !Supported) return;
            Camera.onPreCull -= OnPreCull;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender -= OnPostRender;
            Camera.onPostRender += OnPostRender;
        }

        public static void Release()
        {
            if (_users <= 0) return;
            _users--;
            if (_users > 0) return;
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            foreach (KeyValuePair<Camera, State> pair in Cameras)
            {
                if (pair.Key != null) pair.Key.RemoveCommandBuffer(Event, pair.Value.Buffer);
                pair.Value.Buffer.Release();
                if (pair.Value.Texture != null) { pair.Value.Texture.Release(); Kill(pair.Value.Texture); }
            }
            Cameras.Clear();
            Shader.SetGlobalVector(OnId, Vector4.zero);
        }

        public static bool Active => _users > 0 && Supported;

        private static void Kill(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>Keeps the capture after any other pass at the same point (the volumetric fog), so it sees the fogged world.</summary>
        internal static void MoveToEnd(Camera cam)
        {
            if (cam == null || !Cameras.TryGetValue(cam, out State s)) return;
            cam.RemoveCommandBuffer(Event, s.Buffer);
            cam.AddCommandBuffer(Event, s.Buffer);
        }

        private static bool Eligible(Camera cam) =>
            cam != null && cam.cameraType == CameraType.Game && (cam.clearFlags == CameraClearFlags.Skybox || cam.clearFlags == CameraClearFlags.SolidColor)
            && (cam.hideFlags & HideFlags.HideAndDontSave) != HideFlags.HideAndDontSave;

        private static void OnPreCull(Camera cam)
        {
            bool on = Eligible(cam);
            Shader.SetGlobalVector(OnId, new Vector4(on ? 1f : 0f, 0f, 0f, 0f));
            if (!on) return;
            cam.depthTextureMode |= DepthTextureMode.Depth;
            bool array = cam.stereoEnabled && (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced
                                               || XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview);
            int w = cam.stereoEnabled ? XRSettings.eyeTextureWidth : cam.pixelWidth;
            int h = cam.stereoEnabled ? XRSettings.eyeTextureHeight : cam.pixelHeight;
            if (!Cameras.TryGetValue(cam, out State s))
            {
                s = new State { Buffer = new CommandBuffer { name = "ParelVR opaque capture" } };
                Cameras[cam] = s;
                cam.AddCommandBuffer(Event, s.Buffer);
            }
            if (s.Texture == null || s.Texture.width != w || s.Texture.height != h || s.Array != array)
            {
                if (s.Texture != null) { s.Texture.Release(); Kill(s.Texture); }
                var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBHalf, 0)
                {
                    dimension = array ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                    volumeDepth = array ? 2 : 1,
                    useMipMap = true,
                    autoGenerateMips = false,
                    msaaSamples = 1,
                };
                s.Texture = new RenderTexture(desc) { name = "ParelVR opaque image", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                s.Texture.Create();
                s.Array = array;
                CommandBuffer cmd = s.Buffer;
                cmd.Clear();
                if (array)
                {
                    cmd.Blit(BuiltinRenderTextureType.CameraTarget, s.Texture, 0, 0);
                    cmd.Blit(BuiltinRenderTextureType.CameraTarget, s.Texture, 1, 1);
                }
                else cmd.Blit(BuiltinRenderTextureType.CameraTarget, s.Texture);
                cmd.GenerateMips(s.Texture);
                cmd.SetGlobalTexture(TextureId, s.Texture);
            }
            Shader.SetGlobalMatrixArray(PrevViewProjId, s.PrevViewProj);
        }

        // Remembers this frame's view-projection: screen-space reflections read last frame's picture where it was.
        private static void OnPostRender(Camera cam)
        {
            if (!Cameras.TryGetValue(cam, out State s)) return;
            if (cam.stereoEnabled)
            {
                s.PrevViewProj[0] = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), true) * cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                s.PrevViewProj[1] = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), true) * cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
            }
            else s.PrevViewProj[0] = s.PrevViewProj[1] = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
        }
    }
}
