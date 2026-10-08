using System;
using System.IO;
using UnityEngine;

namespace ParelVR.SDK.Core.Settings
{
    /// <summary>
    /// Defines the target backend environment. Persisted to Library/ParelVR/preferences.json
    /// alongside other preferences. Controls which base URL ParelApiClient uses.
    /// </summary>
    public enum ParelEnvironmentType
    {
        Development,
        Staging,
        Production
    }

    /// <summary>
    /// Resolves the base URL for the active environment. Default is Production, so the SDK works
    /// out of the box in a creator's project; Development (localhost) is for ParelVR's own team.
    /// </summary>
    public static class ParelEnvironment
    {
        public static ParelEnvironmentType Current { get; set; } = ParelEnvironmentType.Production;

        public static string GetBaseUrl(ParelEnvironmentType env) => env switch
        {
            ParelEnvironmentType.Development => "http://127.0.0.1:3000",
            ParelEnvironmentType.Staging     => "https://staging-api-parelvr.parelllc.com",
            ParelEnvironmentType.Production  => "https://api-parelvr.parelllc.com",
            _ => "https://api-parelvr.parelllc.com"
        };

        public static string BaseUrl => GetBaseUrl(Current);
    }
}
