using System.Collections.Generic;
using ParelVR.AvatarSDK;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Validation
{
    /// <summary>
    /// Avatar performance stats and rank, using VRChat's PC thresholds so creators get the same
    /// Excellent / Good / Medium / Poor / Very Poor picture they're used to. Sent with the upload and
    /// shown on the avatar in game.
    /// </summary>
    public sealed class ParelAvatarPerformance
    {
        public enum Rank
        {
            Excellent,
            Good,
            Medium,
            Poor,
            VeryPoor,
        }

        public int Triangles;
        public int MaterialSlots;
        public int SkinnedMeshes;
        public int Meshes;
        public int Bones;
        public int ParticleSystems;
        public int Lights;
        public int AudioSources;
        public int PhysBones;
        public int PhysBoneTransforms;
        public int PhysBoneColliders;
        public int Contacts;

        private static readonly int[] TriangleLimits = { 32000, 70000, 70000, 70000 };
        private static readonly int[] MaterialLimits = { 4, 8, 16, 32 };
        private static readonly int[] SkinnedMeshLimits = { 1, 2, 8, 16 };
        private static readonly int[] MeshLimits = { 4, 8, 16, 24 };
        private static readonly int[] BoneLimits = { 75, 150, 256, 400 };
        private static readonly int[] ParticleLimits = { 0, 4, 8, 16 };
        private static readonly int[] LightLimits = { 0, 0, 0, 1 };
        private static readonly int[] PhysBoneLimits = { 4, 8, 16, 32 };
        private static readonly int[] PhysBoneTransformLimits = { 16, 64, 128, 256 };
        private static readonly int[] PhysBoneColliderLimits = { 4, 8, 16, 32 };
        private static readonly int[] ContactLimits = { 8, 16, 24, 32 };

        // Quest / Android (VRChat's mobile thresholds).
        private static readonly int[] MobileTriangleLimits = { 7500, 10000, 15000, 20000 };
        private static readonly int[] MobileMaterialLimits = { 1, 1, 2, 4 };
        private static readonly int[] MobileSkinnedMeshLimits = { 1, 1, 2, 2 };
        private static readonly int[] MobileMeshLimits = { 1, 1, 2, 2 };
        private static readonly int[] MobileBoneLimits = { 75, 90, 150, 150 };
        private static readonly int[] MobileParticleLimits = { 0, 0, 0, 2 };
        private static readonly int[] MobilePhysBoneLimits = { 0, 4, 6, 8 };
        private static readonly int[] MobilePhysBoneTransformLimits = { 0, 16, 32, 64 };
        private static readonly int[] MobilePhysBoneColliderLimits = { 0, 4, 8, 16 };
        private static readonly int[] MobileContactLimits = { 2, 4, 8, 16 };

        public static ParelAvatarPerformance Measure(GameObject root)
        {
            var stats = new ParelAvatarPerformance();
            if (root == null) return stats;

            var bones = new HashSet<Transform>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer smr)
                {
                    stats.SkinnedMeshes++;
                    mesh = smr.sharedMesh;
                    if (smr.bones != null)
                    {
                        foreach (Transform b in smr.bones)
                        {
                            if (b != null) bones.Add(b);
                        }
                    }
                }
                else if (renderer is MeshRenderer)
                {
                    stats.Meshes++;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }
                else
                {
                    continue;
                }

                stats.MaterialSlots += renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
                if (mesh == null) continue;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) == MeshTopology.Triangles) stats.Triangles += (int)(mesh.GetIndexCount(s) / 3);
                }
            }

            stats.Bones = bones.Count;
            stats.ParticleSystems = root.GetComponentsInChildren<ParticleSystem>(true).Length;
            stats.Lights = root.GetComponentsInChildren<Light>(true).Length;
            stats.AudioSources = root.GetComponentsInChildren<AudioSource>(true).Length;

            ParelPhysBone[] physBones = root.GetComponentsInChildren<ParelPhysBone>(true);
            stats.PhysBones = physBones.Length;
            foreach (ParelPhysBone physBone in physBones)
            {
                Transform chain = physBone.Root;
                if (chain != null) stats.PhysBoneTransforms += chain.GetComponentsInChildren<Transform>(true).Length;
            }
            stats.PhysBoneColliders = root.GetComponentsInChildren<ParelPhysBoneCollider>(true).Length;
            stats.Contacts = root.GetComponentsInChildren<ParelContactBase>(true).Length;
            return stats;
        }

        public Rank OverallRank
        {
            get
            {
                Rank worst = Rank.Excellent;
                worst = Max(worst, RankOf(Triangles, TriangleLimits));
                worst = Max(worst, RankOf(MaterialSlots, MaterialLimits));
                worst = Max(worst, RankOf(SkinnedMeshes, SkinnedMeshLimits));
                worst = Max(worst, RankOf(Meshes, MeshLimits));
                worst = Max(worst, RankOf(Bones, BoneLimits));
                worst = Max(worst, RankOf(ParticleSystems, ParticleLimits));
                worst = Max(worst, RankOf(Lights, LightLimits));
                worst = Max(worst, RankOf(PhysBones, PhysBoneLimits));
                worst = Max(worst, RankOf(PhysBoneTransforms, PhysBoneTransformLimits));
                worst = Max(worst, RankOf(PhysBoneColliders, PhysBoneColliderLimits));
                worst = Max(worst, RankOf(Contacts, ContactLimits));
                return worst;
            }
        }

        /// <summary>The rank on Quest / Android, which has much tighter limits.</summary>
        public Rank MobileRank
        {
            get
            {
                Rank worst = Rank.Excellent;
                worst = Max(worst, RankOf(Triangles, MobileTriangleLimits));
                worst = Max(worst, RankOf(MaterialSlots, MobileMaterialLimits));
                worst = Max(worst, RankOf(SkinnedMeshes, MobileSkinnedMeshLimits));
                worst = Max(worst, RankOf(Meshes, MobileMeshLimits));
                worst = Max(worst, RankOf(Bones, MobileBoneLimits));
                worst = Max(worst, RankOf(ParticleSystems, MobileParticleLimits));
                worst = Max(worst, RankOf(Lights, LightLimits));
                worst = Max(worst, RankOf(PhysBones, MobilePhysBoneLimits));
                worst = Max(worst, RankOf(PhysBoneTransforms, MobilePhysBoneTransformLimits));
                worst = Max(worst, RankOf(PhysBoneColliders, MobilePhysBoneColliderLimits));
                worst = Max(worst, RankOf(Contacts, MobileContactLimits));
                return worst;
            }
        }

        public static string RankLabel(Rank rank)
        {
            switch (rank)
            {
                case Rank.Excellent: return "Excellent";
                case Rank.Good: return "Good";
                case Rank.Medium: return "Medium";
                case Rank.Poor: return "Poor";
                default: return "Very Poor";
            }
        }

        private static Rank RankOf(int value, int[] limits)
        {
            for (int i = 0; i < limits.Length; i++)
            {
                if (value <= limits[i]) return (Rank)i;
            }
            return Rank.VeryPoor;
        }

        private static Rank Max(Rank a, Rank b) => (int)a >= (int)b ? a : b;
    }
}
