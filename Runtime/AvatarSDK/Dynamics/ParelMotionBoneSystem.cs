using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("ParelVR.SDK.Tests")]

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// A hand (or anything else) that can grab Motion Bones. The game registers one per hand of every
    /// player -- remote players included, so everyone sees the same grabs.
    /// </summary>
    public interface IParelMotionBoneGrabber
    {
        /// <summary>The avatar root of whoever is grabbing (decides Self / Others permission).</summary>
        Transform OwnerRoot { get; }
        Vector3 Position { get; }
        float Radius { get; }
        bool IsGrabbing { get; }
        /// <summary>Held while releasing to leave the bone posed.</summary>
        bool IsPosing { get; }
        bool IsValid { get; }
    }

    /// <summary>
    /// Runs every Motion Bone once per frame after animation, IK and networking have placed the bones
    /// (see <see cref="ParelAvatarDynamicsRunner"/>), handles grabbing / posing, and keeps the list
    /// of "global" colliders (everyone's hands and fingers).
    /// </summary>
    public static class ParelMotionBoneSystem
    {
        private sealed class GrabState
        {
            public ParelMotionBone Bone;
            public int Particle;
            public Vector3 Offset;
        }

        private static readonly List<ParelMotionBone> Bones = new List<ParelMotionBone>();
        private static readonly List<ParelMotionBone> Ready = new List<ParelMotionBone>();
        private static readonly List<ParelMotionBoneCollider> AllColliders = new List<ParelMotionBoneCollider>();
        private static readonly List<ParelMotionBoneCollider> ActiveGlobals = new List<ParelMotionBoneCollider>();
        private static readonly List<ParelMotionBoneCollider> FrameColliders = new List<ParelMotionBoneCollider>();
        private static readonly List<IParelMotionBoneGrabber> Grabbers = new List<IParelMotionBoneGrabber>();
        private static readonly Dictionary<IParelMotionBoneGrabber, GrabState> Grabs = new Dictionary<IParelMotionBoneGrabber, GrabState>();

        internal static readonly List<int> PathBuffer = new List<int>(64);

        /// <summary>Hard cap on simulated bones (all avatars together) to keep frame time bounded.</summary>
        public static int MaxSimulatedParticles = 12000;

        /// <summary>
        /// Motion Bones whose root is farther than this (meters) from <see cref="Viewer"/> rest in their
        /// original pose instead of simulating. 0 (the default) never skips any.
        /// </summary>
        public static float CullDistance;

        /// <summary>Where <see cref="CullDistance"/> is measured from (empty = the main camera).</summary>
        public static Transform Viewer;

        public static int ActiveCount => Bones.Count;

        /// <summary>Motion Bones simulated last frame.</summary>
        public static int SimulatedLastFrame { get; private set; }

        /// <summary>Motion Bones paused last frame for the budget or <see cref="CullDistance"/>.</summary>
        public static int SkippedLastFrame { get; private set; }

        /// <summary>Bones (particles) simulated last frame, all Motion Bones together.</summary>
        public static int SimulatedParticlesLastFrame { get; private set; }

        public static void Register(ParelMotionBone bone)
        {
            if (bone == null || Bones.Contains(bone)) return;
            Bones.Add(bone);
            ParelAvatarDynamicsRunner.EnsureRunning();
        }

        public static void Unregister(ParelMotionBone bone)
        {
            Bones.Remove(bone);
            foreach (KeyValuePair<IParelMotionBoneGrabber, GrabState> pair in Grabs)
            {
                if (pair.Value.Bone == bone)
                {
                    Grabs.Remove(pair.Key);
                    break;
                }
            }
        }

        internal static void RegisterCollider(ParelMotionBoneCollider collider)
        {
            if (collider != null && !AllColliders.Contains(collider)) AllColliders.Add(collider);
        }

        internal static void UnregisterCollider(ParelMotionBoneCollider collider) => AllColliders.Remove(collider);

        /// <summary>Makes a collider bump every avatar's Motion Bones (subject to their Allow Collision).</summary>
        public static void RegisterGlobalCollider(ParelMotionBoneCollider collider)
        {
            if (collider == null) return;
            collider.IsGlobal = true;
            RegisterCollider(collider);
        }

        public static void UnregisterGlobalCollider(ParelMotionBoneCollider collider)
        {
            if (collider != null) collider.IsGlobal = false;
        }

        public static void RegisterGrabber(IParelMotionBoneGrabber grabber)
        {
            if (grabber != null && !Grabbers.Contains(grabber)) Grabbers.Add(grabber);
            ParelAvatarDynamicsRunner.EnsureRunning();
        }

        public static void UnregisterGrabber(IParelMotionBoneGrabber grabber)
        {
            if (grabber == null) return;
            Grabbers.Remove(grabber);
            if (Grabs.TryGetValue(grabber, out GrabState state))
            {
                if (state.Bone != null) state.Bone.ReleaseGrab(false);
                Grabs.Remove(grabber);
            }
        }

        /// <summary>Is any grabber holding a Motion Bone on this avatar? (for UI / debugging)</summary>
        public static bool IsAnythingGrabbed(Transform avatarRoot)
        {
            foreach (GrabState state in Grabs.Values)
            {
                if (state.Bone != null && state.Bone.OwnerRoot == avatarRoot) return true;
            }
            return false;
        }

        internal static void Simulate(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            ActiveGlobals.Clear();
            for (int i = AllColliders.Count - 1; i >= 0; i--)
            {
                ParelMotionBoneCollider collider = AllColliders[i];
                if (collider == null)
                {
                    AllColliders.RemoveAt(i);
                    continue;
                }
                if (!collider.isActiveAndEnabled) continue;
                if (collider.UpdateWorld() && collider.IsGlobal) ActiveGlobals.Add(collider);
            }

            Vector3 viewer = default;
            bool cull = CullDistance > 0f && TryGetViewer(out viewer);
            float cullSq = CullDistance * CullDistance;
            int budget = MaxSimulatedParticles;
            int skipped = 0;
            Ready.Clear();
            for (int i = 0; i < Bones.Count; i++)
            {
                ParelMotionBone bone = Bones[i];
                if (bone == null)
                {
                    Bones.RemoveAt(i--);
                    continue;
                }
                if (!bone.isActiveAndEnabled) continue;

                if (bone.ParticleCount > budget || (cull && (bone.Root.position - viewer).sqrMagnitude > cullSq))
                {
                    bone.Suspend();
                    skipped++;
                    continue;
                }

                try
                {
                    if (!bone.PrepareFrame()) continue;
                }
                catch (Exception e)
                {
                    Report(bone, e);
                    continue;
                }
                budget -= bone.ParticleCount;
                Ready.Add(bone);
            }

            UpdateGrabs();

            int particles = 0;
            for (int i = 0; i < Ready.Count; i++)
            {
                ParelMotionBone bone = Ready[i];
                if (bone == null || !bone.isActiveAndEnabled) continue;
                CollectColliders(bone);
                try
                {
                    bone.Simulate(deltaTime, FrameColliders);
                }
                catch (Exception e)
                {
                    Report(bone, e);
                    continue;
                }
                particles += bone.ParticleCount;
            }

            FrameColliders.Clear();
            SimulatedLastFrame = Ready.Count;
            SkippedLastFrame = skipped;
            SimulatedParticlesLastFrame = particles;
        }

        /// <summary>One broken Motion Bone must not stop everyone else's: log it, rebuild it, give up after repeats.</summary>
        private static void Report(ParelMotionBone bone, Exception e)
        {
            Debug.LogException(e, bone);
            if (bone.Fault())
            {
                Debug.LogError($"[ParelVR] Motion Bone on '{bone.name}' stopped after repeated errors. It starts again when re-enabled or rebuilt.", bone);
            }
        }

        private static bool TryGetViewer(out Vector3 position)
        {
            Transform viewer = Viewer;
            if (viewer == null)
            {
                Camera main = Camera.main;
                viewer = main != null ? main.transform : null;
            }
            position = viewer != null ? viewer.position : default;
            return viewer != null;
        }

        /// <summary>
        /// This chain's own colliders plus the nearby global ones it allows, checked once per frame so the
        /// per-bone, per-step collision loop doesn't repeat activity and permission checks.
        /// </summary>
        private static void CollectColliders(ParelMotionBone bone)
        {
            FrameColliders.Clear();
            List<ParelMotionBoneCollider> own = bone.colliders;
            if (own != null)
            {
                for (int i = 0; i < own.Count; i++)
                {
                    ParelMotionBoneCollider collider = own[i];
                    if (collider != null && collider.isActiveAndEnabled && collider.IsValid) FrameColliders.Add(collider);
                }
            }

            if (bone.allowCollision == ParelDynamicsPermission.Nobody || ActiveGlobals.Count == 0) return;
            Bounds bounds = bone.WorldBounds;
            bounds.Expand(0.3f);
            Transform owner = bone.OwnerRoot;
            for (int i = 0; i < ActiveGlobals.Count; i++)
            {
                ParelMotionBoneCollider collider = ActiveGlobals[i];
                float reach = collider.WorldRadius * collider.WorldRadius + 0.0001f;
                if (bounds.SqrDistance(collider.WorldA) > reach && bounds.SqrDistance(collider.WorldB) > reach) continue;
                if (!ParelDynamicsMath.Allows(bone.allowCollision, owner, collider.OwnerRoot)) continue;
                FrameColliders.Add(collider);
            }
        }

        private static void UpdateGrabs()
        {
            for (int g = Grabbers.Count - 1; g >= 0; g--)
            {
                IParelMotionBoneGrabber grabber = Grabbers[g];
                if (grabber == null || !grabber.IsValid)
                {
                    if (grabber != null && Grabs.TryGetValue(grabber, out GrabState dead))
                    {
                        if (dead.Bone != null) dead.Bone.ReleaseGrab(false);
                        Grabs.Remove(grabber);
                    }
                    Grabbers.RemoveAt(g);
                    continue;
                }

                bool holding = Grabs.TryGetValue(grabber, out GrabState state) && state.Bone != null && state.Bone.isActiveAndEnabled && !state.Bone.IsSuspended;
                if (holding)
                {
                    if (grabber.IsGrabbing)
                    {
                        state.Bone.SetGrabTarget(grabber.Position + state.Offset);
                    }
                    else
                    {
                        bool pose = grabber.IsPosing && ParelDynamicsMath.Allows(state.Bone.allowPosing, state.Bone.OwnerRoot, grabber.OwnerRoot);
                        state.Bone.ReleaseGrab(pose);
                        Grabs.Remove(grabber);
                    }
                    continue;
                }

                if (state != null) Grabs.Remove(grabber);
                if (!grabber.IsGrabbing) continue;

                ParelMotionBone best = null;
                int bestParticle = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < Ready.Count; i++)
                {
                    ParelMotionBone bone = Ready[i];
                    if (bone == null || !bone.isActiveAndEnabled || bone.IsGrabbed) continue;
                    if (!ParelDynamicsMath.Allows(bone.allowGrabbing, bone.OwnerRoot, grabber.OwnerRoot)) continue;
                    int particle = bone.FindGrabbable(grabber.Position, grabber.Radius, out float distance);
                    if (particle > 0 && distance < bestDistance)
                    {
                        best = bone;
                        bestParticle = particle;
                        bestDistance = distance;
                    }
                }

                if (best == null) continue;
                best.BeginGrab(bestParticle);
                Vector3 offset = best.snapToHand ? Vector3.zero : best.ParticlePosition(bestParticle) - grabber.Position;
                Grabs[grabber] = new GrabState { Bone = best, Particle = bestParticle, Offset = offset };
            }
        }
    }
}
