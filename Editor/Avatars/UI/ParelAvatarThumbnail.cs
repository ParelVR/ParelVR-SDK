using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.UI
{
    /// <summary>
    /// Thumbnail pictures for avatar uploads: an automatic head-and-shoulders portrait (like VRChat's
    /// upload camera), or whatever the Scene View is looking at.
    /// </summary>
    public static class ParelAvatarThumbnail
    {
        public const int Width = 1200;
        public const int Height = 900;

        private static readonly Color Backdrop = new Color(0.09f, 0.08f, 0.13f, 1f);

        /// <summary>Frames the avatar's upper body from the front and renders it.</summary>
        public static Texture2D CapturePortrait(GameObject avatar)
        {
            if (avatar == null) return null;

            Animator animator = avatar.GetComponentInChildren<Animator>();
            Transform head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            Transform chest = animator != null && animator.isHuman ? (animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest)) : null;

            Bounds bounds = CalculateBounds(avatar);
            Vector3 focus;
            float span;
            if (head != null)
            {
                Vector3 lower = chest != null ? chest.position : head.position - avatar.transform.up * 0.35f;
                focus = Vector3.Lerp(head.position, lower, 0.35f);
                span = Mathf.Max(0.35f, Vector3.Distance(head.position, lower) * 2.4f);
            }
            else
            {
                focus = bounds.center;
                span = Mathf.Max(bounds.size.y, 0.5f);
            }

            const float fov = 24f;
            float distance = span * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            Vector3 forward = avatar.transform.forward;
            Vector3 cameraPosition = focus + forward * distance + avatar.transform.up * (span * 0.05f);

            var cameraObject = new GameObject("ParelVR Avatar Thumbnail Camera") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.transform.SetPositionAndRotation(cameraPosition, Quaternion.LookRotation(focus - cameraPosition, avatar.transform.up));
                camera.fieldOfView = fov;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = distance * 10f + 10f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Backdrop;
                camera.cullingMask = ~0;
                return Render(camera);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        /// <summary>Renders what the Scene View camera currently sees.</summary>
        public static Texture2D CaptureSceneView()
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null) return null;

            var cameraObject = new GameObject("ParelVR Avatar Thumbnail Camera") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.CopyFrom(view.camera);
                return Render(camera);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }

        private static Texture2D Render(Camera camera)
        {
            var target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.aspect = (float)Width / Height;
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(root.transform.position + Vector3.up, Vector3.one);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }
    }
}
