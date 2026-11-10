namespace ParelVR.Rendering
{
    /// <summary>
    /// Writes a <see cref="MaterialDescription"/> into a ParelVR shader material. Every ParelVR shader (PC and Quest)
    /// uses the Standard shader's property names plus _Cull/_SrcBlend/_DstBlend/_ZWrite, so one writer serves them all;
    /// properties a given shader lacks are skipped by the sink.
    /// </summary>
    public sealed class ParelMaterialWriter : IMaterialWriter
    {
        public bool CanWrite(ShaderRole role) => role != ShaderRole.Terrain && role != ShaderRole.Skybox;

        public void Write(MaterialDescription d, ShaderRole role, TargetPlatform target, IMaterialPropertySink sink)
        {
            sink.SetColor("_Color", d.BaseColor);
            sink.SetTexture("_MainTex", d.BaseMap ?? new TextureSlot());
            sink.SetFloat("_Cull", (float)d.Cull);
            WriteSurface(d, sink);

            switch (role)
            {
                case ShaderRole.Lit:
                case ShaderRole.Cutout:
                case ShaderRole.Transparent:
                case ShaderRole.ParticlesLit:
                    WriteLit(d, sink);
                    break;
                case ShaderRole.SimpleLit:
                    WriteSimpleLit(d, sink);
                    break;
                case ShaderRole.Toon:
                case ShaderRole.Emissive:
                case ShaderRole.Unlit:
                case ShaderRole.Water:
                    WriteEmission(d, sink);
                    break;
            }
        }

        private static void WriteSurface(MaterialDescription d, IMaterialPropertySink sink)
        {
            SurfaceState state = SurfaceState.For(d.Surface);
            bool clip = state.AlphaTest || d.AlphaClip;
            sink.SetFloat("_Mode", (float)d.Surface);
            sink.SetFloat("_SrcBlend", state.SrcBlend);
            sink.SetFloat("_DstBlend", state.DstBlend);
            sink.SetFloat("_ZWrite", state.ZWrite ? 1f : 0f);
            sink.SetFloat("_Cutoff", d.AlphaCutoff);
            sink.SetKeyword("_ALPHATEST_ON", clip);
            sink.SetKeyword("_ALPHABLEND_ON", state.AlphaBlend);
            sink.SetKeyword("_ALPHAPREMULTIPLY_ON", state.AlphaPremultiply);
            sink.SetOverrideTag("RenderType", clip && state.Queue < Queues.Transparent ? "TransparentCutout" : state.RenderType);
            int queue = clip && state.Queue < Queues.AlphaTest ? Queues.AlphaTest : state.Queue;
            sink.SetRenderQueue(QueueFits(d.RenderQueue, queue) ? d.RenderQueue : queue);
        }

        /// <summary>The source's own queue is kept (it may be tuned for sorting) unless it puts a blended surface among opaque ones or the reverse.</summary>
        private static bool QueueFits(int sourceQueue, int defaultQueue)
        {
            if (sourceQueue < 0) return false;
            bool sourceTransparent = sourceQueue > 2500;
            bool targetTransparent = defaultQueue > 2500;
            return sourceTransparent == targetTransparent;
        }

        private static void WriteLit(MaterialDescription d, IMaterialPropertySink sink)
        {
            bool normal = d.NormalMap != null && d.NormalMap.HasTexture;
            sink.SetKeyword("_NORMALMAP", normal);
            if (normal)
            {
                sink.SetTexture("_BumpMap", d.NormalMap);
                sink.SetFloat("_BumpScale", d.NormalScale);
            }

            // A packed HDRP-style mask map reads the same channels as Standard's metallic map (R metal, A smoothness)
            // and occlusion map (G), so the one texture goes into both slots.
            bool mask = d.MaskMap != null && d.MaskMap.HasTexture;
            bool specular = d.Shading == ShadingModel.Specular && !mask;
            TextureSlot metallicMap = mask ? d.MaskMap : d.MetallicGlossMap;
            bool metalMap = !specular && metallicMap != null && metallicMap.HasTexture;
            bool specMap = specular && d.SpecularGlossMap != null && d.SpecularGlossMap.HasTexture;

            sink.SetKeyword("_SPECULAR_SETUP", specular);
            sink.SetKeyword("_METALLICGLOSSMAP", metalMap);
            sink.SetKeyword("_SPECGLOSSMAP", specMap);

            if (specular)
            {
                sink.SetColor("_SpecColor", d.SpecularColor);
                if (specMap) sink.SetTexture("_SpecGlossMap", d.SpecularGlossMap);
            }
            else if (metalMap)
            {
                sink.SetTexture("_MetallicGlossMap", metallicMap);
            }

            sink.SetFloat("_Metallic", d.Metallic);
            float smoothness = mask ? d.SmoothnessRemapMax : d.Smoothness;
            sink.SetFloat("_Glossiness", smoothness);
            sink.SetFloat("_GlossMapScale", smoothness);
            bool fromAlbedo = d.SmoothnessFrom == SmoothnessSource.AlbedoAlpha;
            sink.SetFloat("_SmoothnessTextureChannel", fromAlbedo ? 1f : 0f);
            sink.SetKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", fromAlbedo);

            TextureSlot occlusion = d.OcclusionMap != null && d.OcclusionMap.HasTexture ? d.OcclusionMap : (mask ? d.MaskMap : null);
            if (occlusion != null)
            {
                sink.SetTexture("_OcclusionMap", occlusion);
                sink.SetFloat("_OcclusionStrength", d.OcclusionStrength);
            }

            WriteEmission(d, sink);

            bool detail = (d.DetailAlbedoMap != null && d.DetailAlbedoMap.HasTexture) || (d.DetailNormalMap != null && d.DetailNormalMap.HasTexture);
            sink.SetKeyword("_DETAIL_MULX2", detail);
            if (detail)
            {
                if (d.DetailMask != null) sink.SetTexture("_DetailMask", d.DetailMask);
                if (d.DetailAlbedoMap != null) sink.SetTexture("_DetailAlbedoMap", d.DetailAlbedoMap);
                if (d.DetailNormalMap != null) sink.SetTexture("_DetailNormalMap", d.DetailNormalMap);
                sink.SetFloat("_DetailNormalMapScale", d.DetailNormalScale);
            }

            bool parallax = d.HeightMap != null && d.HeightMap.HasTexture;
            sink.SetKeyword("_PARALLAXMAP", parallax);
            if (parallax)
            {
                sink.SetTexture("_ParallaxMap", d.HeightMap);
                sink.SetFloat("_Parallax", d.HeightScale);
            }

            sink.SetFloat("_SpecularHighlights", d.SpecularHighlights ? 1f : 0f);
            sink.SetKeyword("_SPECULARHIGHLIGHTS_OFF", !d.SpecularHighlights);
            sink.SetFloat("_GlossyReflections", d.GlossyReflections ? 1f : 0f);
            sink.SetKeyword("_GLOSSYREFLECTIONS_OFF", !d.GlossyReflections);
        }

        private static void WriteSimpleLit(MaterialDescription d, IMaterialPropertySink sink)
        {
            bool normal = d.NormalMap != null && d.NormalMap.HasTexture;
            sink.SetKeyword("_NORMALMAP", normal);
            if (normal)
            {
                sink.SetTexture("_BumpMap", d.NormalMap);
                sink.SetFloat("_BumpScale", d.NormalScale);
            }
            sink.SetColor("_SpecColor", d.SpecularHighlights ? d.SpecularColor : ColorValue.Black);
            sink.SetFloat("_Shininess", d.Shininess);
            sink.SetKeyword("_SPECULARHIGHLIGHTS_OFF", !d.SpecularHighlights);
            sink.SetKeyword("_GLOSS_FROM_ALBEDO_ALPHA", d.SpecularHighlights && d.SmoothnessFrom == SmoothnessSource.AlbedoAlpha);
            WriteEmission(d, sink);
        }

        private static void WriteEmission(MaterialDescription d, IMaterialPropertySink sink)
        {
            sink.SetKeyword("_EMISSION", d.EmissionEnabled);
            if (!d.EmissionEnabled) return;
            sink.SetColor("_EmissionColor", d.EmissionColor);
            if (d.EmissionMap != null) sink.SetTexture("_EmissionMap", d.EmissionMap);
        }
    }
}
