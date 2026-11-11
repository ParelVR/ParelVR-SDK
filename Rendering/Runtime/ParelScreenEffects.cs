using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// A world's screen-space lighting, converted from HDRP's Screen Space Reflection, Screen Space Global Illumination,
    /// Contact Shadows and Micro Shadows overrides. Drawn inside the ParelVR lit shaders (ParelVRLitExtras.cginc) from the
    /// depth texture and ParelVR's opaque picture; PC only (standalone headsets keep probes and baked light).
    /// </summary>
    [Serializable]
    public sealed class ParelScreenEffectsSettings
    {
        public bool Reflections;
        [Range(0f, 1f)] public float ReflectionMinSmoothness = 0.9f;
        [Range(0f, 1f)] public float ReflectionFadeStart = 0.95f;
        public float ReflectionDistance = 40f;
        public bool GlobalIllumination;
        public float GlobalIlluminationIntensity = 1f;
        public float GlobalIlluminationRayLength = 2f;
        public bool ContactShadows;
        public float ContactShadowLength = 0.15f;
        [Range(0f, 1f)] public float ContactShadowOpacity = 1f;
        public float ContactShadowMaxDistance = 50f;
        [Range(0f, 1f)] public float MicroShadowOpacity;

        public bool Any => Reflections || GlobalIllumination || ContactShadows || MicroShadowOpacity > 0f;
    }

    /// <summary>Sets the screen-space lighting globals for a world and keeps ParelVR's opaque picture on while it needs it.</summary>
    public static class ParelScreenEffects
    {
        private static readonly int SsrId = Shader.PropertyToID("_ParelSSR");
        private static readonly int SsgiId = Shader.PropertyToID("_ParelSSGI");
        private static readonly int ContactId = Shader.PropertyToID("_ParelContactShadows");
        private static readonly int MicroId = Shader.PropertyToID("_ParelMicroShadows");
        private static bool _capturing;

        public static void Apply(ParelScreenEffectsSettings s)
        {
            Clear();
            if (s == null || !s.Any) return;
            bool screen = ParelOpaqueCapture.Supported && (s.Reflections || s.GlobalIllumination || s.ContactShadows);
            if (screen)
            {
                ParelOpaqueCapture.Request();
                _capturing = true;
            }
            Shader.SetGlobalVector(SsrId, screen && s.Reflections
                ? new Vector4(1f, Mathf.Max(1f, s.ReflectionDistance), Mathf.Clamp01(s.ReflectionMinSmoothness), Mathf.Max(s.ReflectionFadeStart, s.ReflectionMinSmoothness + 0.01f))
                : Vector4.zero);
            Shader.SetGlobalVector(SsgiId, screen && s.GlobalIllumination ? new Vector4(1f, s.GlobalIlluminationIntensity, Mathf.Max(0.2f, s.GlobalIlluminationRayLength), 0f) : Vector4.zero);
            Shader.SetGlobalVector(ContactId, screen && s.ContactShadows ? new Vector4(1f, Mathf.Max(0.01f, s.ContactShadowLength), Mathf.Clamp01(s.ContactShadowOpacity), Mathf.Max(1f, s.ContactShadowMaxDistance)) : Vector4.zero);
            Shader.SetGlobalVector(MicroId, new Vector4(Mathf.Clamp01(s.MicroShadowOpacity), 0f, 0f, 0f));
        }

        public static void Clear()
        {
            Shader.SetGlobalVector(SsrId, Vector4.zero);
            Shader.SetGlobalVector(SsgiId, Vector4.zero);
            Shader.SetGlobalVector(ContactId, Vector4.zero);
            Shader.SetGlobalVector(MicroId, Vector4.zero);
            if (_capturing) ParelOpaqueCapture.Release();
            _capturing = false;
        }
    }

    /// <summary>
    /// A real-time area light (HDRP rectangle, tube or disc light), drawn by the ParelVR lit shaders: soft highlights the
    /// shape of the light and diffuse light from its whole surface. The 4 nearest the player light the scene. Converted
    /// at upload from HDRP area lights set to real-time; creators can add it too.
    /// </summary>
    [AddComponentMenu("ParelVR/Rendering/Area Light")]
    [ExecuteAlways]
    public sealed class ParelAreaLight : MonoBehaviour
    {
        public enum Shape { Rectangle, Tube, Disc }

        public Shape LightShape = Shape.Rectangle;
        [Tooltip("Width × height (m); a tube uses the width as its length, a disc the width as its diameter.")]
        public Vector2 Size = new Vector2(1f, 1f);
        [ColorUsage(false, true)] public Color Color = Color.white;
        [Tooltip("Brightness in ParelVR light units (as a Built-in light's intensity at 1 m).")]
        public float Intensity = 1f;
        public float Range = 10f;
        [Tooltip("Lights both sides (HDRP: the light faces +Z only otherwise).")]
        public bool TwoSided;

        private const int Max = 4;
        private static readonly List<ParelAreaLight> All = new List<ParelAreaLight>();
        private static readonly List<ParelAreaLight> Sorted = new List<ParelAreaLight>();
        private static readonly Vector4[] Pos = new Vector4[Max], Right = new Vector4[Max], Up = new Vector4[Max], Col = new Vector4[Max];
        private static readonly int CountId = Shader.PropertyToID("_ParelAreaLightCount");
        private static int _frame = -1;

        private void OnEnable() => All.Add(this);

        private void OnDisable()
        {
            All.Remove(this);
            if (All.Count == 0) Shader.SetGlobalVector(CountId, Vector4.zero);
        }

        private void LateUpdate()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            Upload(Camera.main != null ? Camera.main.transform.position : transform.position);
        }

        /// <summary>Picks the area lights nearest <paramref name="focus"/> and sets the shader globals (checks call it).</summary>
        public static void Upload(Vector3 focus)
        {
            Sorted.Clear();
            foreach (ParelAreaLight l in All) if (l != null && l.isActiveAndEnabled && l.Intensity > 0f) Sorted.Add(l);
            Sorted.Sort((a, b) => (Vector3.Distance(a.transform.position, focus) - a.Range).CompareTo(Vector3.Distance(b.transform.position, focus) - b.Range));
            int n = Mathf.Min(Sorted.Count, Max);
            for (int i = 0; i < n; i++)
            {
                ParelAreaLight l = Sorted[i];
                Transform t = l.transform;
                Vector3 s = Vector3.Scale(new Vector3(l.Size.x, l.Size.y, 1f), t.lossyScale);
                float shape = l.LightShape == Shape.Rectangle ? 0f : l.LightShape == Shape.Tube ? 1f : 2f;
                Vector3 p = t.position;
                Vector3 r = t.right * (s.x * 0.5f);
                Vector3 u = t.up * (l.LightShape == Shape.Disc ? s.x * 0.5f : s.y * 0.5f);
                Pos[i] = new Vector4(p.x, p.y, p.z, shape);
                Right[i] = new Vector4(r.x, r.y, r.z, Mathf.Max(0.1f, l.Range));
                Up[i] = new Vector4(u.x, u.y, u.z, l.TwoSided ? 1f : 0f);
                Color c = l.Color.linear * l.Intensity;
                Col[i] = new Vector4(c.r, c.g, c.b, 0f);
            }
            Shader.SetGlobalVectorArray("_ParelAreaLightPos", Pos);
            Shader.SetGlobalVectorArray("_ParelAreaLightRight", Right);
            Shader.SetGlobalVectorArray("_ParelAreaLightUp", Up);
            Shader.SetGlobalVectorArray("_ParelAreaLightColor", Col);
            Shader.SetGlobalVector(CountId, new Vector4(n, 0f, 0f, 0f));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(Size.x, LightShape == Shape.Tube ? 0.02f : Size.y, 0.01f));
        }
    }
}
