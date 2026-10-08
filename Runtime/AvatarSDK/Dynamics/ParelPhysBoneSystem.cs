using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// A hand (or anything else) that can grab PhysBones. The game registers one per hand of every
    /// player -- remote players included, so everyone sees the same grabs.
    /// </summary>
    public interface IParelPhysBoneGrabber
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
    /// Runs every PhysBone once per frame after animation, IK and networking have placed the bones
    /// (see <see cref="ParelAvatarDynamicsRunner"/>), handles grabbing / posing, and keeps the list
    /// of "global" colliders (everyone's hands and fingers).
    /// </summary>
    public static class ParelPhysBoneSystem
    {
        private sealed class GrabState
        {
            public ParelPhysBone Bone;
            public int Particle;
            public Vector3 Offset;
        }

        private static readonly List<ParelPhysBone> Bones = new List<ParelPhysBone>();
        private static readonly List<ParelPhysBoneCollider> AllColliders = new List<ParelPhysBoneCollider>();
        private static readonly List<ParelPhysBoneCollider> GlobalColliders = new List<ParelPhysBoneCollider>();
        private static readonly List<ParelPhysBoneCollider> NearbyGlobals = new List<ParelPhysBoneCollider>();
        private static readonly List<IParelPhysBoneGrabber> Grabbers = new List<IParelPhysBoneGrabber>();
        private static readonly Dictionary<IParelPhysBoneGrabber, GrabState> Grabs = new Dictionary<IParelPhysBoneGrabber, GrabState>();

        internal static readonly List<int> PathBuffer = new List<int>(64);

        /// <summary>Hard cap on simulated bones (all avatars together) to keep frame time bounded.</summary>
        public static int MaxSimulatedParticles = 12000;

        public static int ActiveCount => Bones.Count;

        public static void Register(ParelPhysBone bone)
        {
            if (bone == null || Bones.Contains(bone)) return;
            Bones.Add(bone);
            ParelAvatarDynamicsRunner.EnsureRunning();
        }

        public static void Unregister(ParelPhysBone bone)
        {
            Bones.Remove(bone);
            foreach (KeyValuePair<IParelPhysBoneGrabber, GrabState> pair in Grabs)
            {
                if (pair.Value.Bone == bone)
                {
                    Grabs.Remove(pair.Key);
                    break;
                }
            }
        }

        internal static void RegisterCollider(ParelPhysBoneCollider collider)
        {
            if (collider != null && !AllColliders.Contains(collider)) AllColliders.Add(collider);
        }

        internal static void UnregisterCollider(ParelPhysBoneCollider collider)
        {
            AllColliders.Remove(collider);
            GlobalColliders.Remove(collider);
        }

        /// <summary>Makes a collider bump every avatar's PhysBones (subject to their Allow Collision).</summary>
        public static void RegisterGlobalCollider(ParelPhysBoneCollider collider)
        {
            if (collider == null) return;
            RegisterCollider(collider);
            if (!GlobalColliders.Contains(collider)) GlobalColliders.Add(collider);
        }

        public static void UnregisterGlobalCollider(ParelPhysBoneCollider collider) => GlobalColliders.Remove(collider);

        public static void RegisterGrabber(IParelPhysBoneGrabber grabber)
        {
            if (grabber != null && !Grabbers.Contains(grabber)) Grabbers.Add(grabber);
            ParelAvatarDynamicsRunner.EnsureRunning();
        }

        public static void UnregisterGrabber(IParelPhysBoneGrabber grabber)
        {
            if (grabber == null) return;
            Grabbers.Remove(grabber);
            if (Grabs.TryGetValue(grabber, out GrabState state))
            {
                if (state.Bone != null) state.Bone.ReleaseGrab(false);
                Grabs.Remove(grabber);
            }
        }

        /// <summary>Is any grabber holding a PhysBone on this avatar? (for UI / debugging)</summary>
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

            for (int i = AllColliders.Count - 1; i >= 0; i--)
            {
                ParelPhysBoneCollider collider = AllColliders[i];
                if (collider == null)
                {
                    AllColliders.RemoveAt(i);
                    continue;
                }
                if (collider.isActiveAndEnabled) collider.UpdateWorld();
            }

            int budget = MaxSimulatedParticles;
            for (int i = Bones.Count - 1; i >= 0; i--)
            {
                ParelPhysBone bone = Bones[i];
                if (bone == null)
                {
                    Bones.RemoveAt(i);
                    continue;
                }
                if (!bone.isActiveAndEnabled) continue;
                bone.PrepareFrame();
            }

            UpdateGrabs();

            for (int i = 0; i < Bones.Count; i++)
            {
                ParelPhysBone bone = Bones[i];
                if (bone == null || !bone.isActiveAndEnabled) continue;
                budget -= bone.ParticleCount;
                if (budget < 0) break;

                CollectNearbyGlobals(bone.WorldBounds);
                bone.Simulate(deltaTime, NearbyGlobals);
            }
        }

        private static void CollectNearbyGlobals(Bounds bounds)
        {
            NearbyGlobals.Clear();
            bounds.Expand(0.3f);
            for (int i = GlobalColliders.Count - 1; i >= 0; i--)
            {
                ParelPhysBoneCollider collider = GlobalColliders[i];
                if (collider == null)
                {
                    GlobalColliders.RemoveAt(i);
                    continue;
                }
                if (!collider.isActiveAndEnabled) continue;
                if (bounds.SqrDistance(collider.WorldA) <= collider.WorldRadius * collider.WorldRadius + 0.0001f ||
                    bounds.SqrDistance(collider.WorldB) <= collider.WorldRadius * collider.WorldRadius + 0.0001f)
                {
                    NearbyGlobals.Add(collider);
                }
            }
        }

        private static void UpdateGrabs()
        {
            for (int g = Grabbers.Count - 1; g >= 0; g--)
            {
                IParelPhysBoneGrabber grabber = Grabbers[g];
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

                bool holding = Grabs.TryGetValue(grabber, out GrabState state) && state.Bone != null && state.Bone.isActiveAndEnabled;
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

                ParelPhysBone best = null;
                int bestParticle = -1;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < Bones.Count; i++)
                {
                    ParelPhysBone bone = Bones[i];
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
