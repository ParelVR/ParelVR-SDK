using System.Collections.Generic;
using ParelVR.SDK.Core.Settings;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build
{
    /// <summary>
    /// Turns Mip Streaming off on every texture a bundle is about to carry. A streamed texture in a bundle
    /// is drawn from its lowest mip until the game's streamer gets round to it, so uploads came out blurry.
    /// Switched off in Settings > Preferences, this leaves the textures alone.
    /// </summary>
    public static class ParelMipStreamingOff
    {
        /// <summary>Turns Mip Streaming off on the textures <paramref name="assetPaths"/> use. Returns how many changed.</summary>
        public static int TurnOffFor(IEnumerable<string> assetPaths, List<string> notes = null)
        {
            if (!ParelPreferences.AutoDisableMipStreaming || assetPaths == null) return 0;

            var changed = new List<TextureImporter>();
            foreach (string path in AssetDatabase.GetDependencies(new List<string>(assetPaths).ToArray(), true))
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.streamingMipmaps)
                    changed.Add(importer);
            }
            if (changed.Count == 0) return 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (TextureImporter importer in changed)
                {
                    importer.streamingMipmaps = false;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            string line = $"Turned Mip Streaming off on {changed.Count} texture(s) so they load at full quality in the game.";
            notes?.Add(line);
            Debug.Log("[ParelVR SDK] " + line);
            return changed.Count;
        }
    }
}
