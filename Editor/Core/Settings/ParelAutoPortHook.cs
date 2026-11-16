using System;
using UnityEngine;

namespace ParelVR.SDK.Core.Settings
{
    /// <summary>
    /// Where the builders call Auto Port when "Auto Port Content" is on. Auto Port lives in its own assembly and
    /// fills these in when the editor loads.
    /// </summary>
    public static class ParelAutoPortHook
    {
        /// <summary>Ports the open scenes; returns what it did, for the build log.</summary>
        public static Func<string> PortScenes;

        /// <summary>Ports one object (an avatar); returns what it did.</summary>
        public static Func<GameObject, string> PortObject;

        /// <summary>Before a world is built: the open scenes are ported when Auto Port Content is on.</summary>
        public static void BeforeWorldBuild()
        {
            if (!ParelPreferences.AutoPortContentEnabled || PortScenes == null) return;
            string result = PortScenes();
            if (!string.IsNullOrEmpty(result)) Debug.Log("[ParelVR SDK] Auto Port before the build: " + result);
        }

        /// <summary>Before an avatar is built: the avatar is ported when Auto Port Content is on.</summary>
        public static void BeforeAvatarBuild(GameObject avatar)
        {
            if (!ParelPreferences.AutoPortContentEnabled || PortObject == null || avatar == null) return;
            string result = PortObject(avatar);
            if (!string.IsNullOrEmpty(result)) Debug.Log("[ParelVR SDK] Auto Port before the build: " + result);
        }
    }
}
