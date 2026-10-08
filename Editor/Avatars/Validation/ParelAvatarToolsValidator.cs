using System.Collections.Generic;
using System.Linq;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Build;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Validation
{
    /// <summary>
    /// Builder checks for the Avatar Tools components (Object Toggle, Outfit Switcher, Clothing,
    /// Attach To Bone, Blend Shape Sync), so problems show up in the Validations list instead of
    /// failing half way through a build.
    /// </summary>
    public static class ParelAvatarToolsValidator
    {
        public static void Check(ParelAvatarDescriptor descriptor, ValidationReport report)
        {
            GameObject root = descriptor.gameObject;
            ParelObjectToggle[] toggles = root.GetComponentsInChildren<ParelObjectToggle>(true);
            ParelOutfitSwitcher[] switchers = root.GetComponentsInChildren<ParelOutfitSwitcher>(true);
            ParelClothing[] clothing = root.GetComponentsInChildren<ParelClothing>(true);
            ParelBoneAttachment[] attachments = root.GetComponentsInChildren<ParelBoneAttachment>(true);
            ParelBlendshapeSync[] syncs = root.GetComponentsInChildren<ParelBlendshapeSync>(true);
            if (toggles.Length + switchers.Length + clothing.Length + attachments.Length + syncs.Length == 0) return;

            if ((toggles.Length > 0 || switchers.Length > 0 || syncs.Length > 0) && descriptor.FxController != null && !(descriptor.FxController is AnimatorController))
            {
                report.AddError("Avatar Tools add layers to your FX controller, but the FX slot holds an Animator Override Controller. Use a normal Animator Controller there.");
            }

            foreach (ParelClothing item in clothing)
            {
                ParelClothingMatcher.Result match = ParelClothingMatcher.Match(descriptor, item);
                if (!match.Ok) report.AddError($"Clothing '{item.name}': {match.Error}", null, item);
            }

            Animator animator = descriptor.AvatarAnimator;
            foreach (ParelBoneAttachment attachment in attachments)
            {
                Transform bone = animator != null && animator.isHuman ? animator.GetBoneTransform(attachment.bone) : null;
                if (bone == null) report.AddError($"Attach To Bone '{attachment.name}': the avatar has no {attachment.bone} bone.", null, attachment);
            }

            foreach (ParelBlendshapeSync sync in syncs)
            {
                if (sync.source == null) report.AddError($"Blend Shape Sync '{sync.name}' has no Source mesh.", null, sync);
                else if (sync.Target == null) report.AddError($"Blend Shape Sync '{sync.name}' has no Skinned Mesh Renderer to drive.", null, sync);
                else if (ParelAvatarToolsProcessor.SyncPairs(sync).Count == 0) report.AddWarning($"Blend Shape Sync '{sync.name}': no blend shapes are shared between the two meshes.", null, sync);
            }

            foreach (ParelOutfitSwitcher switcher in switchers)
            {
                int count = switcher.outfits?.Count(o => o != null) ?? 0;
                if (count == 0) report.AddWarning($"Outfit Switcher '{switcher.name}' has no outfits yet.", null, switcher);
                else if (count == 1) report.AddInfo($"Outfit Switcher '{switcher.name}' has only one outfit.", null, switcher);
                if (count > 0 && switcher.defaultOutfit >= count)
                {
                    report.AddWarning($"Outfit Switcher '{switcher.name}': the default outfit doesn't exist.", () =>
                    {
                        Undo.RecordObject(switcher, "Fix Default Outfit");
                        switcher.defaultOutfit = 0;
                        EditorUtility.SetDirty(switcher);
                    });
                }
            }

            foreach (ParelObjectToggle toggle in toggles)
            {
                foreach (ParelToggledObject target in toggle.Targets())
                {
                    if (target.target != null && !target.target.transform.IsChildOf(root.transform))
                    {
                        report.AddError($"Object Toggle '{toggle.MenuLabel}' turns '{target.target.name}' on and off, but it isn't part of the avatar.", null, toggle);
                    }
                }
            }

            // Synced bits and parameter types.
            ParelExpressionParameters existing = descriptor.ExpressionParameters;
            int baseCost = existing != null ? existing.CalcTotalCost() : 0;
            int extra = ExtraSyncedBits(descriptor, out List<string> conflicts);
            foreach (string conflict in conflicts) report.AddError(conflict);
            if (baseCost + extra > ParelExpressionParameters.MaxSyncedBits)
            {
                report.AddError($"Your Expression Parameters ({baseCost} bits) plus Avatar Tools ({extra} bits) go over the {ParelExpressionParameters.MaxSyncedBits}-bit sync limit. Turn Synced off on some toggles, or remove parameters.");
            }

            // Room in the top page of the Expressions Menu.
            ParelExpressionsMenu menu = descriptor.ExpressionsMenu;
            int used = menu != null && menu.controls != null ? menu.controls.Count : 0;
            var topLevel = new HashSet<string>();
            foreach (ParelObjectToggle toggle in toggles)
            {
                string folder = FirstFolder(toggle.menuFolder);
                topLevel.Add(folder ?? "toggle:" + System.Array.IndexOf(toggles, toggle));
            }
            foreach (ParelOutfitSwitcher switcher in switchers)
            {
                topLevel.Add(FirstFolder(switcher.menuFolder) ?? switcher.MenuLabel);
            }
            if (menu != null)
            {
                foreach (ParelExpressionsMenu.Control control in menu.controls)
                {
                    if (control != null && control.type == ParelExpressionsMenu.ControlType.SubMenu) topLevel.Remove(control.name);
                }
            }
            if (used + topLevel.Count > ParelExpressionsMenu.MaxControls)
            {
                report.AddError($"Avatar Tools need {topLevel.Count} more button(s) on the top page of your Expressions Menu, but it only has room for {ParelExpressionsMenu.MaxControls - used}. Give your toggles a Menu Folder (e.g. \"Clothing\") so they share a sub menu.");
            }

            report.AddInfo($"Avatar Tools: {toggles.Length} toggle(s), {switchers.Length} outfit switcher(s), {clothing.Length} clothing item(s), {attachments.Length} attachment(s), {syncs.Length} blend shape sync(s) -- applied when you build.");
        }

        private static string FirstFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string first = path.Split('/').Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0);
            return first;
        }

        /// <summary>Synced bits the Avatar Tools add on top of the avatar's own Expression Parameters.</summary>
        public static int ExtraSyncedBits(ParelAvatarDescriptor descriptor) => ExtraSyncedBits(descriptor, out _);

        private static int ExtraSyncedBits(ParelAvatarDescriptor descriptor, out List<string> conflictList)
        {
            var conflicts = new List<string>();
            conflictList = conflicts;
            ParelExpressionParameters existing = descriptor.ExpressionParameters;
            var added = new Dictionary<string, ParelExpressionParameters.ValueType>();
            int bits = 0;

            void Add(string name, ParelExpressionParameters.ValueType type, bool synced, string owner, bool isExplicit)
            {
                ParelExpressionParameters.Parameter mine = existing?.FindParameter(name);
                if (mine != null)
                {
                    if (mine.valueType != type && isExplicit) conflicts.Add($"{owner}: the parameter '{name}' is a {mine.valueType} in your Expression Parameters, but it needs to be {type}.");
                    if (mine.valueType == type) return;
                }
                if (added.TryGetValue(name, out ParelExpressionParameters.ValueType other))
                {
                    if (other != type) conflicts.Add($"{owner}: the parameter '{name}' is used as both {other} and {type}.");
                    return;
                }
                added[name] = type;
                if (synced) bits += ParelExpressionParameters.CostOf(type);
            }

            foreach (ParelObjectToggle toggle in descriptor.GetComponentsInChildren<ParelObjectToggle>(true))
            {
                bool isExplicit = !string.IsNullOrWhiteSpace(toggle.parameterName);
                string name = isExplicit ? toggle.parameterName.Trim() : toggle.MenuLabel.Replace('/', '_');
                Add(name, ParelExpressionParameters.ValueType.Bool, toggle.synced, $"Object Toggle '{toggle.MenuLabel}'", isExplicit);
            }
            foreach (ParelOutfitSwitcher switcher in descriptor.GetComponentsInChildren<ParelOutfitSwitcher>(true))
            {
                bool isExplicit = !string.IsNullOrWhiteSpace(switcher.parameterName);
                string name = isExplicit ? switcher.parameterName.Trim() : switcher.MenuLabel.Replace('/', '_');
                Add(name, ParelExpressionParameters.ValueType.Int, switcher.synced, $"Outfit Switcher '{switcher.MenuLabel}'", isExplicit);
            }
            return bits;
        }
    }
}
