using System;

namespace ParelVR.SDK.Core.Validation
{
    public sealed class ValidationIssue
    {
        public string Message { get; }
        public ValidationIssueLevel Level { get; }
        public Action AutoFix { get; }
        public bool HasAutoFix => AutoFix != null;

        /// <summary>The object the issue is about; the Builder shows a Select button for it.</summary>
        public UnityEngine.Object Context { get; }

        public ValidationIssue(string message, ValidationIssueLevel level, Action autoFix = null, UnityEngine.Object context = null)
        {
            Message = message;
            Level = level;
            AutoFix = autoFix;
            Context = context;
        }
    }
}
