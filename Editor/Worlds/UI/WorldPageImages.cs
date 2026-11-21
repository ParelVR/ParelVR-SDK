using System.IO;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Worlds.UI
{
    /// <summary>
    /// Pictures for the world's Shop page: copied in from a file the creator picks, or snapped from the Scene
    /// view. Both land as PNG / JPG files in <see cref="Folder"/>, so the publisher can upload them as they are.
    /// </summary>
    public static class WorldPageImages
    {
        public const string Folder = "Assets/ParelVR World Page";
        private const int SnapWidth = 1280, SnapHeight = 720;

        /// <summary>Copies a PNG or JPG into the project and returns it as a texture (null if cancelled or not a picture).</summary>
        public static Texture2D Choose(string name)
        {
            string source = EditorUtility.OpenFilePanel("Choose a picture", string.Empty, "png,jpg,jpeg");
            if (string.IsNullOrEmpty(source)) return null;

            string extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                EditorUtility.DisplayDialog("Choose a picture", "Shop pictures have to be PNG or JPG files.", "OK");
                return null;
            }

            string target = NewPath(name, extension);
            File.Copy(source, target, true);
            return Import(target);
        }

        /// <summary>Takes a 16:9 picture of what the Scene view shows (or the main camera when no Scene view is open).</summary>
        public static Texture2D Snap(string name)
        {
            Camera cam = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (cam == null)
            {
                EditorUtility.DisplayDialog("Snap", "Open a Scene view and frame what the picture should show, then press Snap again.", "OK");
                return null;
            }

            RenderTexture rt = RenderTexture.GetTemporary(SnapWidth, SnapHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture oldTarget = cam.targetTexture;
            float oldAspect = cam.aspect;
            RenderTexture oldActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)SnapWidth / SnapHeight;
                cam.Render();
                RenderTexture.active = rt;
                var picture = new Texture2D(SnapWidth, SnapHeight, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0, 0, SnapWidth, SnapHeight), 0, 0);
                picture.Apply();
                byte[] png = picture.EncodeToPNG();
                Object.DestroyImmediate(picture);

                string target = NewPath(name, ".png");
                File.WriteAllBytes(target, png);
                return Import(target);
            }
            finally
            {
                cam.targetTexture = oldTarget;
                cam.aspect = oldAspect;
                RenderTexture.active = oldActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static string NewPath(string name, string extension)
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));
            string clean = string.Join("_", (string.IsNullOrWhiteSpace(name) ? "picture" : name).Split(Path.GetInvalidFileNameChars()));
            return AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + clean + extension);
        }

        private static Texture2D Import(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
