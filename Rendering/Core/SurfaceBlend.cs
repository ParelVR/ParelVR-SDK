namespace ParelVR.Rendering
{
    /// <summary>UnityEngine.Rendering.BlendMode values, so Core can set blend state without engine types.</summary>
    public static class BlendFactor
    {
        public const int Zero = 0;
        public const int One = 1;
        public const int DstColor = 2;
        public const int SrcColor = 3;
        public const int OneMinusDstColor = 4;
        public const int SrcAlpha = 5;
        public const int OneMinusSrcColor = 6;
        public const int DstAlpha = 7;
        public const int OneMinusDstAlpha = 8;
        public const int SrcAlphaSaturate = 9;
        public const int OneMinusSrcAlpha = 10;
    }

    /// <summary>Render queue numbers matching UnityEngine.Rendering.RenderQueue.</summary>
    public static class Queues
    {
        public const int Background = 1000;
        public const int Geometry = 2000;
        public const int AlphaTest = 2450;
        public const int Transparent = 3000;
    }

    /// <summary>The blend state, queue and tags each surface type uses in ParelVR shaders.</summary>
    public struct SurfaceState
    {
        public int SrcBlend, DstBlend;
        public bool ZWrite;
        public int Queue;
        public string RenderType;
        public bool AlphaTest, AlphaPremultiply;
        /// <summary>The shader outputs real alpha (fade, additive, multiply). Off means alpha is forced to 1.</summary>
        public bool AlphaBlend;

        public static SurfaceState For(SurfaceType surface)
        {
            switch (surface)
            {
                case SurfaceType.Cutout:
                    return new SurfaceState { SrcBlend = BlendFactor.One, DstBlend = BlendFactor.Zero, ZWrite = true, Queue = Queues.AlphaTest, RenderType = "TransparentCutout", AlphaTest = true };
                case SurfaceType.Fade:
                    return new SurfaceState { SrcBlend = BlendFactor.SrcAlpha, DstBlend = BlendFactor.OneMinusSrcAlpha, ZWrite = false, Queue = Queues.Transparent, RenderType = "Transparent", AlphaBlend = true };
                case SurfaceType.Transparent:
                    return new SurfaceState { SrcBlend = BlendFactor.One, DstBlend = BlendFactor.OneMinusSrcAlpha, ZWrite = false, Queue = Queues.Transparent, RenderType = "Transparent", AlphaPremultiply = true };
                case SurfaceType.Additive:
                    return new SurfaceState { SrcBlend = BlendFactor.SrcAlpha, DstBlend = BlendFactor.One, ZWrite = false, Queue = Queues.Transparent, RenderType = "Transparent", AlphaBlend = true };
                case SurfaceType.Multiply:
                    return new SurfaceState { SrcBlend = BlendFactor.DstColor, DstBlend = BlendFactor.Zero, ZWrite = false, Queue = Queues.Transparent, RenderType = "Transparent", AlphaBlend = true };
                default:
                    return new SurfaceState { SrcBlend = BlendFactor.One, DstBlend = BlendFactor.Zero, ZWrite = true, Queue = Queues.Geometry, RenderType = "Opaque" };
            }
        }

        /// <summary>Works out the surface type from a blend pair, for shaders that expose _SrcBlend/_DstBlend.</summary>
        public static SurfaceType FromBlend(int src, int dst, bool alphaTest)
        {
            if (src == BlendFactor.One && dst == BlendFactor.Zero) return alphaTest ? SurfaceType.Cutout : SurfaceType.Opaque;
            if (src == BlendFactor.SrcAlpha && dst == BlendFactor.OneMinusSrcAlpha) return SurfaceType.Fade;
            if (src == BlendFactor.One && dst == BlendFactor.OneMinusSrcAlpha) return SurfaceType.Transparent;
            if (dst == BlendFactor.One || (src == BlendFactor.One && dst == BlendFactor.OneMinusSrcColor)) return SurfaceType.Additive;
            if ((src == BlendFactor.DstColor && dst == BlendFactor.Zero) || (src == BlendFactor.Zero && dst == BlendFactor.SrcColor)) return SurfaceType.Multiply;
            return SurfaceType.Fade;
        }
    }

    /// <summary>Picks the final ParelVR role from the classifier's suggestion and what the material actually does.</summary>
    public static class RoleSelector
    {
        public static ShaderRole Select(MaterialDescription d, ShaderRole suggested)
        {
            switch (suggested)
            {
                case ShaderRole.Skybox:
                case ShaderRole.Terrain:
                case ShaderRole.Water:
                case ShaderRole.Toon:
                    return suggested;
                case ShaderRole.ParticlesLit:
                case ShaderRole.ParticlesUnlit:
                    return d.Shading == ShadingModel.Unlit ? ShaderRole.ParticlesUnlit : suggested;
            }

            if (d.Shading == ShadingModel.Unlit)
                return suggested == ShaderRole.Emissive ? ShaderRole.Emissive : ShaderRole.Unlit;
            // Simple Lit handles opaque, cutout, fade, additive and multiply; premultiplied glass needs Transparent.
            if (d.Shading == ShadingModel.Simple)
                return d.Surface == SurfaceType.Transparent ? ShaderRole.Transparent : ShaderRole.SimpleLit;
            // Layered and subsurface materials have their own shaders for opaque and cutout surfaces.
            if (!d.IsTransparent && d.Layers.Count > 1) return ShaderRole.LayeredLit;
            if (!d.IsTransparent && d.Subsurface) return ShaderRole.Subsurface;
            if (d.IsTransparent && d.Subsurface) d.Unmapped("subsurface scattering on a transparent surface (shown as plain transparency)");
            if (d.IsTransparent && d.Layers.Count > 1) d.Unmapped("layers 2+ on a transparent surface (the main layer is shown)");
            if (d.IsTransparent) return ShaderRole.Transparent;
            if (d.Surface == SurfaceType.Cutout || d.AlphaClip) return ShaderRole.Cutout;
            return ShaderRole.Lit;
        }
    }
}
