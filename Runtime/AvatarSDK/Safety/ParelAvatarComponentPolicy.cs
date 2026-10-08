using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// The one list of components an uploaded avatar may contain -- the SDK refuses to build an
    /// avatar that carries anything else, and the game strips anything else again when it loads a
    /// bundle (never trust a bundle just because the official SDK is supposed to have built it).
    /// Same idea as VRChat's avatar component whitelist: rendering, animation, physics, constraints,
    /// particles, audio, plus ParelVR's own avatar components and UniVRM's runtime components
    /// (spring bones, expressions) so VRM-based avatars keep working.
    /// </summary>
    public static class ParelAvatarComponentPolicy
    {
        private static readonly HashSet<Type> AllowedTypes = new HashSet<Type>
        {
            typeof(Transform),
            typeof(Animator),
            typeof(SkinnedMeshRenderer),
            typeof(MeshFilter),
            typeof(MeshRenderer),
            typeof(ParticleSystem),
            typeof(ParticleSystemRenderer),
            typeof(TrailRenderer),
            typeof(LineRenderer),
            typeof(Light),
            typeof(AudioSource),
            typeof(Cloth),
            typeof(Rigidbody),
            typeof(CharacterJoint),
            typeof(ConfigurableJoint),
            typeof(FixedJoint),
            typeof(HingeJoint),
            typeof(SpringJoint),
            typeof(BoxCollider),
            typeof(SphereCollider),
            typeof(CapsuleCollider),
            typeof(AimConstraint),
            typeof(LookAtConstraint),
            typeof(ParentConstraint),
            typeof(PositionConstraint),
            typeof(RotationConstraint),
            typeof(ScaleConstraint),
            typeof(LODGroup),
        };

        // Matched against Type.Namespace (exact, or as a "Prefix." parent namespace).
        private static readonly string[] AllowedNamespaces =
        {
            "ParelVR.AvatarSDK",
            "UniVRM10",
            "VRM",
        };

        public static bool IsAllowed(Type type)
        {
            if (type == null) return false;
            if (AllowedTypes.Contains(type)) return true;

            string ns = type.Namespace;
            if (string.IsNullOrEmpty(ns)) return false;
            for (int i = 0; i < AllowedNamespaces.Length; i++)
            {
                string allowed = AllowedNamespaces[i];
                if (ns == allowed || ns.StartsWith(allowed + ".", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Every component on the avatar that isn't allowed, plus missing-script slots (null).</summary>
        public static void FindDisallowed(GameObject root, List<Component> disallowed, List<GameObject> withMissingScripts)
        {
            if (root == null) return;
            var buffer = new List<Component>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                buffer.Clear();
                t.GetComponents(buffer);
                for (int i = 0; i < buffer.Count; i++)
                {
                    Component c = buffer[i];
                    if (c == null)
                    {
                        if (withMissingScripts != null && !withMissingScripts.Contains(t.gameObject)) withMissingScripts.Add(t.gameObject);
                        continue;
                    }
                    if (!IsAllowed(c.GetType())) disallowed?.Add(c);
                }
            }
        }

        /// <summary>
        /// Removes every disallowed component. Runs several passes because Unity refuses to remove a
        /// component another one depends on ([RequireComponent]) until the dependent is gone first.
        /// Returns the type names that were removed, for logging.
        /// </summary>
        public static List<string> RemoveDisallowed(GameObject root)
        {
            var removed = new List<string>();
            if (root == null) return removed;

            var disallowed = new List<Component>();
            for (int pass = 0; pass < 4; pass++)
            {
                disallowed.Clear();
                FindDisallowed(root, disallowed, null);
                if (disallowed.Count == 0) break;

                // Dependents first: walk backwards so later-added components (usually the ones that
                // [RequireComponent] earlier ones) go before what they depend on.
                for (int i = disallowed.Count - 1; i >= 0; i--)
                {
                    Component c = disallowed[i];
                    if (c == null) continue;
                    string name = c.GetType().FullName;
                    try
                    {
                        UnityEngine.Object.DestroyImmediate(c);
                        if (c == null) removed.Add(name);
                    }
                    catch (Exception)
                    {
                        // Still required by something removed later this pass; the next pass gets it.
                    }
                }
            }
            return removed;
        }
    }
}
