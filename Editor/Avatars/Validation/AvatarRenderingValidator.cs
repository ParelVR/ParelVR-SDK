using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Build;
using ParelVR.SDK.Core.Validation;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Validation
{
    /// <summary>The Rendering section of the Avatar checks: materials that convert at upload or can't be shown, and the headset budget.</summary>
    public class AvatarRenderingValidator : IValidator
    {
        public string Name => "Avatar Rendering";
        public global::ParelVR.SDK.Core.Settings.ParelProjectType RequiredMode => global::ParelVR.SDK.Core.Settings.ParelProjectType.Avatar;

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();
#if UNITY_6000_4_OR_NEWER
            ParelAvatarDescriptor[] descriptors = Object.FindObjectsByType<ParelAvatarDescriptor>(FindObjectsInactive.Include);
#else
            ParelAvatarDescriptor[] descriptors = Object.FindObjectsByType<ParelAvatarDescriptor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#endif
            if (descriptors.Length == 0) return Task.FromResult(report);
            ParelRenderingValidation.AddPipelineIssues(report);
            // A URP avatar uploads as it is (ParelVR runs on URP): only materials that can't draw at all are converted.
            bool urp = ParelVR.Rendering.Editor.ParelSceneConverter.ProjectPipeline() == ParelVR.Rendering.RenderPipelineKind.Universal;
            foreach (ParelAvatarDescriptor d in descriptors)
            {
                var single = new ValidationReport();
                IEnumerable<Material> materials = d.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct();
                if (urp) materials = materials.Where(m => ParelVR.SDK.Build.ParelUploadRendering.NeedsConversion(new[] { m }, ParelVR.Rendering.RepairPolicy.BrokenOnly)).ToList();
                ParelRenderingValidation.AddMaterialIssues(single, materials, d.name);
                ParelRenderingValidation.AddQuestBudgetIssues(single, new[] { d.gameObject }, true);
                // What isn't headset-compatible, as an Android project's Console would show it (the project stays on Windows).
                ParelVR.SDK.Build.Quest.ParelQuestValidation.AddIssues(single, new[] { d.gameObject }, true, null, d.gameObject.name, d == descriptors[0]);
                foreach (ValidationIssue issue in single.Issues)
                    report.Issues.Add(new ValidationIssue($"[{d.name}] {issue.Message}", issue.Level, issue.AutoFix, issue.Context));
            }
            return Task.FromResult(report);
        }
    }
}
