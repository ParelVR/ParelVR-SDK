using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    public sealed class AdaptiveTests
    {
        private const float Budget90 = 1000f / 90f; // 11.1 ms

        /// <summary>Feeds <paramref name="seconds"/> of frames at the budget's rate and returns how many steps were taken.</summary>
        private static int Run(AdaptiveController c, ref double t, float seconds, float gpuMs, float cpuMs, bool split = true, bool thermal = false)
        {
            int steps = 0;
            double end = t + seconds;
            while (t < end)
            {
                t += Budget90 / 1000.0;
                var s = new FrameSample { Time = t, GpuMs = gpuMs, CpuMs = cpuMs, TargetMs = Budget90, HasSplit = split, Thermal = thermal };
                if (!split) s = FrameSample.Total(t, gpuMs, Budget90);
                s.Thermal = thermal;
                if (c.Update(s)) steps++;
            }
            return steps;
        }

        [Test]
        public void Profiles_ladders_go_from_best_to_worst()
        {
            var pc = QualityProfiles.For(TargetPlatform.Pc);
            Assert.AreEqual(4, pc.Count);
            Assert.AreEqual("PC Ultra", pc[0].Name);
            for (int i = 1; i < pc.Count; i++)
            {
                Assert.LessOrEqual(pc[i].RenderScale, pc[i - 1].RenderScale);
                Assert.LessOrEqual(pc[i].ShadowDistanceScale, pc[i - 1].ShadowDistanceScale);
                Assert.LessOrEqual(pc[i].PixelLights, pc[i - 1].PixelLights);
                Assert.LessOrEqual(pc[i].LodBiasScale, pc[i - 1].LodBiasScale);
            }
            var quest = QualityProfiles.For(TargetPlatform.Quest);
            Assert.AreEqual(3, quest.Count);
            foreach (QualityProfile p in quest) Assert.AreEqual(TargetPlatform.Quest, p.Platform);
            for (int i = 1; i < quest.Count; i++) Assert.GreaterOrEqual(quest[i].MinFoveation, quest[i - 1].MinFoveation);
        }

        [Test]
        public void Compose_takes_gpu_values_from_one_rung_and_cpu_values_from_the_other()
        {
            QualityProfile p = QualityProfiles.Compose(QualityProfiles.PcPerformance, QualityProfiles.PcUltra);
            Assert.AreEqual(QualityProfiles.PcPerformance.RenderScale, p.RenderScale);
            Assert.AreEqual(QualityProfiles.PcPerformance.Msaa, p.Msaa);
            Assert.AreEqual(QualityProfiles.PcUltra.ShadowDistanceScale, p.ShadowDistanceScale);
            Assert.AreEqual(QualityProfiles.PcUltra.LodBiasScale, p.LodBiasScale);
            Assert.AreEqual(1, p.PixelLights, "per-pixel lights cost both sides: the lower count wins");
            StringAssert.Contains("GPU PC Performance", p.Name);
        }

        [Test]
        public void Hardware_classifier_never_starts_pc_at_ultra_and_starts_quest_by_model()
        {
            var strong = new HardwareInfo { Platform = TargetPlatform.Pc, GpuMemoryMb = 16000, SystemMemoryMb = 64000, CpuCores = 16 };
            var weak = new HardwareInfo { Platform = TargetPlatform.Pc, GpuMemoryMb = 2000, SystemMemoryMb = 8000, CpuCores = 4 };
            Assert.AreEqual(1, HardwareClassifier.StartingLevel(strong));
            Assert.AreEqual(3, HardwareClassifier.StartingLevel(weak));
            Assert.AreEqual(1, HardwareClassifier.StartingLevel(new HardwareInfo { Platform = TargetPlatform.Pc }), "nothing known: start in the middle");
            Assert.AreEqual(0, HardwareClassifier.StartingLevel(new HardwareInfo { Platform = TargetPlatform.Quest, DeviceModel = "Oculus Quest 3" }));
            Assert.AreEqual(1, HardwareClassifier.StartingLevel(new HardwareInfo { Platform = TargetPlatform.Quest, DeviceModel = "Oculus Quest 2", SystemMemoryMb = 5800 }));
        }

        [Test]
        public void Gpu_bound_frames_lower_only_the_gpu_rung()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Assert.Greater(Run(c, ref t, 4f, gpuMs: 14f, cpuMs: 5f), 0);
            Assert.Greater(c.GpuLevel, 0);
            Assert.AreEqual(0, c.CpuLevel);
            StringAssert.StartsWith("GPU down", c.LastReason);
        }

        [Test]
        public void Cpu_bound_frames_lower_only_the_cpu_rung()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Run(c, ref t, 4f, gpuMs: 4f, cpuMs: 14f);
            Assert.Greater(c.CpuLevel, 0);
            Assert.AreEqual(0, c.GpuLevel);
        }

        [Test]
        public void Steps_are_spaced_and_stop_at_the_worst_rung()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Assert.AreEqual(1, Run(c, ref t, 1.9f, gpuMs: 30f, cpuMs: 30f), "one step within the minimum interval");
            Run(c, ref t, 60f, gpuMs: 30f, cpuMs: 30f);
            Assert.AreEqual(3, c.GpuLevel);
            Assert.AreEqual(3, c.CpuLevel);
            Assert.AreEqual(0, Run(c, ref t, 10f, gpuMs: 30f, cpuMs: 30f), "nothing left to lower");
        }

        [Test]
        public void A_single_hitch_does_not_drop_a_step()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Run(c, ref t, 3f, gpuMs: 6f, cpuMs: 6f);
            t += 0.5; // a 500 ms frame
            Assert.IsFalse(c.Update(new FrameSample { Time = t, GpuMs = 500f, CpuMs = 500f, TargetMs = Budget90, HasSplit = true }));
            Assert.AreEqual(0, Run(c, ref t, 3f, gpuMs: 6f, cpuMs: 6f));
            Assert.AreEqual(0, c.GpuLevel);
        }

        [Test]
        public void Recovery_waits_for_the_recover_delay_then_steps_back_up()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Run(c, ref t, 1.5f, gpuMs: 14f, cpuMs: 5f);
            Assert.AreEqual(1, c.GpuLevel);
            Assert.AreEqual(0, Run(c, ref t, 6f, gpuMs: 5f, cpuMs: 5f), "still within the recover delay");
            Assert.AreEqual(1, c.GpuLevel);
            Run(c, ref t, 4f, gpuMs: 5f, cpuMs: 5f);
            Assert.AreEqual(0, c.GpuLevel);
            StringAssert.StartsWith("GPU up", c.LastReason);
        }

        [Test]
        public void Bouncing_doubles_the_recover_delay()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            // Level 0 is too slow (14 ms), level 1 is fine. Each recovery to level 0 is taken back.
            int ups = 0;
            for (int i = 0; i < 400; i++)
            {
                t += Budget90 / 1000.0 * 10;
                float gpu = c.GpuLevel == 0 ? 14f : 6f;
                if (c.Update(new FrameSample { Time = t, GpuMs = gpu, CpuMs = 4f, TargetMs = Budget90, HasSplit = true }) && c.LastReason.StartsWith("GPU up")) ups++;
                if (t > 60) break;
            }
            // With a fixed 8 s delay there would be about 6 recoveries in 60 s; doubling (8, 16, 32) allows 3.
            Assert.LessOrEqual(ups, 3);
            Assert.GreaterOrEqual(ups, 1);
        }

        [Test]
        public void Total_frame_time_lowers_gpu_first_then_cpu_and_recovers_in_reverse()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Run(c, ref t, 30f, gpuMs: 30f, cpuMs: 30f, split: false);
            Assert.AreEqual(3, c.GpuLevel);
            Assert.AreEqual(3, c.CpuLevel);
            StringAssert.Contains("total frame time", c.LastReason);
            Run(c, ref t, 12f, gpuMs: 4f, cpuMs: 4f, split: false);
            Assert.Less(c.CpuLevel, 3, "CPU comes back first");
            Assert.AreEqual(3, c.GpuLevel);
            Run(c, ref t, 60f, gpuMs: 4f, cpuMs: 4f, split: false);
            Assert.AreEqual(0, c.CpuLevel);
            Assert.AreEqual(0, c.GpuLevel, "then the GPU, all the way back");
        }

        [Test]
        public void Paced_total_frame_time_only_counts_missed_frames()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Quest));
            double t = 0;
            int steps = 0;
            // Healthy: every frame takes exactly the budget because it waits for the display.
            for (int i = 0; i < 900; i++)
            {
                t += Budget90 / 1000.0;
                if (c.Update(new FrameSample { Time = t, CpuMs = Budget90, GpuMs = Budget90, TargetMs = Budget90, Paced = true })) steps++;
            }
            Assert.AreEqual(0, steps, "frames at the budget are fine when paced");
            // Every third frame missed (takes two intervals).
            for (int i = 0; i < 900; i++)
            {
                float ms = i % 3 == 0 ? 2 * Budget90 : Budget90;
                t += ms / 1000.0;
                if (c.Update(new FrameSample { Time = t, CpuMs = ms, GpuMs = ms, TargetMs = Budget90, Paced = true })) steps++;
            }
            Assert.Greater(c.GpuLevel, 0);
            Assert.Greater(steps, 0);
        }

        [Test]
        public void Thermal_throttling_blocks_recovery()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Quest));
            double t = 0;
            Run(c, ref t, 1.5f, gpuMs: 14f, cpuMs: 5f);
            Assert.AreEqual(1, c.GpuLevel);
            Assert.AreEqual(0, Run(c, ref t, 30f, gpuMs: 4f, cpuMs: 4f, thermal: true));
            Assert.AreEqual(1, c.GpuLevel);
        }

        [Test]
        public void User_limits_bound_both_rungs()
        {
            var settings = new AdaptiveSettings { BestLevel = 1, WorstLevel = 2 };
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc), settings, startLevel: 0);
            Assert.AreEqual(1, c.GpuLevel, "start clamped to the ceiling");
            double t = 0;
            Run(c, ref t, 30f, gpuMs: 30f, cpuMs: 30f);
            Assert.AreEqual(2, c.GpuLevel);
            Assert.AreEqual(2, c.CpuLevel);
            settings.WorstLevel = 1;
            Assert.IsTrue(c.ApplyLimits());
            Assert.AreEqual(1, c.GpuLevel);
        }

        [Test]
        public void Hold_ignores_samples_while_content_loads()
        {
            var c = new AdaptiveController(QualityProfiles.For(TargetPlatform.Pc));
            double t = 0;
            Run(c, ref t, 1f, gpuMs: 6f, cpuMs: 6f);
            c.Hold(t, 5f);
            Assert.AreEqual(0, Run(c, ref t, 4.9f, gpuMs: 40f, cpuMs: 40f));
            Assert.AreEqual(0, c.GpuLevel);
        }
    }
}
