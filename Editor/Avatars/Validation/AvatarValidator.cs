using System.Threading.Tasks;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Core.Validation;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Validation
{
    /// <summary>Scene-wide avatar check used by Settings > Scan Now: validates every Avatar Descriptor in the open scenes.</summary>
    public class AvatarValidator : IValidator
    {
        public string Name => "Avatar Components";
        public global::ParelVR.SDK.Core.Settings.ParelProjectType RequiredMode => global::ParelVR.SDK.Core.Settings.ParelProjectType.Avatar;

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();
            ParelAvatarDescriptor[] descriptors = Object.FindObjectsByType<ParelAvatarDescriptor>(FindObjectsInactive.Include);

            if (descriptors.Length == 0)
            {
                report.AddError("No avatar in the open scenes has a Parel Avatar Descriptor. Add one to your avatar's root (Add Component > ParelVR > Avatar SDK > Parel Avatar Descriptor).");
                return Task.FromResult(report);
            }

            foreach (ParelAvatarDescriptor descriptor in descriptors)
            {
                ValidationReport single = ParelAvatarDescriptorValidator.Validate(descriptor);
                foreach (ValidationIssue issue in single.Issues)
                {
                    report.Issues.Add(new ValidationIssue($"[{descriptor.name}] {issue.Message}", issue.Level, issue.AutoFix));
                }
            }
            return Task.FromResult(report);
        }
    }
}
