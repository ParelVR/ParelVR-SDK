using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ParelVR.SDK.Backend
{
    /// <summary>
    /// Pictures of what was built, sent with an "Upload for someone else" request so ParelVR Support can see
    /// what it was: the model without textures (Geometry), every texture it uses (Textures), and the model
    /// with its textures (Combined). JPEG bytes; any of them can be null if it couldn't be made.
    /// </summary>
    public sealed class ParelBuildPictures
    {
        public byte[] Geometry;
        public byte[] Textures;
        public byte[] Combined;
        public int TextureCount;
        public long TextureBytes;
        public long Triangles;
        public int Meshes;
        public int MaterialSlots;

        private const int AvatarSize = 1024;
        private const int WorldWidth = 1600;
        private const int WorldHeight = 900;
        private const int SheetTile = 192;
        private const int SheetPad = 4;
        private const int SheetMax = 64;
        private const int Quality = 88;
        private const string ClayShader = "Hidden/ParelVR/Clay";
        private static readonly Color Backdrop = new Color(0.11f, 0.11f, 0.13f);

        /// <summary>The avatar on its own, from the front, in a preview scene (the open scene isn't touched).</summary>
        public static ParelBuildPictures ForAvatar(GameObject root)
        {
            var pictures = new ParelBuildPictures();
            if (root == null) return pictures;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            pictures.Count(renderers);
            pictures.Textures = Safe("textures", () => Sheet(pictures.TexturesOf(renderers)));

            PreviewRenderUtility preview = null;
            GameObject copy = null;
            try
            {
                preview = new PreviewRenderUtility();
                copy = Object.Instantiate(root);
                copy.hideFlags = HideFlags.HideAndDontSave;
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                preview.AddSingleGO(copy);

                Bounds bounds = BoundsOf(copy.GetComponentsInChildren<Renderer>(true));
                Camera camera = preview.camera;
                camera.fieldOfView = 30f;
                float radius = Mathf.Max(0.1f, bounds.extents.magnitude);
                float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.02f;
                camera.transform.position = bounds.center + Vector3.forward * distance; // avatars face +Z
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = distance * 0.01f;
                camera.farClipPlane = distance * 4f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Backdrop;
                preview.lights[0].intensity = 1.1f;
                preview.lights[0].transform.rotation = Quaternion.Euler(30f, 200f, 0f);
                preview.lights[1].intensity = 0.6f;
                preview.lights[1].transform.rotation = Quaternion.Euler(340f, 20f, 0f);
                preview.ambientColor = new Color(0.32f, 0.32f, 0.35f);

                var rect = new Rect(0, 0, AvatarSize, AvatarSize);
                pictures.Combined = Safe("combined", () =>
                {
                    preview.BeginStaticPreview(rect);
                    preview.Render(false, false);
                    return Encode(preview.EndStaticPreview());
                });
                Shader clay = Shader.Find(ClayShader);
                if (clay != null)
                {
                    pictures.Geometry = Safe("geometry", () =>
                    {
                        preview.BeginStaticPreview(rect);
                        camera.RenderWithShader(clay, string.Empty);
                        return Encode(preview.EndStaticPreview());
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ParelVR SDK] The avatar pictures weren't made: " + e.Message);
            }
            finally
            {
                if (copy != null) Object.DestroyImmediate(copy);
                preview?.Cleanup();
            }
            return pictures;
        }

        /// <summary>The world from where the Scene view is looking (or an overview when there is none).</summary>
        public static ParelBuildPictures ForWorld(Scene scene)
        {
            var pictures = new ParelBuildPictures();
            var renderers = new List<Renderer>();
            foreach (GameObject root in scene.GetRootGameObjects()) renderers.AddRange(root.GetComponentsInChildren<Renderer>(true));
            pictures.Count(renderers);
            pictures.Textures = Safe("textures", () => Sheet(pictures.TexturesOf(renderers)));

            var holder = new GameObject("ParelVR Build Pictures") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture target = null;
            try
            {
                Camera camera = holder.AddComponent<Camera>();
                camera.enabled = false;
                SceneView view = SceneView.lastActiveSceneView;
                if (view != null && view.camera != null)
                {
                    holder.transform.SetPositionAndRotation(view.camera.transform.position, view.camera.transform.rotation);
                    camera.fieldOfView = view.camera.fieldOfView;
                    camera.nearClipPlane = view.camera.nearClipPlane;
                    camera.farClipPlane = Mathf.Max(view.camera.farClipPlane, 2000f);
                }
                else
                {
                    Bounds bounds = BoundsOf(renderers.Where(r => r.enabled && r.gameObject.activeInHierarchy));
                    float radius = Mathf.Max(1f, bounds.extents.magnitude);
                    Vector3 direction = new Vector3(-1f, 0.8f, -1f).normalized;
                    camera.fieldOfView = 50f;
                    holder.transform.position = bounds.center + direction * radius * 1.4f;
                    holder.transform.LookAt(bounds.center);
                    camera.nearClipPlane = 0.1f;
                    camera.farClipPlane = radius * 6f;
                }

                target = new RenderTexture(WorldWidth, WorldHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
                camera.targetTexture = target;
                camera.scene = scene;
                pictures.Combined = Safe("combined", () =>
                {
                    camera.Render();
                    return Read(target);
                });
                Shader clay = Shader.Find(ClayShader);
                if (clay != null)
                {
                    pictures.Geometry = Safe("geometry", () =>
                    {
                        camera.clearFlags = CameraClearFlags.SolidColor;
                        camera.backgroundColor = Backdrop;
                        camera.RenderWithShader(clay, string.Empty);
                        return Read(target);
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ParelVR SDK] The world pictures weren't made: " + e.Message);
            }
            finally
            {
                Object.DestroyImmediate(holder);
                if (target != null) Object.DestroyImmediate(target);
            }
            return pictures;
        }

        // ---- counting ----------------------------------------------------------------------------

        private void Count(IEnumerable<Renderer> renderers)
        {
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                MaterialSlots += renderer.sharedMaterials.Length;
                if (mesh == null) continue;
                Meshes++;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    if (mesh.GetTopology(i) == MeshTopology.Triangles) Triangles += mesh.GetIndexCount(i) / 3;
                }
            }
        }

        private List<Texture2D> TexturesOf(IEnumerable<Renderer> renderers)
        {
            var found = new HashSet<Texture2D>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    foreach (int property in material.GetTexturePropertyNameIDs())
                    {
                        if (material.GetTexture(property) is Texture2D texture && texture != null) found.Add(texture);
                    }
                }
            }
            TextureCount = found.Count;
            var sized = found.Select(t => (texture: t, bytes: Profiler.GetRuntimeMemorySizeLong(t))).ToList();
            TextureBytes = sized.Sum(s => s.bytes);
            return sized.OrderByDescending(s => s.bytes).Select(s => s.texture).ToList();
        }

        // ---- drawing -----------------------------------------------------------------------------

        /// <summary>Every texture side by side, biggest first, each fitted into a square tile.</summary>
        private static byte[] Sheet(List<Texture2D> textures)
        {
            int count = Mathf.Min(textures.Count, SheetMax);
            if (count == 0) return null;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt(count / (float)columns);
            int width = columns * (SheetTile + SheetPad) + SheetPad;
            int height = rows * (SheetTile + SheetPad) + SheetPad;

            RenderTexture sheet = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = sheet;
                GL.Clear(true, true, QualitySettings.activeColorSpace == ColorSpace.Linear ? Backdrop.linear : Backdrop); // the sheet is sRGB
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, width, height, 0);
                for (int i = 0; i < count; i++)
                {
                    Texture2D texture = textures[i];
                    float aspect = texture.height > 0 ? texture.width / (float)texture.height : 1f;
                    float w = aspect >= 1f ? SheetTile : SheetTile * aspect;
                    float h = aspect >= 1f ? SheetTile / aspect : SheetTile;
                    float x = SheetPad + (i % columns) * (SheetTile + SheetPad) + (SheetTile - w) * 0.5f;
                    float y = SheetPad + (i / columns) * (SheetTile + SheetPad) + (SheetTile - h) * 0.5f;
                    Graphics.DrawTexture(new Rect(x, y, w, h), texture);
                }
                GL.PopMatrix();
                return Read(sheet);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(sheet);
            }
        }

        private static byte[] Read(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active;
            var picture = new Texture2D(source.width, source.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = source;
                picture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                picture.Apply();
                return picture.EncodeToJPG(Quality);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(picture);
            }
        }

        private static byte[] Encode(Texture2D picture)
        {
            if (picture == null) return null;
            try { return picture.EncodeToJPG(Quality); }
            finally { Object.DestroyImmediate(picture); }
        }

        private static Bounds BoundsOf(IEnumerable<Renderer> renderers)
        {
            bool any = false;
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) continue;
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        private static byte[] Safe(string what, Func<byte[]> make)
        {
            try { return make(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[ParelVR SDK] The {what} picture wasn't made: {e.Message}");
                return null;
            }
        }
    }
}
