using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    public sealed class QuestBudgetTests
    {
        [Test]
        public void Texture_memory_is_astc_6x6_with_mips()
        {
            // 2048² at 128 bits per 6x6 block ≈ 1.78 MiB, + 1/3 for mips ≈ 2.37 MiB.
            long bytes = QuestBudgets.TextureBytes(2048, 2048, mipmaps: true);
            Assert.AreEqual(2.37, bytes / (1024.0 * 1024.0), 0.01);
            Assert.AreEqual(6 * QuestBudgets.TextureBytes(512, 512, false), QuestBudgets.TextureBytes(512, 512, false, faces: 6), 6);
            Assert.AreEqual(1024, QuestBudgets.CappedSize(4096, 1024));
            Assert.AreEqual(300, QuestBudgets.CappedSize(300, 1024), "never upscaled");
        }

        [Test]
        public void Content_within_budget_has_no_findings()
        {
            var s = new QuestContentStats { Triangles = 200_000, MaterialSlots = 120, TextureBytes = 100L << 20, RealtimeLights = 2, ForcedPixelLights = 1 };
            CollectionAssert.IsEmpty(QuestBudgets.Assess(s));
        }

        [Test]
        public void Over_budget_world_gets_one_finding_per_problem()
        {
            var s = new QuestContentStats { Triangles = 1_200_000, MaterialSlots = 900, TextureBytes = 900L << 20, RealtimeLights = 12, ForcedPixelLights = 5 };
            var f = QuestBudgets.Assess(s);
            Assert.AreEqual(5, f.Count);
            StringAssert.Contains("1,200,000 triangles", f[0]);
            StringAssert.Contains("Mesh LODs", f[0]);
            StringAssert.Contains("900 MB", f[2]);
            StringAssert.Contains("12 realtime lights", f[3]);
        }

        [Test]
        public void Avatars_use_avatar_budgets_and_skip_world_light_rules()
        {
            var s = new QuestContentStats { Avatar = true, Triangles = 70_000, MaterialSlots = 12, TextureBytes = 10L << 20, RealtimeLights = 9 };
            var f = QuestBudgets.Assess(s);
            Assert.AreEqual(2, f.Count);
            StringAssert.Contains("per avatar", f[0]);
            StringAssert.Contains("atlas", f[1]);
        }
    }
}
