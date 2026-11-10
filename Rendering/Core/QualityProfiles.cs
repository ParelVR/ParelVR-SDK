using System;
using System.Collections.Generic;

namespace ParelVR.Rendering
{
    /// <summary>
    /// One rung of ParelVR Adaptive Rendering (PVAR). Values are relative to the project's own top quality (its "Ultra"
    /// setup), so a profile never asks for more than the game is set up for. GPU-side and CPU-side settings are kept
    /// apart because the controller lowers only the side that is actually slow.
    /// </summary>
    public sealed class QualityProfile
    {
        public string Name = string.Empty;
        public TargetPlatform Platform;

        // ---- GPU-side: pixels and per-pixel cost --------------------------------------------------------------------
        /// <summary>Eye-buffer / render resolution scale (1 = native).</summary>
        public float RenderScale = 1f;
        /// <summary>0 low … 3 very high, capped by the project's own setting.</summary>
        public int ShadowResolution = 3;
        /// <summary>MSAA samples (0, 2, 4, 8), capped by the user's choice.</summary>
        public int Msaa = 4;
        /// <summary>Lowest fixed foveation used (0 off … 1 high); a user setting above it wins.</summary>
        public float MinFoveation;
        /// <summary>Texture mip levels skipped (0 = full resolution).</summary>
        public int TextureMipLimit;
        public bool RealtimeReflectionProbes = true;
        public bool SoftParticles = true;
        /// <summary>Full-screen effects (post-processing) allowed.</summary>
        public bool PostProcessing = true;

        // ---- CPU-side: draw calls, shadow casters, simulation ------------------------------------------------------
        /// <summary>Fraction of the project's shadow distance.</summary>
        public float ShadowDistanceScale = 1f;
        public int ShadowCascades = 4;
        /// <summary>Per-pixel lights; each extra one re-draws lit objects.</summary>
        public int PixelLights = 4;
        /// <summary>Fraction of the project's LOD bias (lower switches to simpler meshes sooner).</summary>
        public float LodBiasScale = 1f;
        public int ParticleRaycastBudget = 4096;

        public QualityProfile Clone() => (QualityProfile)MemberwiseClone();

        public override string ToString() => Name;
    }

    /// <summary>The PC and Quest profile ladders. PC and Quest are independent: a PC never drops to Quest settings.</summary>
    public static class QualityProfiles
    {
        public static readonly QualityProfile PcUltra = new QualityProfile
        {
            Name = "PC Ultra", Platform = TargetPlatform.Pc, RenderScale = 1f, ShadowResolution = 3, Msaa = 8, TextureMipLimit = 0,
            RealtimeReflectionProbes = true, SoftParticles = true, PostProcessing = true,
            ShadowDistanceScale = 1f, ShadowCascades = 4, PixelLights = 8, LodBiasScale = 1f, ParticleRaycastBudget = 4096,
        };

        public static readonly QualityProfile PcHigh = new QualityProfile
        {
            Name = "PC High", Platform = TargetPlatform.Pc, RenderScale = 1f, ShadowResolution = 2, Msaa = 4, TextureMipLimit = 0,
            RealtimeReflectionProbes = true, SoftParticles = true, PostProcessing = true,
            ShadowDistanceScale = 0.8f, ShadowCascades = 2, PixelLights = 4, LodBiasScale = 0.85f, ParticleRaycastBudget = 1024,
        };

        public static readonly QualityProfile PcBalanced = new QualityProfile
        {
            Name = "PC Balanced", Platform = TargetPlatform.Pc, RenderScale = 0.9f, ShadowResolution = 1, Msaa = 2, TextureMipLimit = 0,
            RealtimeReflectionProbes = false, SoftParticles = true, PostProcessing = true,
            ShadowDistanceScale = 0.6f, ShadowCascades = 2, PixelLights = 2, LodBiasScale = 0.7f, ParticleRaycastBudget = 256,
        };

        public static readonly QualityProfile PcPerformance = new QualityProfile
        {
            Name = "PC Performance", Platform = TargetPlatform.Pc, RenderScale = 0.8f, ShadowResolution = 0, Msaa = 0, TextureMipLimit = 1,
            RealtimeReflectionProbes = false, SoftParticles = false, PostProcessing = false,
            ShadowDistanceScale = 0.35f, ShadowCascades = 1, PixelLights = 1, LodBiasScale = 0.5f, ParticleRaycastBudget = 64,
        };

        public static readonly QualityProfile QuestHigh = new QualityProfile
        {
            Name = "Quest High", Platform = TargetPlatform.Quest, RenderScale = 1f, ShadowResolution = 1, Msaa = 4, MinFoveation = 0f, TextureMipLimit = 0,
            RealtimeReflectionProbes = false, SoftParticles = false, PostProcessing = false,
            ShadowDistanceScale = 0.5f, ShadowCascades = 1, PixelLights = 1, LodBiasScale = 0.8f, ParticleRaycastBudget = 64,
        };

        public static readonly QualityProfile QuestBalanced = new QualityProfile
        {
            Name = "Quest Balanced", Platform = TargetPlatform.Quest, RenderScale = 0.9f, ShadowResolution = 0, Msaa = 4, MinFoveation = 0.34f, TextureMipLimit = 0,
            RealtimeReflectionProbes = false, SoftParticles = false, PostProcessing = false,
            ShadowDistanceScale = 0.3f, ShadowCascades = 1, PixelLights = 1, LodBiasScale = 0.6f, ParticleRaycastBudget = 32,
        };

        public static readonly QualityProfile QuestPerformance = new QualityProfile
        {
            Name = "Quest Performance", Platform = TargetPlatform.Quest, RenderScale = 0.8f, ShadowResolution = 0, Msaa = 2, MinFoveation = 0.67f, TextureMipLimit = 1,
            RealtimeReflectionProbes = false, SoftParticles = false, PostProcessing = false,
            ShadowDistanceScale = 0.15f, ShadowCascades = 1, PixelLights = 0, LodBiasScale = 0.45f, ParticleRaycastBudget = 16,
        };

        /// <summary>Best first.</summary>
        public static IReadOnlyList<QualityProfile> For(TargetPlatform platform) =>
            platform == TargetPlatform.Quest
                ? new[] { QuestHigh, QuestBalanced, QuestPerformance }
                : new[] { PcUltra, PcHigh, PcBalanced, PcPerformance };

        /// <summary>
        /// The settings in effect: GPU-side values from <paramref name="gpu"/>, CPU-side ones from <paramref name="cpu"/>.
        /// </summary>
        public static QualityProfile Compose(QualityProfile gpu, QualityProfile cpu)
        {
            QualityProfile p = gpu.Clone();
            p.Name = gpu == cpu ? gpu.Name : "GPU " + gpu.Name + " / CPU " + cpu.Name;
            p.ShadowDistanceScale = cpu.ShadowDistanceScale;
            p.ShadowCascades = Math.Min(gpu.ShadowCascades, cpu.ShadowCascades); // cascades cost both: the lower wins
            p.PixelLights = Math.Min(gpu.PixelLights, cpu.PixelLights);          // so do per-pixel lights
            p.LodBiasScale = cpu.LodBiasScale;
            p.ParticleRaycastBudget = cpu.ParticleRaycastBudget;
            return p;
        }
    }

    /// <summary>What the device reported about itself. Every field is optional (0 / empty = unknown).</summary>
    public sealed class HardwareInfo
    {
        public TargetPlatform Platform;
        public string GpuName = string.Empty;
        public string GpuVendor = string.Empty;
        public string GraphicsApi = string.Empty;
        public int GpuMemoryMb;
        public string CpuName = string.Empty;
        public int CpuCores;
        public int CpuFrequencyMhz;
        public int SystemMemoryMb;
        public string DeviceModel = string.Empty;
        public string XrDevice = string.Empty;
        public float RefreshRate;
        public int RenderWidth, RenderHeight;
        public bool SupportsCompute;
        public int MaxTextureSize;

        public override string ToString() =>
            $"{Platform}: {GpuName} ({GpuVendor}, {GraphicsApi}, {GpuMemoryMb} MB) | {CpuName} {CpuCores}c {CpuFrequencyMhz} MHz | {SystemMemoryMb} MB RAM | {DeviceModel} | XR '{XrDevice}' {RefreshRate:0.#} Hz | {RenderWidth}x{RenderHeight}";
    }

    /// <summary>
    /// The profile PVAR starts from. It only picks a starting rung from several indicators (memory, cores, GPU memory,
    /// headset model); frame-time measurements then move it, so a wrong first guess corrects itself within seconds.
    /// </summary>
    public static class HardwareClassifier
    {
        public static int StartingLevel(HardwareInfo hw)
        {
            if (hw == null) return 1;
            if (hw.Platform == TargetPlatform.Quest)
            {
                string model = (hw.DeviceModel + " " + hw.XrDevice).ToLowerInvariant();
                bool newer = model.Contains("quest 3") || model.Contains("quest pro") || model.Contains("eureka") || hw.SystemMemoryMb >= 7000;
                return newer ? 0 : 1; // Quest 3/Pro start at High, Quest 2 at Balanced
            }
            int score = 0, known = 0;
            if (hw.GpuMemoryMb > 0) { known++; score += hw.GpuMemoryMb >= 10000 ? 2 : hw.GpuMemoryMb >= 6000 ? 1 : 0; }
            if (hw.SystemMemoryMb > 0) { known++; score += hw.SystemMemoryMb >= 30000 ? 2 : hw.SystemMemoryMb >= 15000 ? 1 : 0; }
            if (hw.CpuCores > 0) { known++; score += hw.CpuCores >= 12 ? 2 : hw.CpuCores >= 6 ? 1 : 0; }
            if (known == 0) return 1;
            float average = (float)score / known; // 0 … 2
            // Ultra is only reached by measurement: the start is at most High.
            return average >= 1.5f ? 1 : average >= 0.75f ? 2 : 3;
        }
    }
}
