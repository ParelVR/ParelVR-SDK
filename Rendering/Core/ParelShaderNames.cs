namespace ParelVR.Rendering
{
    /// <summary>
    /// The names of the ParelVR shader library. Code finds the shaders through <c>ParelShaderLibrary</c> (explicit
    /// references, so builds keep them); these names are for matching, reports, and the last-resort lookup.
    /// </summary>
    public static class ParelShaderNames
    {
        public const string Lit = "ParelVR/Lit";
        public const string SimpleLit = "ParelVR/Simple Lit";
        public const string Unlit = "ParelVR/Unlit";
        public const string Transparent = "ParelVR/Transparent";
        public const string Cutout = "ParelVR/Cutout";
        public const string ParticlesLit = "ParelVR/Particles/Lit";
        public const string ParticlesUnlit = "ParelVR/Particles/Unlit";
        public const string Terrain = "ParelVR/Terrain";
        public const string Skybox = "ParelVR/Skybox";
        /// <summary>HDRP's physically based / gradient / HDRI skies with their cloud layer and volumetric clouds.</summary>
        public const string Sky = "ParelVR/Sky";
        /// <summary>HDRP Water Surface, and the stencil mask of its Water Excluders.</summary>
        public const string Water = "ParelVR/Water";
        public const string WaterExcluder = "ParelVR/Water Excluder";
        public const string Emissive = "ParelVR/Emissive";
        public const string Toon = "ParelVR/Toon";
        public const string Subsurface = "ParelVR/Subsurface";
        public const string LayeredLit = "ParelVR/Layered Lit";

        public static string For(ShaderRole role)
        {
            switch (role)
            {
                case ShaderRole.Lit: return Lit;
                case ShaderRole.SimpleLit: return SimpleLit;
                case ShaderRole.Unlit: return Unlit;
                case ShaderRole.Transparent: return Transparent;
                case ShaderRole.Cutout: return Cutout;
                case ShaderRole.ParticlesLit: return ParticlesLit;
                case ShaderRole.ParticlesUnlit: return ParticlesUnlit;
                case ShaderRole.Terrain: return Terrain;
                case ShaderRole.Skybox: return Skybox;
                case ShaderRole.Emissive: return Emissive;
                case ShaderRole.Water: return Water;
                case ShaderRole.Toon: return Toon;
                case ShaderRole.Subsurface: return Subsurface;
                case ShaderRole.LayeredLit: return LayeredLit;
                default: return Lit;
            }
        }

        /// <summary>The role a fallback falls back to when its own shader isn't available.</summary>
        public static ShaderRole? Parent(ShaderRole role)
        {
            switch (role)
            {
                case ShaderRole.SimpleLit:
                case ShaderRole.Cutout:
                case ShaderRole.Terrain:
                case ShaderRole.Emissive:
                case ShaderRole.Toon:
                case ShaderRole.Subsurface:
                case ShaderRole.LayeredLit:
                    return ShaderRole.Lit;
                case ShaderRole.ParticlesLit:
                    return ShaderRole.ParticlesUnlit;
                case ShaderRole.ParticlesUnlit:
                case ShaderRole.Water:
                    return ShaderRole.Transparent;
                case ShaderRole.Transparent:
                case ShaderRole.Skybox:
                case ShaderRole.Lit:
                    return ShaderRole.Unlit;
                default:
                    return null;
            }
        }
    }
}
