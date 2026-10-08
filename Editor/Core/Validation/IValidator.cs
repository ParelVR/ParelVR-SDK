using System.Threading.Tasks;

namespace ParelVR.SDK.Core.Validation
{
    public interface IValidator
    {
        /// <summary>
        /// Name of the validation step (e.g., "World Components")
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Which project type this validator applies to.
        /// </summary>
        global::ParelVR.SDK.Core.Settings.ParelProjectType RequiredMode { get; }

        /// <summary>
        /// Executes the validation rules and returns a report.
        /// Can be asynchronous to support heavy scans without freezing the Editor.
        /// </summary>
        Task<ValidationReport> ValidateAsync();
    }
}
