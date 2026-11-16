using System.Threading.Tasks;
using ParelVR.SDK.Core.Settings;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Port
{
    /// <summary>Connects Auto Port to the builders (see <see cref="ParelAutoPortHook"/>).</summary>
    [InitializeOnLoad]
    internal static class ParelPortHookup
    {
        static ParelPortHookup()
        {
            ParelAutoPortHook.PortScenes = () =>
            {
                ParelPortReport report = ParelContentPorter.PortOpenScenes();
                return report.Any || report.Notes.Count > 0 ? report.ToString() : null;
            };
            ParelAutoPortHook.PortObject = go =>
            {
                ParelPortReport report = ParelContentPorter.Port(go);
                return report.Any || report.Notes.Count > 0 ? report.ToString() : null;
            };
        }

        /// <summary>One line for the Control Panel's scan, with Auto Fix doing the port.</summary>
        public static void Report(ValidationReport into)
        {
            ParelPortReport survey = ParelContentPorter.ScanOpenScenes();
            if (!survey.Any) return;
            var text = new System.Text.StringBuilder();
            text.Append(survey.PortedCount + survey.RemovedCount).Append(" component(s) from other platforms are in the open scenes.");
            if (survey.Ported.Count > 0) text.Append(" Replaced with ParelVR's, same settings: ").Append(ParelPortReport.Describe(survey.Ported)).Append('.');
            if (survey.Removed.Count > 0) text.Append(" Removed, ParelVR has nothing like them: ").Append(ParelPortReport.Describe(survey.Removed)).Append('.');
            into.AddWarning(text.ToString(), () =>
            {
                ParelPortReport done = ParelContentPorter.PortOpenScenes();
                Debug.Log("[ParelVR Auto Port] " + done);
                EditorUtility.DisplayDialog("Auto Port", done.ToString(), "OK");
            });
        }
    }

    public sealed class ParelWorldPortValidator : IValidator
    {
        public string Name => "Content From Other Platforms";
        public ParelProjectType RequiredMode => ParelProjectType.World;

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();
            ParelPortHookup.Report(report);
            return Task.FromResult(report);
        }
    }

    public sealed class ParelAvatarPortValidator : IValidator
    {
        public string Name => "Content From Other Platforms";
        public ParelProjectType RequiredMode => ParelProjectType.Avatar;

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();
            ParelPortHookup.Report(report);
            return Task.FromResult(report);
        }
    }
}
