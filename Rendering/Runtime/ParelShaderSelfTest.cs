using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Proves in a real player build that the ParelVR shaders and the keyword variants runtime conversion needs were
    /// kept, and that a broken material gets a working stand-in. Launch the build with <c>-pvrp-selftest</c>; results go
    /// to Player.log as <c>[PVRP] SELFTEST</c> lines. Add <c>-pvrp-selftest-quit</c> to exit afterwards (exit code 1 on a
    /// failure). Without the flag nothing runs.
    /// Variant checks draw a 4×4 picture with each material and read it back: a variant missing from the build falls back
    /// to a neighbouring one, which draws the wrong colour.
    /// </summary>
    public static class ParelShaderSelfTest
    {
        public const string Flag = "-pvrp-selftest";
        public const string QuitFlag = "-pvrp-selftest-quit";

        public sealed class Result
        {
            public string Name;
            public bool Passed;
            public string Detail;
            public override string ToString() => (Passed ? "PASS " : "FAIL ") + Name + (string.IsNullOrEmpty(Detail) ? "" : " — " + Detail);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRun()
        {
            if (!HasArgument(Flag)) return;
            List<Result> results = Run(ParelRenderManager.CurrentTarget);
            int failed = 0;
            foreach (Result r in results)
            {
                if (!r.Passed) failed++;
                Debug.Log(RenderingLog.Prefix + "SELFTEST " + r);
            }
            string summary = RenderingLog.Prefix + "SELFTEST " + (results.Count - failed) + " passed, " + failed + " failed (" + Application.platform + ", " + SystemInfo.graphicsDeviceType + ")";
            if (failed > 0) Debug.LogError(summary);
            else Debug.Log(summary);
            if (HasArgument(QuitFlag)) Application.Quit(failed > 0 ? 1 : 0);
        }

        private static bool HasArgument(string flag)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static List<Result> Run(TargetPlatform target)
        {
            ParelRenderManager.Initialize();
            var results = new List<Result>();
            ParelShaderLibrary library = ParelShaderLibrary.Active;
            Add(results, "Shader library is in the build", library != null, library != null ? library.Entries.Count + " roles" : "Resources/" + ParelShaderLibrary.ResourcePath + " missing");
            if (library == null) return results;

            foreach (ParelShaderLibrary.Entry e in library.Entries)
            {
                if (e == null) continue;
                Shader shader = library.Get(e.Role, target);
                bool ok = shader != null && shader.isSupported && shader.name != ShaderClassifier.ErrorShaderName;
                Add(results, "Shader for " + e.Role + " is present and supported", ok, shader != null ? shader.name : "missing");
            }

            ShaderVariantCollection svc = library.Variants;
            Add(results, "Variant collection is in the build", svc != null && svc.variantCount > 0, svc != null ? svc.variantCount + " variants" : "missing");
            // Every ParelVR shader has runtime keywords, so each must be in the collection. (A builder bug once kept only
            // the unlit ones: lit passes need their light keywords in each entry.)
            var parelShaders = new HashSet<Shader>();
            foreach (ParelShaderLibrary.Entry e in library.Entries)
            {
                if (e == null) continue;
                if (e.Pc != null && e.Pc.name.StartsWith("ParelVR/", StringComparison.Ordinal)) parelShaders.Add(e.Pc);
                if (e.Quest != null && e.Quest.name.StartsWith("ParelVR/", StringComparison.Ordinal)) parelShaders.Add(e.Quest);
            }
            int svcShaders = svc != null ? svc.shaderCount : 0;
            Add(results, "Variant collection covers every ParelVR shader", svcShaders >= parelShaders.Count,
                svcShaders + " of " + parelShaders.Count + " shaders");

            Color red = new Color(1f, 0f, 0f, 1f), green = new Color(0f, 1f, 0f, 1f), blue = new Color(0f, 0f, 1f, 1f), black = new Color(0f, 0f, 0f, 1f);
            Check(results, library, target, ShaderRole.Unlit, "Unlit draws its colour", m => m.SetColor("_Color", red), null, c => c.r > 0.9f && c.g < 0.1f);
            Check(results, library, target, ShaderRole.Unlit, "Unlit cutout variant (_ALPHATEST_ON) clips", m => { m.SetColor("_Color", new Color(1f, 0f, 0f, 0f)); m.SetFloat("_Cutoff", 0.5f); }, new[] { "_ALPHATEST_ON" }, c => c.b > 0.9f && c.r < 0.1f);
            foreach (ShaderRole role in new[] { ShaderRole.Lit, ShaderRole.Transparent, ShaderRole.SimpleLit, ShaderRole.Unlit })
            {
                string[] keywords = role == ShaderRole.Transparent ? new[] { "_EMISSION", "_ALPHAPREMULTIPLY_ON" } : new[] { "_EMISSION" };
                Check(results, library, target, role, role + " emission variant (_EMISSION) glows", m =>
                {
                    m.SetColor("_Color", black);
                    m.SetColor("_EmissionColor", green);
                    if (role == ShaderRole.Transparent) { m.SetFloat("_SrcBlend", 1f); m.SetFloat("_DstBlend", 0f); }
                }, keywords, c => c.g > 0.5f && c.g > c.r + 0.3f);
            }
            Check(results, library, target, ShaderRole.Lit, "Lit without emission stays dark (control)", m => { m.SetColor("_Color", black); m.SetColor("_EmissionColor", green); }, null, c => c.g < 0.4f);
            Check(results, library, target, ShaderRole.Cutout, "Cutout clips transparent pixels", m => { m.SetColor("_Color", new Color(1f, 0f, 0f, 0f)); m.SetFloat("_Cutoff", 0.5f); }, null, c => c.b > 0.9f && c.r < 0.1f);

            results.Add(CheckFallback(target));
            return results;
        }

        private static void Add(List<Result> results, string name, bool passed, string detail) =>
            results.Add(new Result { Name = name, Passed = passed, Detail = detail });

        private static void Check(List<Result> results, ParelShaderLibrary library, TargetPlatform target, ShaderRole role, string name,
            Action<Material> setup, string[] keywords, Func<Color, bool> expect)
        {
            Shader shader = library.Get(role, target);
            if (shader == null || !shader.isSupported)
            {
                Add(results, name, false, "no usable shader for " + role);
                return;
            }
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                setup(material);
                if (keywords != null) foreach (string k in keywords) material.EnableKeyword(k);
                Color c = Draw(material, new Color(0f, 0f, 1f, 1f));
                Add(results, name, expect(c), shader.name + " drew " + c);
            }
            catch (Exception ex)
            {
                Add(results, name, false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        /// <summary>Draws pass 0 of <paramref name="material"/> over a cleared 4×4 linear target and returns the centre pixel.</summary>
        public static Color Draw(Material material, Color clear)
        {
            RenderTexture rt = RenderTexture.GetTemporary(4, 4, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            var read = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, clear);
                // Blit puts its source in _MainTex: white keeps the material's own colour and alpha.
                Graphics.Blit(Texture2D.whiteTexture, rt, material, 0);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, 4, 4), 0, 0, false);
                return read.GetPixel(2, 2);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(read);
            }
        }

        private static Result CheckFallback(TargetPlatform target)
        {
            const string name = "A material on a missing shader gets a ParelVR stand-in";
            Shader error = Shader.Find(ShaderClassifier.ErrorShaderName);
            if (error == null) return new Result { Name = name, Passed = false, Detail = "Unity's error shader wasn't found to test with" };
            var go = new GameObject("PVRP self-test") { hideFlags = HideFlags.HideAndDontSave };
            var broken = new Material(error) { name = "PVRP self-test (broken)", hideFlags = HideFlags.HideAndDontSave };
            var validator = new ParelRenderValidator(target, RepairPolicy.BrokenOnly);
            try
            {
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = broken;
                validator.Validate(go, "self-test");
                Material now = renderer.sharedMaterial;
                bool ok = now != null && now != broken && now.shader != null && now.shader.isSupported && now.shader.name.StartsWith("ParelVR/", StringComparison.Ordinal);
                return new Result { Name = name, Passed = ok, Detail = now != null && now.shader != null ? now.shader.name : "no material" };
            }
            finally
            {
                validator.Release();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(broken);
            }
        }
    }
}
