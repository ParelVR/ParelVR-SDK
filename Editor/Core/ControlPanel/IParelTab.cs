using UnityEngine.UIElements;
using ParelVR.SDK.Core.Settings;

namespace ParelVR.SDK.Core.ControlPanel
{
    /// <summary>
    /// Extension point that modules (Worlds, Avatars) implement to plug their own tab into the
    /// shared dashboard shell. ParelControlPanel discovers implementations via TypeCache reflection
    /// rather than referencing downstream assemblies directly -- Core has zero compile-time
    /// knowledge of any module, keeping the dependency graph one-directional.
    /// </summary>
    public interface IParelTab
    {
        /// <summary>Display name shown in the dashboard's tab strip.</summary>
        string TabName { get; }

        /// <summary>Left-to-right ordering in the tab strip -- lower shows first.</summary>
        int TabOrder { get; }

        /// <summary>Which project type this tab applies to. ParelControlPanel only discovers and
        /// constructs tabs matching ParelModeManager.Current -- a tab for the inactive mode
        /// is never instantiated, so it has no UI, no Update loop, nothing running.</summary>
        ParelProjectType RequiredMode { get; }

        /// <summary>Called once per session the first time this tab is shown; build your
        /// VisualElement tree into <paramref name="container"/>.</summary>
        void BuildUI(VisualElement container);

        /// <summary>Called every time the tab is switched TO (including the first time, after
        /// BuildUI). Good place to (re)fetch data.</summary>
        void OnShown();
    }
}
