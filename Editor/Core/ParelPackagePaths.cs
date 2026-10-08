using System.IO;
using UnityEditor;

namespace ParelVR.SDK.Core
{
    /// <summary>
    /// Where the SDK's own files are. The SDK can be installed as a package (from Git or embedded) or imported
    /// under Assets from a .unitypackage, so nothing may assume a folder: everything is found from this file.
    /// </summary>
    public static class ParelPackagePaths
    {
        // The GUID of this script's .meta file. It never changes, wherever the SDK ends up.
        private const string AnchorGuid = "24fad8f3e43209d28bc9ba8c31d6f1ed";
        private static string _root;

        /// <summary>The SDK's root folder as an asset path, without a trailing slash.</summary>
        public static string Root
        {
            get
            {
                if (_root != null && AssetDatabase.IsValidFolder(_root)) return _root;
                string anchor = AssetDatabase.GUIDToAssetPath(AnchorGuid);
                // <root>/Editor/Core/ParelPackagePaths.cs
                _root = string.IsNullOrEmpty(anchor) ? "Packages/com.parelvrsdk.pvr"
                    : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(anchor))).Replace('\\', '/');
                return _root;
            }
        }

        public static string Combine(string relative) => Root + "/" + relative.TrimStart('/');

        /// <summary>The same place on disk, for code that works with files rather than assets.</summary>
        public static string FullPath(string relative) => Path.GetFullPath(Combine(relative));
    }
}
