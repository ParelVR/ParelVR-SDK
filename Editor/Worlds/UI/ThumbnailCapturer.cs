using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Worlds.UI
{
    public static class ThumbnailCapturer
    {
        public static Texture2D CaptureSceneView(int width = 800, int height = 600)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null) return null;

            Camera svCam = sceneView.camera;
            if (svCam == null) return null;

            // Use a temporary camera to avoid rendering gizmos and UI overlays
            GameObject tempCamObj = new GameObject("TempThumbnailCamera");
            tempCamObj.hideFlags = HideFlags.HideAndDontSave;
            
            Camera tempCam = tempCamObj.AddComponent<Camera>();
            tempCam.CopyFrom(svCam);
            
            RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            tempCam.targetTexture = rt;
            tempCam.Render();
            tempCam.targetTexture = null;

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tempCamObj);
            
            return tex;
        }
    }
}
