using ParelVR.SDK.Core.Settings;

namespace ParelVR.SDK.Core.ControlPanel
{
    /// <summary>
    /// Extension point for any SDK system (tabs, editor hooks, background services) that
    /// needs to turn itself fully on/off when the project's ParelProjectType changes --
    /// not merely hide its UI. Discovered via TypeCache reflection by ParelModeManager.
    /// </summary>
    public interface IParelModeAware
    {
        /// <summary>Called when this becomes the active project type -- both at Editor domain
        /// load (for whatever mode is currently persisted) and after a mode switch.</summary>
        void OnModeActivated(ParelProjectType mode);

        /// <summary>Called when this stops being the active project type, i.e. right before
        /// switching to a different mode. Never called at domain load.</summary>
        void OnModeDeactivated(ParelProjectType mode);
    }
}
