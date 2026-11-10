using System;
using System.Collections.Generic;

namespace ParelVR.Rendering
{
    /// <summary>What a world or avatar costs on a standalone headset, as counted by the Quest conversion.</summary>
    public sealed class QuestContentStats
    {
        public bool Avatar;
        public long Triangles;
        public int Renderers;
        public int MaterialSlots;
        public int SkinnedMeshes;
        /// <summary>Estimated GPU memory of the textures after Android compression, in bytes.</summary>
        public long TextureBytes;
        public int RealtimeLights;
        /// <summary>Realtime lights marked Important (always per-pixel, ignoring the pixel-light limit).</summary>
        public int ForcedPixelLights;
        public int RealtimeShadowLights;
        public int ParticleSystems;
        public int MaxParticles;
    }

    /// <summary>
    /// Budgets for standalone headsets (Quest 2 is the floor) and what to say when content is over them. Over-budget
    /// content still converts and uploads: the findings go into the creator's report so they know what to cut.
    /// </summary>
    public static class QuestBudgets
    {
        public const long WorldTriangles = 500_000;
        public const long AvatarTriangles = 20_000;
        public const int WorldMaterialSlots = 300;
        public const int AvatarMaterialSlots = 4;
        public const long WorldTextureBytes = 400L * 1024 * 1024;
        public const long AvatarTextureBytes = 40L * 1024 * 1024;
        public const int WorldRealtimeLights = 4;
        /// <summary>Meshes above this many triangles get Mesh LODs in Quest worlds.</summary>
        public const int MeshLodMinTriangles = 2_000;

        /// <summary>Bits per pixel of ASTC 6x6, Unity's automatic Android format, which the Quest conversion uses.</summary>
        public const double AstcBitsPerPixel = 128.0 / 36.0;

        /// <summary>GPU memory of one compressed texture, with a full mip chain (+1/3) when it has mips.</summary>
        public static long TextureBytes(int width, int height, bool mipmaps, int faces = 1)
        {
            double bytes = (double)Math.Max(1, width) * Math.Max(1, height) * AstcBitsPerPixel / 8.0 * Math.Max(1, faces);
            if (mipmaps) bytes *= 4.0 / 3.0;
            return (long)Math.Ceiling(bytes);
        }

        /// <summary>The size a side ends up at: the import cap, never upscaled.</summary>
        public static int CappedSize(int source, int cap) => cap > 0 ? Math.Min(source, cap) : source;

        public static List<string> Assess(QuestContentStats s)
        {
            var findings = new List<string>();
            long tris = s.Avatar ? AvatarTriangles : WorldTriangles;
            if (s.Triangles > tris)
                findings.Add($"{s.Triangles:N0} triangles; a standalone headset handles about {tris:N0} {(s.Avatar ? "per avatar" : "in a world's view")}." +
                             (s.Avatar ? " Use a lower-poly version for Meta / Standalone." : " Mesh LODs were generated; also cull or simplify distant geometry."));
            int slots = s.Avatar ? AvatarMaterialSlots : WorldMaterialSlots;
            if (s.MaterialSlots > slots)
                findings.Add($"{s.MaterialSlots} material slots (draw calls); aim for {slots} or fewer{(s.Avatar ? " (atlas the textures)" : ", e.g. by atlasing and combining static meshes")}.");
            long tex = s.Avatar ? AvatarTextureBytes : WorldTextureBytes;
            if (s.TextureBytes > tex)
                findings.Add($"Textures need about {Mb(s.TextureBytes)} MB of GPU memory after compression; the budget is {Mb(tex)} MB.");
            if (!s.Avatar && s.RealtimeLights > WorldRealtimeLights)
                findings.Add($"{s.RealtimeLights} realtime lights; bake the ones that don't move (each realtime light re-draws what it touches).");
            if (s.ForcedPixelLights > 1)
                findings.Add($"{s.ForcedPixelLights} lights were set to Important; on the headset only the brightest stays per-pixel, the rest light per vertex.");
            return findings;
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0");
    }
}
