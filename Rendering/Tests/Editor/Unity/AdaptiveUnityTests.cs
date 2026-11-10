using NUnit.Framework;
using UnityEngine;

namespace ParelVR.Rendering.Tests
{
    /// <summary>The PVAR applier against the real QualitySettings. Every test puts the Editor's settings back.</summary>
    public sealed class AdaptiveUnityTests
    {
        private QualityLimits _saved;

        [SetUp]
        public void Save() => _saved = QualityLimits.Capture();

        [TearDown]
        public void Restore()
        {
            var a = new ParelQualityApplier(_saved, _saved);
            a.Restore();
        }

        private static QualityLimits Top() => new QualityLimits
        {
            ShadowDistance = 150f, ShadowCascades = 4, ShadowResolution = ShadowResolution.VeryHigh, PixelLights = 8, LodBias = 2f,
            ParticleRaycastBudget = 4096, SoftParticles = true, RealtimeReflectionProbes = true, Msaa = 8, TextureMipLimit = 0,
        };

        [Test]
        public void Each_pc_profile_applies_and_lower_rungs_never_raise_a_value()
        {
            QualityLimits top = Top();
            var applier = new ParelQualityApplier(top, top);
            QualityLimits previous = default;
            bool first = true;
            foreach (QualityProfile p in QualityProfiles.For(TargetPlatform.Pc))
            {
                applier.Apply(p);
                QualityLimits now = QualityLimits.Capture();
                Assert.AreEqual(applier.Applied.ShadowCascades, now.ShadowCascades, p.Name);
                Assert.AreEqual(applier.Applied.PixelLights, now.PixelLights, p.Name);
                Assert.AreEqual(applier.Applied.ShadowDistance, now.ShadowDistance, 0.01f, p.Name);
                Assert.AreEqual(applier.Applied.LodBias, now.LodBias, 0.001f, p.Name);
                Assert.AreEqual(top.ShadowDistance * p.ShadowDistanceScale, now.ShadowDistance, 0.01f, p.Name);
                if (!first)
                {
                    Assert.LessOrEqual(now.ShadowDistance, previous.ShadowDistance, p.Name);
                    Assert.LessOrEqual(now.PixelLights, previous.PixelLights, p.Name);
                    Assert.LessOrEqual(now.LodBias, previous.LodBias, p.Name);
                    Assert.LessOrEqual((int)now.ShadowResolution, (int)previous.ShadowResolution, p.Name);
                    Assert.LessOrEqual(now.ParticleRaycastBudget, previous.ParticleRaycastBudget, p.Name);
                }
                previous = now;
                first = false;
            }
        }

        [Test]
        public void The_players_settings_are_the_ceiling()
        {
            QualityLimits user = Top();
            user.ShadowDistance = 20f;
            user.PixelLights = 1;
            user.LodBias = 0.5f;
            user.SoftParticles = false;
            var applier = new ParelQualityApplier(Top(), user);
            applier.Apply(QualityProfiles.PcUltra);
            QualityLimits now = QualityLimits.Capture();
            Assert.AreEqual(20f, now.ShadowDistance, 0.01f);
            Assert.AreEqual(1, now.PixelLights);
            Assert.AreEqual(0.5f, now.LodBias, 0.001f);
            Assert.IsFalse(now.SoftParticles);
        }

        [Test]
        public void Msaa_and_texture_mips_only_change_when_allowed()
        {
            QualityLimits top = Top();
            top.Msaa = 4;
            var applier = new ParelQualityApplier(top, top);
            applier.Apply(QualityProfiles.PcPerformance);
            Assert.AreEqual(4, QualitySettings.antiAliasing, "MSAA re-creates the eye buffers: left alone by default");
            Assert.AreEqual(0, QualitySettings.globalTextureMipmapLimit);
            applier.AdaptMsaa = true;
            applier.AdaptTextures = true;
            applier.Apply(QualityProfiles.PcPerformance);
            Assert.AreEqual(0, QualitySettings.antiAliasing);
            Assert.AreEqual(1, QualitySettings.globalTextureMipmapLimit);
        }

        [Test]
        public void Restore_puts_the_players_settings_back()
        {
            QualityLimits user = Top();
            user.ShadowDistance = 77f;
            var applier = new ParelQualityApplier(Top(), user);
            applier.Apply(QualityProfiles.PcPerformance);
            Assert.Less(QualitySettings.shadowDistance, 77f);
            applier.Restore();
            Assert.AreEqual(77f, QualitySettings.shadowDistance, 0.01f);
            Assert.AreEqual(user.PixelLights, QualitySettings.pixelLightCount);
        }

        [Test]
        public void Hardware_detection_reads_this_machine()
        {
            HardwareInfo hw = ParelHardwareDetector.Detect();
            Assert.IsNotEmpty(hw.GpuName);
            Assert.Greater(hw.SystemMemoryMb, 0);
            Assert.Greater(hw.CpuCores, 0);
            Assert.Greater(hw.RefreshRate, 1f);
            Assert.Greater(ParelHardwareDetector.TargetFrameMs(), 0f);
            int start = HardwareClassifier.StartingLevel(hw);
            Assert.That(start, Is.InRange(1, 3), "a PC never starts at Ultra");
        }
    }
}
