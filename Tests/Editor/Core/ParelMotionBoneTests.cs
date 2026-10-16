using System;
using System.Collections.Generic;
using NUnit.Framework;
using ParelVR.AvatarSDK;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ParelVR.SDK.Tests
{
    public class ParelMotionBoneTests
    {
        private const int Bones = 4;
        private const float BoneLength = 0.1f;

        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _created)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _created.Clear();
        }

        /// <summary>A straight chain of <see cref="Bones"/> bones along +X under a holder object at the origin.</summary>
        private ParelMotionBone MakeChain(out Transform holder)
        {
            var go = new GameObject("MotionBoneTest");
            _created.Add(go);
            holder = go.transform;
            Transform parent = holder;
            Transform chainRoot = null;
            for (int i = 0; i < Bones; i++)
            {
                Transform bone = new GameObject("Bone" + i).transform;
                bone.SetParent(parent, false);
                bone.localPosition = i == 0 ? Vector3.zero : Vector3.right * BoneLength;
                if (i == 0) chainRoot = bone;
                parent = bone;
            }
            var motionBone = go.AddComponent<ParelMotionBone>();
            motionBone.rootTransform = chainRoot;
            return motionBone;
        }

        /// <summary>Runs frames the way ParelMotionBoneSystem does (prepare, then simulate if ready).</summary>
        private static void Run(ParelMotionBone bone, float seconds, float fps)
        {
            int frames = Mathf.RoundToInt(seconds * fps);
            for (int i = 0; i < frames; i++) Frame(bone, 1f / fps);
        }

        private static void Frame(ParelMotionBone bone, float deltaTime)
        {
            if (bone.PrepareFrame()) bone.Simulate(deltaTime, bone.colliders);
        }

        private static Vector3 Tip(ParelMotionBone bone) => bone.ParticlePosition(bone.ParticleCount - 1);

        [Test]
        public void RestingChain_StaysAtRest()
        {
            ParelMotionBone bone = MakeChain(out _);
            Run(bone, 2f, 72f);
            Assert.That(Vector3.Distance(Tip(bone), new Vector3(0.3f, 0f, 0f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void Gravity_SettlesAlikeAtDifferentFrameRates()
        {
            ParelMotionBone slow = MakeChain(out _);
            ParelMotionBone fast = MakeChain(out _);
            foreach (ParelMotionBone bone in new[] { slow, fast })
            {
                bone.gravity = 0.5f;
                bone.pull = 0.2f;
            }

            Run(slow, 4f, 30f);
            Run(fast, 4f, 144f);

            Assert.That(Tip(slow).y, Is.LessThan(-0.01f), "gravity should bend the chain");
            Assert.That(Vector3.Distance(Tip(slow), Tip(fast)), Is.LessThan(0.02f));
        }

        [Test]
        public void HighRefreshRate_MovesTheChainEveryFrame()
        {
            ParelMotionBone bone = MakeChain(out Transform holder);
            bone.gravity = 0.3f;
            Run(bone, 0.5f, 120f);

            for (int i = 0; i < 60; i++)
            {
                Vector3 before = Tip(bone);
                holder.position += Vector3.forward * (1f / 120f);
                Frame(bone, 1f / 120f);
                Assert.That(Vector3.Distance(before, Tip(bone)), Is.GreaterThan(1e-6f), $"frame {i} did not advance");
            }
        }

        [Test]
        public void Teleport_ResetsInsteadOfWhipping()
        {
            ParelMotionBone bone = MakeChain(out Transform holder);
            bone.gravity = 0.5f;
            Run(bone, 1f, 90f);

            holder.position += new Vector3(50f, 0f, 0f);
            Frame(bone, 1f / 90f);

            Assert.That(Vector3.Distance(Tip(bone), new Vector3(50.3f, 0f, 0f)), Is.LessThan(0.05f));
        }

        [Test]
        public void SnapTurn_CarriesTheChainRound()
        {
            ParelMotionBone bone = MakeChain(out Transform holder);
            Run(bone, 0.5f, 90f);

            holder.rotation = Quaternion.Euler(0f, 90f, 0f);
            Frame(bone, 1f / 90f);

            Vector3 expected = Quaternion.Euler(0f, 90f, 0f) * new Vector3(0.3f, 0f, 0f);
            Assert.That(Vector3.Distance(Tip(bone), expected), Is.LessThan(0.01f));
        }

        [Test]
        public void AngleLimit_HoldsUnderGravity()
        {
            ParelMotionBone bone = MakeChain(out _);
            bone.gravity = 1f;
            bone.pull = 0f;
            bone.spring = 0f;
            bone.limitType = ParelMotionBone.LimitType.Angle;
            bone.maxAngleX = 15f;
            Run(bone, 2f, 90f);

            for (int i = 1; i < bone.ParticleCount; i++)
            {
                Vector3 segment = bone.ParticlePosition(i) - bone.ParticlePosition(i - 1);
                Assert.That(Vector3.Angle(Vector3.right, segment), Is.LessThan(15.5f), $"bone {i}");
            }
        }

        [Test]
        public void DestroyedBone_RebuildsWithoutThrowing()
        {
            ParelMotionBone bone = MakeChain(out _);
            Run(bone, 0.2f, 90f);
            Assert.AreEqual(Bones, bone.ParticleCount);

            Object.DestroyImmediate(bone.Root.GetChild(0).GetChild(0).gameObject);

            Assert.DoesNotThrow(() => Run(bone, 0.2f, 90f));
            Assert.AreEqual(2, bone.ParticleCount);
        }

        [Test]
        public void NaNSetting_LeavesTheBonesInTheirPose()
        {
            ParelMotionBone bone = MakeChain(out _);
            bone.gravity = float.NaN;
            Run(bone, 0.5f, 90f);

            Transform tip = bone.Root.GetChild(0).GetChild(0).GetChild(0);
            Assert.That(Vector3.Distance(tip.position, new Vector3(0.3f, 0f, 0f)), Is.LessThan(1e-4f));
            Assert.IsFalse(float.IsNaN(Tip(bone).x));
        }

        [Test]
        public void Presets_StayInsideTheInspectorRanges()
        {
            ParelMotionBone bone = MakeChain(out _);
            foreach (ParelMotionBonePreset preset in Enum.GetValues(typeof(ParelMotionBonePreset)))
            {
                bone.pullCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
                bone.ApplyPreset(preset);

                string name = preset.ToString();
                Assert.That(bone.pull, Is.InRange(0f, 1f), name);
                Assert.That(bone.spring, Is.InRange(0f, 1f), name);
                Assert.That(bone.stiffness, Is.InRange(0f, 1f), name);
                Assert.That(bone.gravity, Is.InRange(-1f, 1f), name);
                Assert.That(bone.gravityFalloff, Is.InRange(0f, 1f), name);
                Assert.That(bone.immobile, Is.InRange(0f, 1f), name);
                Assert.That(bone.maxAngleX, Is.InRange(0f, 180f), name);
                Assert.That(bone.maxStretch, Is.InRange(0f, 5f), name);
                Assert.That(bone.maxSquish, Is.InRange(0f, 1f), name);
                Assert.That(bone.stretchMotion, Is.InRange(0f, 1f), name);
                Assert.AreEqual(0, bone.pullCurve.length, name);
            }
        }
    }
}
