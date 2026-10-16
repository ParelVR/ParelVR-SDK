using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Build
{
    /// <summary>
    /// Locks the avatar's Poiyomi (Thry Shader Optimizer) materials before the bundle is built.
    /// Poiyomi takes every unlocked Poiyomi shader out of any build, bundles included, so an avatar
    /// uploaded with unlocked materials can't draw them in the game and falls back to a plain shader for
    /// everybody who sees it. Locked materials carry a generated shader of their own that does ship.
    /// Poiyomi is optional: without it installed this does nothing. It is found by name, not referenced.
    /// </summary>
    internal static class ParelAvatarShaderLocker
    {
        private static readonly string[] OptimizerTypeNames = { "Thry.ThryEditor.ShaderOptimizer", "Thry.ShaderOptimizer" };

        /// <summary>Locks every unlocked Poiyomi material the avatar uses (renderers and animated material swaps).</summary>
        public static void LockFor(GameObject avatar, List<string> notes)
        {
            if (avatar == null) return;
            Type optimizer = FindOptimizer();
            if (optimizer == null) return;

            MethodInfo usesOptimizer = optimizer.GetMethod("IsShaderUsingThryOptimizer", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Shader) }, null);
            MethodInfo isLocked = optimizer.GetMethod("IsMaterialLocked", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Material) }, null);
            MethodInfo lockMaterials = FindLockMethod(optimizer);
            if (usesOptimizer == null || isLocked == null || lockMaterials == null)
            {
                Debug.LogWarning("[ParelAvatarBuilder] This Poiyomi version has no lock API ParelVR knows; lock the avatar's materials by hand (Thry > Lock All) before uploading.");
                return;
            }

            var unlocked = new List<Material>();
            var seen = new HashSet<Material>();
            // Everything the avatar references: its renderers' materials, and the materials its animations swap in.
            foreach (UnityEngine.Object dependency in EditorUtility.CollectDependencies(new UnityEngine.Object[] { avatar }))
            {
                if (!(dependency is Material material) || !seen.Add(material)) continue;
                if (material.shader == null || !AssetDatabase.Contains(material)) continue;
                try
                {
                    if ((bool)usesOptimizer.Invoke(null, new object[] { material.shader }) && !(bool)isLocked.Invoke(null, new object[] { material }))
                        unlocked.Add(material);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ParelAvatarBuilder] Could not check material '{material.name}' for locking: {Unwrap(e).Message}");
                }
            }
            if (unlocked.Count == 0) return;

            try
            {
                lockMaterials.Invoke(null, LockArguments(lockMaterials, unlocked));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ParelAvatarBuilder] Poiyomi could not lock the avatar's materials; they will not show in the game. {Unwrap(e)}");
                return;
            }
            // Poiyomi saves on a later Editor tick, which never comes before the bundle reads the assets.
            AssetDatabase.SaveAssets();

            var failed = new List<string>();
            foreach (Material material in unlocked)
            {
                if (!(bool)isLocked.Invoke(null, new object[] { material })) failed.Add(material.name);
            }

            int locked = unlocked.Count - failed.Count;
            if (locked > 0)
            {
                string line = $"Locked {locked} Poiyomi material(s) so they show in the game.";
                notes?.Add(line);
                Debug.Log("[ParelAvatarBuilder] " + line);
            }
            if (failed.Count > 0)
            {
                Debug.LogWarning($"[ParelAvatarBuilder] These Poiyomi materials could not be locked and will not show in the game: {string.Join(", ", failed)}");
            }
        }

        private static Type FindOptimizer()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (string name in OptimizerTypeNames)
                {
                    Type type = assembly.GetType(name, false);
                    if (type != null) return type;
                }
            }
            return null;
        }

        // LockMaterials(IEnumerable<Material>, ProgressBar = None) in current Poiyomi; older ones only have
        // SetLockedForAllMaterials(IEnumerable<Material>, int lockState, ...).
        private static MethodInfo FindLockMethod(Type optimizer)
        {
            MethodInfo fallback = null;
            foreach (MethodInfo method in optimizer.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0 || !parameters[0].ParameterType.IsAssignableFrom(typeof(List<Material>))) continue;
                if (method.Name == "LockMaterials") return method;
                if (method.Name == "SetLockedForAllMaterials" && parameters.Length >= 2 && parameters[1].ParameterType == typeof(int)) fallback = method;
            }
            return fallback;
        }

        private static object[] LockArguments(MethodInfo method, List<Material> materials)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var args = new object[parameters.Length];
            args[0] = materials;
            for (int i = 1; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (method.Name == "SetLockedForAllMaterials" && i == 1) args[i] = 1;   // lock
                else if (type == typeof(bool)) args[i] = false;                           // no progress bar, dialog or cancel
                else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
                else if (type.IsValueType) args[i] = Activator.CreateInstance(type);
                else args[i] = null;
            }
            return args;
        }

        private static Exception Unwrap(Exception e) => e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
    }
}
