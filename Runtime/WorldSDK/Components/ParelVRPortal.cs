using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// A portal of any shape, anywhere: on a wall, in the floor, on the ceiling, or standing in the air. Its
    /// <see cref="Surface"/> (any mesh: a quad, a crack, a circle) shows the place its <see cref="LinkedPortal"/> is,
    /// live, as a window into it. Players, thrown props and anything held go through it into that place, keeping
    /// their speed turned the way the two portals face (drop a ball into one floor portal and it flies up out of the
    /// other). Whatever is half-way through, a held prop or a hand, is seen coming out of the other end.
    /// The portal's plane is this object's own: its blue line points out of the front, the side it is seen and
    /// entered from. The frame, sparks and sounds around it are the world's own: put anything you like there.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/Portal")]
    public sealed class ParelVRPortal : MonoBehaviour
    {
        [Tooltip("The other end: this portal shows it and leads to it. Link that one back to this for a two-way pair.")]
        [SerializeField] private ParelVRPortal linkedPortal;
        [Tooltip("The surface the live view is drawn on, any shape. Lay it flat on this object's plane (its local Z = 0). " +
                 "In ParelVR its material is swapped for the view; in the editor it shows as you made it.")]
        [SerializeField] private Renderer surface;
        [Tooltip("Optional: cuts the shape out of the surface with a picture (white or opaque = open), e.g. a crack on a plain quad.")]
        [SerializeField] private Texture2D shapeMask;
        [Range(0.01f, 0.99f)]
        [SerializeField] private float maskCutoff = 0.5f;
        [Tooltip("Glow along the mask's edge, drawn by the game (alpha 0 = none).")]
        [ColorUsage(true, true)]
        [SerializeField] private Color edgeGlow = new Color(1f, 0.55f, 0.15f, 0f);
        [Range(0.005f, 0.4f)]
        [SerializeField] private float edgeWidth = 0.08f;
        [Tooltip("Seen and entered from both sides (a portal standing in the air). Off: only from the front, like a portal on a wall.")]
        [SerializeField] private bool twoSided;

        [Header("Passing through")]
        [Tooltip("Players walking, falling or jumping into it come out of the linked portal.")]
        [SerializeField] private bool playersPass = true;
        [Tooltip("Props with a Rigidbody go through it (held ones show on both sides and go through when let go).")]
        [SerializeField] private bool objectsPass = true;
        [Tooltip("Players and props keep their speed, turned the way the linked portal faces: fall into the floor, fly out of the wall.")]
        [SerializeField] private bool keepMomentum = true;
        [Tooltip("The wall, floor or ceiling this portal is on: whatever goes into the opening passes through these instead of hitting them.")]
        [SerializeField] private Collider[] passThrough = Array.Empty<Collider>();

        [Header("View")]
        [Tooltip("The live view is drawn while the viewer is within this many metres.")]
        [SerializeField] private float drawWithin = 40f;
        [Tooltip("Sharpness of the live view (1 = as sharp as the screen).")]
        [Range(0.25f, 1f)]
        [SerializeField] private float sharpness = 1f;
        [Tooltip("What a portal shows when it is seen inside another portal's view, or by a camera that has no view of its own.")]
        [SerializeField] private Color nestedColor = new Color(0.02f, 0.02f, 0.03f, 1f);

        [Header("Events")]
        [Tooltip("The local player came out of this portal's linked portal, having gone in here.")]
        public UnityEvent onPlayerPassed = new UnityEvent();
        [Tooltip("A prop went in here (it is now at the linked portal).")]
        public ObjectEvent onObjectPassed = new ObjectEvent();

        [Serializable]
        public sealed class ObjectEvent : UnityEvent<GameObject> { }

        // The opening's shape in the portal's own space (metres, unscaled), baked from the surface (and mask) in the
        // editor, so it is known in the game even when the mesh and picture can't be read there.
        [SerializeField, HideInInspector] private Vector2[] shapeTris = Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private Vector2[] shapeUvs = Array.Empty<Vector2>();
        [SerializeField, HideInInspector] private Rect shapeRect;
        [SerializeField, HideInInspector] private int maskSize;
        [SerializeField, HideInInspector] private byte[] maskValues = Array.Empty<byte>();

        private static readonly List<ParelVRPortal> all = new List<ParelVRPortal>();
        private Component runtime;

        /// <summary>Every portal switched on.</summary>
        public static IReadOnlyList<ParelVRPortal> All => all;

        public ParelVRPortal LinkedPortal { get => linkedPortal; set => linkedPortal = value; }
        public Renderer Surface { get => surface; set { surface = value; BakeAtRuntime(); } }
        public Texture2D ShapeMask { get => shapeMask; set { shapeMask = value; maskSize = 0; maskValues = Array.Empty<byte>(); } }
        public float MaskCutoff { get => maskCutoff; set => maskCutoff = Mathf.Clamp(value, 0.01f, 0.99f); }
        public Color EdgeGlow { get => edgeGlow; set => edgeGlow = value; }
        public float EdgeWidth { get => edgeWidth; set => edgeWidth = Mathf.Clamp(value, 0.005f, 0.4f); }
        public bool TwoSided { get => twoSided; set => twoSided = value; }
        public bool PlayersPass { get => playersPass; set => playersPass = value; }
        public bool ObjectsPass { get => objectsPass; set => objectsPass = value; }
        public bool KeepMomentum { get => keepMomentum; set => keepMomentum = value; }
        public Collider[] PassThrough { get => passThrough; set => passThrough = value ?? Array.Empty<Collider>(); }
        public float DrawWithin { get => drawWithin; set => drawWithin = Mathf.Max(1f, value); }
        public float Sharpness { get => sharpness; set => sharpness = Mathf.Clamp(value, 0.25f, 1f); }
        public Color NestedColor { get => nestedColor; set => nestedColor = value; }

        /// <summary>Open: switched on and linked to a portal that is switched on too.</summary>
        public bool IsOpen => isActiveAndEnabled && linkedPortal != null && linkedPortal != this && linkedPortal.isActiveAndEnabled;

        /// <summary>The portal's plane as a matrix: its position and turn, without scale (metres).</summary>
        public Matrix4x4 Frame => Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        /// <summary>The opening's bounds on the portal's plane (its own space, metres).</summary>
        public Rect ShapeRect
        {
            get
            {
                if (shapeRect.width <= 0f) BakeAtRuntime();
                return shapeRect;
            }
        }

        /// <summary>
        /// Carries a point (or, with MultiplyVector, a direction) from beside this portal to the matching place at the
        /// linked one: what stands just behind this portal's front stands just in front of the linked one.
        /// </summary>
        public Matrix4x4 ToLinked(Vector3 fromSide)
        {
            if (linkedPortal == null) return Matrix4x4.identity;
            Matrix4x4 here = Frame, there = linkedPortal.Frame;
            bool front = Vector3.Dot(fromSide - transform.position, transform.forward) >= 0f;
            return front ? there * Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)) * here.inverse : there * here.inverse;
        }

        /// <summary>A world point, in the portal's own space (metres; Z = in front of the plane).</summary>
        public Vector3 Local(Vector3 world) => Quaternion.Inverse(transform.rotation) * (world - transform.position);

        /// <summary>Whether a world point, seen straight along the portal's line, is inside the opening (its shape and mask).</summary>
        public bool InOpening(Vector3 world) => InOpeningLocal(Local(world));

        /// <summary>Whether a point of the portal's own space lies inside the opening's shape on its plane.</summary>
        public bool InOpeningLocal(Vector3 local)
        {
            if (shapeRect.width <= 0f) BakeAtRuntime();
            var p = new Vector2(local.x, local.y);
            if (!shapeRect.Contains(p)) return false;
            if (shapeTris == null || shapeTris.Length < 3) return true;
            for (int i = 0; i + 2 < shapeTris.Length; i += 3)
            {
                if (!Barycentric(p, shapeTris[i], shapeTris[i + 1], shapeTris[i + 2], out Vector3 w)) continue;
                if (!HasMask()) return true;
                Vector2 uv = shapeUvs[i] * w.x + shapeUvs[i + 1] * w.y + shapeUvs[i + 2] * w.z;
                if (MaskAt(uv) >= maskCutoff) return true;
            }

            return false;
        }

        /// <summary>Whether a point is near the opening: within its bounds grown by <paramref name="grow"/> metres, and that close to the plane.</summary>
        public bool NearOpening(Vector3 world, float grow)
        {
            Vector3 local = Local(world);
            Rect r = ShapeRect;
            return Mathf.Abs(local.z) <= grow && local.x >= r.xMin - grow && local.x <= r.xMax + grow && local.y >= r.yMin - grow && local.y <= r.yMax + grow;
        }

        private void OnEnable()
        {
            if (!all.Contains(this)) all.Add(this);
            if (shapeRect.width <= 0f) BakeAtRuntime();
            if (runtime == null && ParelVRPortalHooks.CreatePortal != null) runtime = ParelVRPortalHooks.CreatePortal(this);
        }

        private void OnDisable() => all.Remove(this);

        // ---- the opening's shape -------------------------------------------------------------------------------

        private bool HasMask() => shapeMask != null && (maskSize > 0 && maskValues != null && maskValues.Length == maskSize * maskSize || shapeMask.isReadable);

        private float MaskAt(Vector2 uv)
        {
            if (maskSize > 0 && maskValues != null && maskValues.Length == maskSize * maskSize)
            {
                int x = Mathf.Clamp((int)(Repeat01(uv.x) * maskSize), 0, maskSize - 1);
                int y = Mathf.Clamp((int)(Repeat01(uv.y) * maskSize), 0, maskSize - 1);
                return maskValues[y * maskSize + x] / 255f;
            }

            if (shapeMask != null && shapeMask.isReadable)
            {
                Color c = shapeMask.GetPixelBilinear(uv.x, uv.y);
                return (c.r + c.g + c.b) / 3f * c.a;
            }

            return 1f;
        }

        private static float Repeat01(float v) => v - Mathf.Floor(v);

        private static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out Vector3 w)
        {
            w = default;
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float d = v0.x * v1.y - v1.x * v0.y;
            if (Mathf.Abs(d) < 1e-9f) return false;
            float v = (v2.x * v1.y - v1.x * v2.y) / d;
            float u = (v0.x * v2.y - v2.x * v0.y) / d;
            if (v < -1e-4f || u < -1e-4f || v + u > 1.0001f) return false;
            w = new Vector3(1f - v - u, v, u);
            return true;
        }

        // The surface's triangles laid on the plane. In the game, meshes can only be read when the world left them
        // readable: then the editor's bake is used, or the surface's bounds.
        private void BakeAtRuntime()
        {
            if (surface == null) return;
            if (shapeTris != null && shapeTris.Length >= 3 && shapeRect.width > 0f) return;
            if (!BakeTriangles(false)) BakeBounds();
        }

        private bool BakeTriangles(bool editor)
        {
            var filter = surface != null ? surface.GetComponent<MeshFilter>() : null;
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || (!editor && !mesh.isReadable)) return false;
            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            int[] triangles = mesh.triangles;
            if (vertices.Length == 0 || triangles.Length < 3) return false;
            Matrix4x4 toPortal = Frame.inverse * surface.transform.localToWorldMatrix;
            var tris = new Vector2[triangles.Length];
            var triUvs = new Vector2[triangles.Length];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < triangles.Length; i++)
            {
                Vector3 p = toPortal.MultiplyPoint3x4(vertices[triangles[i]]);
                tris[i] = new Vector2(p.x, p.y);
                triUvs[i] = uvs.Length == vertices.Length ? uvs[triangles[i]] : Vector2.zero;
                minX = Mathf.Min(minX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxX = Mathf.Max(maxX, p.x);
                maxY = Mathf.Max(maxY, p.y);
            }

            shapeTris = tris;
            shapeUvs = triUvs;
            shapeRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        private void BakeBounds()
        {
            shapeTris = Array.Empty<Vector2>();
            shapeUvs = Array.Empty<Vector2>();
            Bounds b = surface.bounds;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 p = Local(corner);
                minX = Mathf.Min(minX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxX = Mathf.Max(maxX, p.x);
                maxY = Mathf.Max(maxY, p.y);
            }

            shapeRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        // ---- editor ------------------------------------------------------------------------------------------------

#if UNITY_EDITOR
        private const int MaskBakeSize = 64;
        private bool bakeQueued;

        private void Reset()
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.GetComponent<MeshFilter>() == null) continue;
                surface = r;
                break;
            }
        }

        private void OnValidate()
        {
            if (bakeQueued) return;
            bakeQueued = true;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                bakeQueued = false;
                if (this != null) BakeInEditor();
            };
        }

        /// <summary>Bakes the opening's shape from the surface's mesh and the mask picture (done on every change in the editor).</summary>
        [ContextMenu("Bake Opening Shape")]
        public void BakeInEditor()
        {
            if (surface == null)
            {
                shapeTris = Array.Empty<Vector2>();
                shapeUvs = Array.Empty<Vector2>();
                shapeRect = default;
                return;
            }

            if (!BakeTriangles(true)) BakeBounds();
            BakeMask();
            UnityEditor.EditorUtility.SetDirty(this);
        }

        private void BakeMask()
        {
            maskSize = 0;
            maskValues = Array.Empty<byte>();
            if (shapeMask == null) return;
            RenderTexture rt = RenderTexture.GetTemporary(MaskBakeSize, MaskBakeSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture was = RenderTexture.active;
            var read = new Texture2D(MaskBakeSize, MaskBakeSize, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(shapeMask, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, MaskBakeSize, MaskBakeSize), 0, 0);
                read.Apply();
                Color32[] pixels = read.GetPixels32();
                var values = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 c = pixels[i];
                    values[i] = (byte)Mathf.Clamp((c.r + c.g + c.b) / 3f * (c.a / 255f), 0f, 255f);
                }

                maskValues = values;
                maskSize = MaskBakeSize;
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                DestroyImmediate(read);
            }
        }

        [UnityEditor.MenuItem("GameObject/ParelVR/Portal Pair", false, 20)]
        private static void CreatePair(UnityEditor.MenuCommand command)
        {
            var root = new GameObject("Portal Pair");
            UnityEditor.Undo.RegisterCreatedObjectUndo(root, "Create Portal Pair");
            if (command.context is GameObject parent) root.transform.SetParent(parent.transform, false);
            ParelVRPortal a = MakePortal("Portal A", root.transform, new Vector3(-1.5f, 0f, 0f));
            ParelVRPortal b = MakePortal("Portal B", root.transform, new Vector3(1.5f, 0f, 0f));
            a.linkedPortal = b;
            b.linkedPortal = a;
            a.BakeInEditor();
            b.BakeInEditor();
            UnityEditor.Selection.activeGameObject = root;
        }

        private static ParelVRPortal MakePortal(string name, Transform parent, Vector3 at)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at + Vector3.up * 1.1f;
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Surface";
            DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(go.transform, false);
            quad.transform.localScale = new Vector3(1.2f, 2.2f, 1f);
            // A quad faces -Z: turned round so it faces the portal's front.
            quad.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var portal = go.AddComponent<ParelVRPortal>();
            portal.surface = quad.GetComponent<Renderer>();
            return portal;
        }

        [NonSerialized] private Matrix4x4 bakedFor;

        private void OnDrawGizmos()
        {
            // The surface was moved or resized since the last bake (moving it doesn't call OnValidate).
            if (!Application.isPlaying && surface != null)
            {
                Matrix4x4 now = Frame.inverse * surface.transform.localToWorldMatrix;
                if (now != bakedFor)
                {
                    bakedFor = now;
                    Rect before = shapeRect;
                    int count = shapeTris != null ? shapeTris.Length : 0;
                    if (BakeTriangles(true) && (before != shapeRect || count != shapeTris.Length)) UnityEditor.EditorUtility.SetDirty(this);
                }
            }

            Color color = IsLinkedBothWays() ? new Color(0.35f, 0.75f, 1f, 0.95f) : new Color(1f, 0.6f, 0.2f, 0.95f);
            Matrix4x4 was = Gizmos.matrix;
            Gizmos.matrix = Frame;
            Gizmos.color = color;
            if (shapeTris != null && shapeTris.Length >= 3 && shapeTris.Length <= 3000)
            {
                for (int i = 0; i + 2 < shapeTris.Length; i += 3)
                {
                    Gizmos.DrawLine(shapeTris[i], shapeTris[i + 1]);
                    Gizmos.DrawLine(shapeTris[i + 1], shapeTris[i + 2]);
                    Gizmos.DrawLine(shapeTris[i + 2], shapeTris[i]);
                }
            }
            else if (shapeRect.width > 0f)
            {
                Gizmos.DrawWireCube(new Vector3(shapeRect.center.x, shapeRect.center.y, 0f), new Vector3(shapeRect.width, shapeRect.height, 0f));
            }

            Vector3 middle = shapeRect.width > 0f ? new Vector3(shapeRect.center.x, shapeRect.center.y, 0f) : Vector3.zero;
            Gizmos.DrawLine(middle, middle + Vector3.forward * 0.6f);
            Gizmos.DrawLine(middle + Vector3.forward * 0.6f, middle + new Vector3(0.08f, 0f, 0.48f));
            Gizmos.DrawLine(middle + Vector3.forward * 0.6f, middle + new Vector3(-0.08f, 0f, 0.48f));
            Gizmos.matrix = was;
            if (linkedPortal != null)
            {
                Gizmos.color = new Color(color.r, color.g, color.b, 0.3f);
                Gizmos.DrawLine(transform.TransformPoint(middle), linkedPortal.transform.position);
            }

            UnityEditor.Handles.color = color;
            UnityEditor.Handles.Label(transform.position + transform.up * (shapeRect.yMax + 0.15f),
                linkedPortal == null ? "Portal (not linked)" : "Portal → " + linkedPortal.name);
        }

        private bool IsLinkedBothWays() => linkedPortal != null && linkedPortal.linkedPortal == this;
#endif
    }
}
