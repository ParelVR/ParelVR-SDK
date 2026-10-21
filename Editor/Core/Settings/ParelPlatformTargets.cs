using System.Collections.Generic;

namespace ParelVR.SDK.Core.Settings
{
    /// <summary>
    /// Where a world or avatar is available. Creators build once on PC; when Meta / Standalone VR is picked, ParelVR's
    /// servers make the standalone version from what the SDK uploads with the publish.
    /// </summary>
    public static class ParelPlatformTargets
    {
        public const string Pc = "pc";
        public const string Standalone = "standalone";

        public const string PcLabel = "PC / PCVR";
        public const string StandaloneLabel = "Meta / Standalone VR";

        public static List<string> From(bool pc, bool standalone)
        {
            var targets = new List<string>();
            if (pc) targets.Add(Pc);
            if (standalone) targets.Add(Standalone);
            return targets;
        }

        /// <summary>True when <paramref name="targets"/> includes <paramref name="target"/>. Records from before platform picking list none: both.</summary>
        public static bool Has(IList<string> targets, string target)
        {
            return targets == null || targets.Count == 0 || targets.Contains(target);
        }
    }
}
