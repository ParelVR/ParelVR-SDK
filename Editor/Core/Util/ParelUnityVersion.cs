using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Core.Util
{
    /// <summary>The Unity version the SDK is made and tested on. Older Unity 6 versions install but can misbehave.</summary>
    [InitializeOnLoad]
    public static class ParelUnityVersion
    {
        public const string Recommended = "6000.4.11f1";

        private const string WarnedKey = "ParelVR.SDK.UnityVersionWarned";

        static ParelUnityVersion()
        {
            if (!IsOlderThanRecommended || SessionState.GetBool(WarnedKey, false)) return;
            SessionState.SetBool(WarnedKey, true);
            Debug.LogWarning("[ParelVR SDK] You're on Unity " + Application.unityVersion + ". The recommended version is Unity " +
                             Recommended + ": older versions may not work properly. Get it from Unity Hub > Installs > Install Editor > Archive.");
        }

        /// <summary>True when this editor is older than <see cref="Recommended"/>.</summary>
        public static bool IsOlderThanRecommended => Compare(Application.unityVersion, Recommended) < 0;

        // Compares versions like 6000.4.11f1: year, minor, patch, then a < b < f < p, then build number.
        private static int Compare(string a, string b)
        {
            long ka = Key(a), kb = Key(b);
            if (ka < 0 || kb < 0) return 0;
            return ka.CompareTo(kb);
        }

        private static long Key(string version)
        {
            Match m = Regex.Match(version ?? string.Empty, @"^(\d+)\.(\d+)\.(\d+)([abfp])(\d+)");
            if (!m.Success) return -1;
            long stage = "abfp".IndexOf(m.Groups[4].Value[0]);
            return long.Parse(m.Groups[1].Value) * 100_000_000_000L
                   + long.Parse(m.Groups[2].Value) * 100_000_000L
                   + long.Parse(m.Groups[3].Value) * 100_000L
                   + stage * 1_000L
                   + long.Parse(m.Groups[5].Value);
        }
    }
}
