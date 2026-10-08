using System.Collections.Generic;
using System.Linq;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Validation
{
    /// <summary>
    /// Everything the Builder checks before an avatar can be built -- the ParelVR take on the VRChat
    /// SDK's validation list. Errors block the build; warnings and info are shown next to it, many
    /// with an Auto Fix.
    /// </summary>
    public static class ParelAvatarDescriptorValidator
    {
        public static ValidationReport Validate(ParelAvatarDescriptor descriptor)
        {
            var report = new ValidationReport();
            if (descriptor == null)
            {
                report.AddError("No avatar selected.");
                return report;
            }

            GameObject root = descriptor.gameObject;
            if (!root.activeInHierarchy)
            {
                report.AddWarning($"'{root.name}' is disabled in the scene -- it will upload, but you can't preview it.", () =>
                {
                    Undo.RecordObject(root, "Enable Avatar");
                    root.SetActive(true);
                });
            }

            CheckRig(descriptor, report);
            CheckView(descriptor, report);
            CheckLipSync(descriptor, report);
            CheckComponents(root, report);
            CheckExpressions(descriptor, report);
            CheckLayers(descriptor, report);
            CheckDynamics(root, report);
            CheckScale(root, report);
            CheckEyeLook(descriptor, report);
            CheckStations(root, report);
            ParelAvatarToolsValidator.Check(descriptor, report);
            CheckPerformance(root, report);
            return report;
        }

        private static void CheckStations(GameObject root, ValidationReport report)
        {
            foreach (ParelStation station in root.GetComponentsInChildren<ParelStation>(true))
            {
                if (station.GetComponent<Collider>() != null) continue;
                report.AddWarning($"Station '{station.name}' has no Collider, so nobody can point at it to sit.", () =>
                {
                    var box = Undo.AddComponent<BoxCollider>(station.gameObject);
                    box.isTrigger = true;
                    box.size = new Vector3(0.5f, 0.5f, 0.5f);
                }, station);
            }
        }

        private static void CheckEyeLook(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            if (!descriptor.EnableEyeLook) return;
            ParelAvatarDescriptor.CustomEyeLookSettings s = descriptor.EyeLookSettings;
            if (s.leftEye == null && s.rightEye == null)
            {
                report.AddWarning("Eye Look is on but no eye bones are set, so the eyes won't move. Set them under Eye Look > Eyes > Transforms.", null, descriptor);
            }
            switch (s.eyelidType)
            {
                case ParelAvatarDescriptor.EyelidType.Blendshapes:
                    if (s.eyelidsSkinnedMesh == null || s.eyelidsBlendshapes == null || s.eyelidsBlendshapes.Length == 0 || s.eyelidsBlendshapes[0] < 0)
                    {
                        report.AddWarning("Eyelid Type is Blendshapes but no Blink blend shape is picked, so the avatar won't blink.", () =>
                        {
                            Undo.RecordObject(descriptor, "Auto Detect Blink");
                            descriptor.AutoDetectBlink(true);
                            EditorUtility.SetDirty(descriptor);
                        }, descriptor);
                    }
                    break;
                case ParelAvatarDescriptor.EyelidType.Bones:
                    if (s.upperLeftEyelid == null && s.upperRightEyelid == null)
                    {
                        report.AddWarning("Eyelid Type is Bones but no eyelid bones are set, so the avatar won't blink.", null, descriptor);
                    }
                    break;
            }
        }

        private static void CheckRig(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            Animator animator = descriptor.AvatarAnimator;
            if (animator == null)
            {
                Animator found = descriptor.GetComponentInChildren<Animator>(true);
                report.AddError("The Avatar Descriptor has no Animator assigned.", found == null ? (System.Action)null : () =>
                {
                    Undo.RecordObject(descriptor, "Assign Animator");
                    descriptor.AvatarAnimator = found;
                    EditorUtility.SetDirty(descriptor);
                });
                return;
            }

            if (animator.avatar == null || !animator.isHuman)
            {
                report.AddError("The avatar's rig isn't Humanoid. Select the model, set Rig > Animation Type to Humanoid, and apply.");
                return;
            }

            if (animator.runtimeAnimatorController != null)
            {
                report.AddWarning("The root Animator has a controller assigned. ParelVR supplies the locomotion controller itself, so it's ignored -- put toggles and other animation in the FX layer instead.", () =>
                {
                    Undo.RecordObject(animator, "Clear Animator Controller");
                    if (descriptor.FxController == null)
                    {
                        Undo.RecordObject(descriptor, "Move Controller to FX");
                        descriptor.FxController = animator.runtimeAnimatorController;
                        EditorUtility.SetDirty(descriptor);
                    }
                    animator.runtimeAnimatorController = null;
                    EditorUtility.SetDirty(animator);
                });
            }
        }

        private static void CheckView(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            if (!descriptor.ViewPointInitialized)
            {
                report.AddWarning("The View Point hasn't been set -- your first-person camera may sit in the wrong place.", () =>
                {
                    Undo.RecordObject(descriptor, "Auto Detect View Point");
                    descriptor.AutoDetectViewPoint();
                    EditorUtility.SetDirty(descriptor);
                });
            }
            else if (descriptor.ViewPoint.y < 0.1f)
            {
                report.AddWarning("The View Point is at the avatar's feet. Move it between the eyes.", () =>
                {
                    Undo.RecordObject(descriptor, "Auto Detect View Point");
                    descriptor.AutoDetectViewPoint();
                    EditorUtility.SetDirty(descriptor);
                });
            }
        }

        private static void CheckLipSync(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            switch (descriptor.LipSync)
            {
                case ParelAvatarDescriptor.LipSyncStyle.Default:
                    if (ParelVisemeAutoMapper.FindFaceMesh(descriptor.gameObject) == null)
                    {
                        report.AddInfo("No viseme blend shapes were found, so this avatar's mouth won't move when you talk.");
                    }
                    break;
                case ParelAvatarDescriptor.LipSyncStyle.JawFlapBone:
                    if (descriptor.JawBone == null) report.AddWarning("Lip sync is set to Jaw Flap Bone but no jaw bone is assigned, so the mouth will not move.");
                    break;
                case ParelAvatarDescriptor.LipSyncStyle.JawFlapBlendShape:
                    if (descriptor.JawFlapMesh == null || ParelVisemeAutoMapper.BlendShapeIndex(descriptor.JawFlapMesh, descriptor.JawFlapBlendShape) < 0)
                    {
                        report.AddWarning("Lip sync is set to Jaw Flap Blend Shape but the mesh or blend shape is not set, so the mouth will not move.");
                    }
                    break;
                case ParelAvatarDescriptor.LipSyncStyle.VisemeBlendShape:
                    if (descriptor.VisemeSkinnedMesh == null)
                    {
                        report.AddWarning("Lip sync is set to Viseme Blend Shape but no face mesh is assigned.", () => AutoLipSync(descriptor));
                    }
                    else
                    {
                        string[] names = descriptor.VisemeBlendShapes;
                        int missing = 0;
                        for (int i = 10; i < 15; i++)
                        {
                            if (ParelVisemeAutoMapper.BlendShapeIndex(descriptor.VisemeSkinnedMesh, names[i]) < 0) missing++;
                        }
                        if (missing > 0)
                        {
                            report.AddWarning($"{missing} of the 5 vowel visemes (aa, E, ih, oh, ou) aren't mapped to a blend shape.", () => AutoLipSync(descriptor));
                        }
                    }
                    break;
            }
        }

        private static void AutoLipSync(ParelAvatarDescriptor descriptor)
        {
            Undo.RecordObject(descriptor, "Auto Detect Lip Sync");
            descriptor.AutoDetectLipSync(true);
            descriptor.AutoDetectBlink(false);
            EditorUtility.SetDirty(descriptor);
        }

        private static void CheckComponents(GameObject root, ValidationReport report)
        {
            var disallowed = new List<Component>();
            var missing = new List<GameObject>();
            ParelAvatarComponentPolicy.FindDisallowed(root, disallowed, missing);

            if (disallowed.Count > 0)
            {
                string types = string.Join(", ", disallowed.Select(c => c.GetType().Name).Distinct().Take(8));
                report.AddWarning($"{disallowed.Count} component(s) aren't allowed on avatars and will be removed from the upload: {types}", () =>
                {
                    foreach (Component c in disallowed)
                    {
                        if (c != null) Undo.DestroyObjectImmediate(c);
                    }
                });
            }

            if (missing.Count > 0)
            {
                report.AddWarning($"{missing.Count} object(s) have missing scripts. They're removed from the upload.", () =>
                {
                    foreach (GameObject go in missing)
                    {
                        if (go != null)
                        {
                            Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");
                            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                        }
                    }
                });
            }
        }

        private static void CheckExpressions(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            ParelExpressionsMenu menu = descriptor.ExpressionsMenu;
            ParelExpressionParameters parameters = descriptor.ExpressionParameters;

            if (parameters != null)
            {
                int cost = parameters.CalcTotalCost();
                if (cost > ParelExpressionParameters.MaxSyncedBits)
                {
                    report.AddError($"Synced Expression Parameters use {cost} of {ParelExpressionParameters.MaxSyncedBits} bits. Remove some or untick Synced on the ones others don't need to see.");
                }

                var seen = new HashSet<string>();
                foreach (ParelExpressionParameters.Parameter p in parameters.parameters)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.name)) continue;
                    if (!seen.Add(p.name)) report.AddError($"Expression Parameter '{p.name}' is defined more than once.");
                }
            }

            if (menu != null)
            {
                if (parameters == null)
                {
                    report.AddError("An Expressions Menu is assigned but no Expression Parameters. Every menu control needs its parameter defined there.");
                }

                var visited = new HashSet<ParelExpressionsMenu>();
                CheckMenu(menu, parameters, report, visited, menu.name);
            }

            AnimatorController fx = descriptor.FxController as AnimatorController;
            if (fx != null && parameters != null)
            {
                foreach (ParelExpressionParameters.Parameter p in parameters.parameters)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.name)) continue;
                    AnimatorControllerParameter ap = fx.parameters.FirstOrDefault(x => x.name == p.name);
                    if (ap == null)
                    {
                        report.AddInfo($"Parameter '{p.name}' isn't used by the FX controller.");
                        continue;
                    }
                    bool compatible = (p.valueType == ParelExpressionParameters.ValueType.Bool && (ap.type == AnimatorControllerParameterType.Bool || ap.type == AnimatorControllerParameterType.Trigger))
                        || (p.valueType == ParelExpressionParameters.ValueType.Int && ap.type == AnimatorControllerParameterType.Int)
                        || (p.valueType == ParelExpressionParameters.ValueType.Float && ap.type == AnimatorControllerParameterType.Float);
                    if (!compatible)
                    {
                        report.AddWarning($"Parameter '{p.name}' is a {p.valueType} in Expression Parameters but a {ap.type} in the FX controller. It will be converted, but matching types avoids surprises.");
                    }
                }
            }
        }

        private static readonly ParelAvatarLayer[] AllLayers =
        {
            ParelAvatarLayer.Base, ParelAvatarLayer.Additive, ParelAvatarLayer.Gesture, ParelAvatarLayer.Action,
            ParelAvatarLayer.FX, ParelAvatarLayer.Sitting, ParelAvatarLayer.TPose, ParelAvatarLayer.IKPose,
        };

        private static void CheckLayers(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            // ParelVR's IK runs inside the Base layer's IK pass: without it, tracking does not move the body.
            if (descriptor.BaseLayer is AnimatorController baseController && baseController.layers.Length > 0 && !baseController.layers[0].iKPass)
            {
                report.AddError("The Base layer's first animator layer needs IK Pass switched on, or head and hand tracking will not move the avatar.",
                    () => global::ParelVR.SDK.Avatars.Inspectors.ParelAvatarDescriptorEditor.EnableIkPass(baseController));
            }

            foreach (ParelAvatarLayer layer in AllLayers)
            {
                RuntimeAnimatorController assigned = descriptor.GetLayer(layer);
                if (assigned == null) continue;
                if (!(assigned is AnimatorController controller))
                {
                    report.AddWarning($"The {layer} layer uses an Animator Override Controller. Use a plain Animator Controller so its parameters and behaviours can be checked.");
                    continue;
                }

                int foreign = 0;
                foreach (StateMachineBehaviour behaviour in controller.GetBehaviours<StateMachineBehaviour>())
                {
                    if (behaviour == null || behaviour.GetType().Namespace != "ParelVR.AvatarSDK") foreign++;
                }
                if (foreign > 0)
                {
                    report.AddWarning($"The {layer} layer has {foreign} state behaviour(s) that are not from the ParelVR Avatar SDK (or are missing). They do nothing in ParelVR.");
                }
            }

            if (descriptor.AdditiveLayer != null)
            {
                report.AddInfo("The Additive layer is blended as a normal layer on top of locomotion in ParelVR; keep its animations small (breathing, sway).");
            }
        }

        private static void CheckDynamics(GameObject root, ValidationReport report)
        {
            foreach (ParelPhysBone physBone in root.GetComponentsInChildren<ParelPhysBone>(true))
            {
                Transform chain = physBone.Root;
                if (chain == null) continue;
                int count = chain.GetComponentsInChildren<Transform>(true).Length;
                if (count > ParelPhysBone.MaxParticles)
                {
                    report.AddWarning($"PhysBone on '{physBone.name}' covers {count} bones; only the first {ParelPhysBone.MaxParticles} are simulated. Split it into several PhysBones.");
                }
                if (chain.childCount == 0 && physBone.endpointPosition == Vector3.zero)
                {
                    report.AddWarning($"PhysBone on '{physBone.name}' starts at a bone with no children and has no Endpoint Position, so nothing will move.");
                }
            }

            foreach (ParelContactReceiver receiver in root.GetComponentsInChildren<ParelContactReceiver>(true))
            {
                if (string.IsNullOrEmpty(receiver.parameter)) report.AddWarning($"Contact Receiver on '{receiver.name}' has no parameter, so it does nothing.");
                if (receiver.collisionTags == null || receiver.collisionTags.Count == 0) report.AddWarning($"Contact Receiver on '{receiver.name}' has no collision tags, so nothing can touch it.");
            }
        }

        private static void CheckMenu(ParelExpressionsMenu menu, ParelExpressionParameters parameters, ValidationReport report, HashSet<ParelExpressionsMenu> visited, string path)
        {
            if (menu == null || !visited.Add(menu)) return;
            if (menu.controls == null) return;

            if (menu.controls.Count > ParelExpressionsMenu.MaxControls)
            {
                report.AddError($"Menu '{path}' has {menu.controls.Count} controls; the most a page can hold is {ParelExpressionsMenu.MaxControls}. Move some into a Sub Menu.");
            }

            foreach (ParelExpressionsMenu.Control c in menu.controls)
            {
                if (c == null) continue;
                CheckParam(c.parameter?.name, parameters, report, path, c.name);
                int subCount = ParelExpressionsMenu.Control.SubParameterCount(c.type);
                for (int i = 0; i < subCount; i++) CheckParam(c.GetSubParameter(i), parameters, report, path, c.name);

                if (c.type == ParelExpressionsMenu.ControlType.SubMenu)
                {
                    if (c.subMenu == null) report.AddWarning($"Sub Menu '{c.name}' in '{path}' has no menu assigned.");
                    else CheckMenu(c.subMenu, parameters, report, visited, path + " > " + c.name);
                }

                if (c.type == ParelExpressionsMenu.ControlType.RadialPuppet && c.GetSubParameter(0) == null)
                {
                    report.AddWarning($"Radial Puppet '{c.name}' in '{path}' has no Rotation parameter.");
                }
            }
        }

        private static void CheckParam(string parameterName, ParelExpressionParameters parameters, ValidationReport report, string path, string controlName)
        {
            if (string.IsNullOrEmpty(parameterName) || parameters == null) return;
            if (parameters.FindParameter(parameterName) == null)
            {
                report.AddError($"'{controlName}' in menu '{path}' uses parameter '{parameterName}', which isn't in Expression Parameters.");
            }
        }

        private static void CheckScale(GameObject root, ValidationReport report)
        {
            Vector3 s = root.transform.localScale;
            if (Mathf.Abs(s.x - s.y) > 0.0001f || Mathf.Abs(s.y - s.z) > 0.0001f)
            {
                report.AddWarning("The avatar root is scaled unevenly. Use a uniform scale (or scale the model in its import settings).");
            }
        }

        private static void CheckPerformance(GameObject root, ValidationReport report)
        {
            ParelAvatarPerformance perf = ParelAvatarPerformance.Measure(root);
            ParelAvatarPerformance.Rank rank = perf.OverallRank;
            string summary = $"Performance: {ParelAvatarPerformance.RankLabel(rank)} on PC, {ParelAvatarPerformance.RankLabel(perf.MobileRank)} on Quest -- {perf.Triangles:N0} triangles, {perf.MaterialSlots} material slots, {perf.SkinnedMeshes} skinned meshes, {perf.Bones} bones, {perf.PhysBones} PhysBones ({perf.PhysBoneTransforms} bones), {perf.Contacts} contacts.";
            if (rank == ParelAvatarPerformance.Rank.VeryPoor) report.AddWarning(summary + " Very Poor avatars may be hidden by other players' performance settings.");
            else report.AddInfo(summary);
        }
    }
}
