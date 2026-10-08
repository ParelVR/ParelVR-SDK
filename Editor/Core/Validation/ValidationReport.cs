using System.Collections.Generic;
using System.Linq;

namespace ParelVR.SDK.Core.Validation
{
    public sealed class ValidationReport
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();

        public bool CanBuild => !Issues.Any(i => i.Level == ValidationIssueLevel.Error);

        public void AddError(string message, System.Action autoFix = null, UnityEngine.Object context = null)
            => Issues.Add(new ValidationIssue(message, ValidationIssueLevel.Error, autoFix, context));

        public void AddWarning(string message, System.Action autoFix = null, UnityEngine.Object context = null)
            => Issues.Add(new ValidationIssue(message, ValidationIssueLevel.Warning, autoFix, context));

        public void AddInfo(string message, System.Action autoFix = null, UnityEngine.Object context = null)
            => Issues.Add(new ValidationIssue(message, ValidationIssueLevel.Info, autoFix, context));

        public void Merge(ValidationReport other)
        {
            if (other != null) Issues.AddRange(other.Issues);
        }
    }
}
