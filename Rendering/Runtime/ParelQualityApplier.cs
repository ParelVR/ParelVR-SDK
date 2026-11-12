using System.Collections.Generic;
using UnityEngine;
#if PVRP_XR
using UnityEngine.XR;
#endif

namespace ParelVR.Rendering
{
    /// <summary>
    /// The quality values PVAR works with, as Unity holds them. Used twice: the project's own top setup (what profile
    /// scales are relative to) and the player's chosen settings (the ceiling PVAR never goes above).
    /// </summary>
    public struct QualityLimits
    {
        public float ShadowDistance;
        public int ShadowCascades;
        public ShadowResolution ShadowResolution;
        public int PixelLights;
        public float LodBias;
        public int ParticleRaycastBudget;
        public bool SoftParticles;
        public bool RealtimeReflectionProbes;
        public int Msaa;
        public int TextureMipLimit;
        /// <summary>The player's foveation level (0 … 1).</summary>
        public float Foveation;

        /// <summary>The values Unity is using right now.</summary>
        public static QualityLimits Capture() => new QualityLimits
        {
            ShadowDistance = QualitySettings.shadowDistance,
            ShadowCascades = QualitySettings.shadowCascades,
            ShadowResolution = QualitySettings.shadowResolution,
            PixelLights = QualitySettings.pixelLightCount,
            LodBias = QualitySettings.lodBias,
            ParticleRaycastBudget = QualitySettings.particleRaycastBudget,
            SoftParticles = QualitySettings.softParticles,
            RealtimeReflectionProbes = QualitySettings.realtimeReflectionProbes,
            Msaa = QualitySettings.antiAliasing,
            TextureMipLimit = QualitySettings.globalTextureMipmapLimit,
        };

        public override string ToString() =>
            $"shadows {ShadowDistance:0.#} m x{ShadowCascades} {ShadowResolution}, {PixelLights} px lights, LOD {LodBias:0.##}, particles {ParticleRaycastBudget}, " +
            $"soft {SoftParticles}, probes {RealtimeReflectionProbes}, MSAA {Msaa}, mip {TextureMipLimit}, foveation {Foveation:0.##}";
    }

    /// <summary>
    /// Applies a <see cref="QualityProfile"/> through QualitySettings and XR. Every value is the lower of the player's
    /// choice and the profile's, so PVAR only ever takes quality away, never adds it. Settings that cause a visible
    /// hitch when changed while running (MSAA in a headset re-creates the eye buffers; the texture mip limit re-uploads
    /// every texture) are only changed when <see cref="AdaptMsaa"/> / <see cref="AdaptTextures"/> are set.
    /// </summary>
    public sealed class ParelQualityApplier
    {
        public QualityLimits Project;
        public QualityLimits User;
        public bool AdaptMsaa;
        public bool AdaptTextures;

        /// <summary>What the last <see cref="Apply"/> set, for the log and the debug overlay.</summary>
        public QualityLimits Applied { get; private set; }
        /// <summary>The headset render viewport scale the last <see cref="Apply"/> set (1 on a desktop).</summary>
        public float AppliedRenderScale { get; private set; } = 1f;

        public ParelQualityApplier(QualityLimits project, QualityLimits user)
        {
            Project = project;
            User = user;
        }

        /// <summary>The values <paramref name="p"/> leads to, without applying them.</summary>
        public QualityLimits Resolve(QualityProfile p)
        {
            QualityLimits u = User;
            return new QualityLimits
            {
                ShadowDistance = Mathf.Min(u.ShadowDistance, Project.ShadowDistance * p.ShadowDistanceScale),
                ShadowCascades = Mathf.Min(u.ShadowCascades, p.ShadowCascades),
                ShadowResolution = (ShadowResolution)Mathf.Min((int)u.ShadowResolution, p.ShadowResolution),
                PixelLights = Mathf.Min(u.PixelLights, p.PixelLights),
                LodBias = Mathf.Max(0.1f, Mathf.Min(u.LodBias, Project.LodBias * p.LodBiasScale)),
                ParticleRaycastBudget = Mathf.Min(u.ParticleRaycastBudget, p.ParticleRaycastBudget),
                SoftParticles = u.SoftParticles && p.SoftParticles,
                RealtimeReflectionProbes = u.RealtimeReflectionProbes && p.RealtimeReflectionProbes,
                Msaa = AdaptMsaa ? Mathf.Min(u.Msaa, p.Msaa) : u.Msaa,
                TextureMipLimit = AdaptTextures ? Mathf.Max(u.TextureMipLimit, p.TextureMipLimit) : u.TextureMipLimit,
                Foveation = Mathf.Max(u.Foveation, p.MinFoveation),
            };
        }

        public void Apply(QualityProfile p)
        {
            QualityLimits v = Resolve(p);
            Set(v);
            Applied = v;
            AppliedRenderScale = SetRenderScale(Mathf.Clamp(p.RenderScale, 0.5f, 1f));
        }

        /// <summary>Puts the player's own settings back (PVAR switched off).</summary>
        public void Restore()
        {
            Set(User);
            Applied = User;
            AppliedRenderScale = SetRenderScale(1f);
        }

        private static void Set(QualityLimits v)
        {
            // A world that carries its creator's quality settings runs on them (ParelWorldQuality.Hold); only the
            // headset's foveation stays PVAR's.
            if (ParelWorldQuality.Held != null)
            {
                SetFoveation(v.Foveation);
                return;
            }
            // Only write what changed: some setters do work even when the value is the same.
            if (!Mathf.Approximately(QualitySettings.shadowDistance, v.ShadowDistance)) QualitySettings.shadowDistance = v.ShadowDistance;
            if (QualitySettings.shadowCascades != v.ShadowCascades) QualitySettings.shadowCascades = v.ShadowCascades;
            if (QualitySettings.shadowResolution != v.ShadowResolution) QualitySettings.shadowResolution = v.ShadowResolution;
            if (QualitySettings.pixelLightCount != v.PixelLights) QualitySettings.pixelLightCount = v.PixelLights;
            if (!Mathf.Approximately(QualitySettings.lodBias, v.LodBias)) QualitySettings.lodBias = v.LodBias;
            if (QualitySettings.particleRaycastBudget != v.ParticleRaycastBudget) QualitySettings.particleRaycastBudget = v.ParticleRaycastBudget;
            if (QualitySettings.softParticles != v.SoftParticles) QualitySettings.softParticles = v.SoftParticles;
            if (QualitySettings.realtimeReflectionProbes != v.RealtimeReflectionProbes) QualitySettings.realtimeReflectionProbes = v.RealtimeReflectionProbes;
            if (QualitySettings.antiAliasing != v.Msaa) QualitySettings.antiAliasing = v.Msaa;
            if (QualitySettings.globalTextureMipmapLimit != v.TextureMipLimit) QualitySettings.globalTextureMipmapLimit = v.TextureMipLimit;
            SetFoveation(v.Foveation);
        }

        // The viewport scale renders into part of the eye buffer, so it changes without re-creating the buffers
        // (unlike eyeTextureResolutionScale, which the player's Render Resolution setting owns).
        private static float SetRenderScale(float scale)
        {
#if PVRP_XR
            if (!ParelHardwareDetector.XrActive) return 1f;
            if (!Mathf.Approximately(XRSettings.renderViewportScale, scale)) XRSettings.renderViewportScale = scale;
            return XRSettings.renderViewportScale;
#else
            return 1f;
#endif
        }

#if PVRP_XR
        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();
#endif

        private static void SetFoveation(float level)
        {
#if PVRP_XR
            Displays.Clear();
            SubsystemManager.GetSubsystems(Displays);
            foreach (XRDisplaySubsystem d in Displays)
                if (d != null && d.running && !Mathf.Approximately(d.foveatedRenderingLevel, level)) d.foveatedRenderingLevel = level;
#endif
        }
    }
}
