using System;
using System.Collections.Generic;
using ParelVR.Volt.Players;
using ParelVR.VoltHost;
using UnityEngine;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// ParelVR's loading avatar: a body, a head and hands, and the nameplate above with the bar that fills while an
    /// avatar loads. It stands in for players without an avatar of their own, so mirrors, badges and anything placed
    /// on players can be tried. With an avatar under test only the nameplate is built.
    /// </summary>
    internal sealed class SimulatedStandIn
    {
        public const float PlateHeight = 0.42f, PlateWidth = 0.56f, PlateDepth = 0.12f;
        private const float LoadSeconds = 1.4f;

        public Transform Root { get; private set; }
        private Transform _head, _leftHand, _rightHand, _plate, _loadBar;
        private TextMesh _label;
        private readonly List<Material> _materials = new List<Material>();
        private float _loadStart;

        private Material Flat(Color colour, bool unlit)
        {
            bool srp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;
            Shader shader = srp ? Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit")
                                : Shader.Find(unlit ? "Unlit/Color" : "Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader) { color = colour, hideFlags = HideFlags.DontSave };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            _materials.Add(material);
            return material;
        }

        private static Transform Part(PrimitiveType shape, string name, Transform parent, Vector3 at, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = name;
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go.transform;
        }

        public static SimulatedStandIn Build(Transform parent, string displayName, Color skinColour, bool withBody, float eyeHeight)
        {
            var look = new SimulatedStandIn();
            look.Root = new GameObject(withBody ? "Avatar (loading avatar)" : "Nameplate").transform;
            look.Root.SetParent(parent, false);
            if (withBody)
            {
                Material skin = look.Flat(skinColour, false);
                float scale = eyeHeight / 1.6f;
                Part(PrimitiveType.Capsule, "Body", look.Root, new Vector3(0f, 0.78f * scale, 0f), new Vector3(0.34f, 0.62f, 0.24f) * scale, skin);
                // Seen from inside, the head's back faces are culled: it shows in mirrors, not in your own view.
                look._head = Part(PrimitiveType.Sphere, "Head", look.Root, new Vector3(0f, eyeHeight, 0f), Vector3.one * 0.24f * scale, skin);
                look._leftHand = Part(PrimitiveType.Sphere, "Left Hand", look.Root, Vector3.zero, Vector3.one * 0.08f, skin);
                look._rightHand = Part(PrimitiveType.Sphere, "Right Hand", look.Root, Vector3.zero, Vector3.one * 0.08f, skin);
            }

            look._plate = new GameObject("Plate").transform;
            look._plate.SetParent(look.Root, false);
            Part(PrimitiveType.Quad, "Plate", look._plate, Vector3.zero, new Vector3(PlateWidth, PlateDepth, 1f), look.Flat(new Color(0.08f, 0.08f, 0.1f), true));
            look._loadBar = Part(PrimitiveType.Quad, "Loading", look._plate, new Vector3(0f, -PlateDepth / 2f + 0.008f, -0.001f), new Vector3(0f, 0.012f, 1f),
                look.Flat(new Color(0.36f, 0.42f, 0.95f), true));
            look._label = new GameObject("Name").AddComponent<TextMesh>();
            look._label.transform.SetParent(look._plate, false);
            look._label.transform.localPosition = new Vector3(0f, 0.008f, -0.002f);
            look._label.text = displayName;
            look._label.anchor = TextAnchor.MiddleCenter;
            look._label.alignment = TextAlignment.Center;
            look._label.fontSize = 64;
            look._label.characterSize = 0.0085f;
            look._label.color = Color.white;
            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (font == null) try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { }
            if (font != null)
            {
                look._label.font = font;
                look._label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }
            look._loadStart = Time.time;
            return look;
        }

        public string Name
        {
            set { if (_label != null) _label.text = value; }
        }

        /// <summary>Places the look: the body under the head, turned the way the head faces, and the plate above the head.</summary>
        public void Pose(Vector3 feet, Vector3 head, Quaternion headRotation, bool hasLeft, Vector3 left, bool hasRight, Vector3 right)
        {
            if (Root == null) return;
            float yaw = headRotation.eulerAngles.y;
            Root.SetPositionAndRotation(new Vector3(head.x, feet.y, head.z), Quaternion.Euler(0f, yaw, 0f));
            if (_head != null) _head.SetPositionAndRotation(head, headRotation);
            if (_leftHand != null)
            {
                _leftHand.gameObject.SetActive(hasLeft);
                if (hasLeft) _leftHand.position = left;
            }
            if (_rightHand != null)
            {
                _rightHand.gameObject.SetActive(hasRight);
                if (hasRight) _rightHand.position = right;
            }
            // Readable from in front of the player, as in a mirror.
            _plate.SetPositionAndRotation(head + Vector3.up * PlateHeight, Quaternion.Euler(0f, yaw + 180f, 0f));
            float loaded = Mathf.Clamp01((Time.time - _loadStart) / LoadSeconds);
            bool loading = Time.time - _loadStart < LoadSeconds + 0.4f;
            _loadBar.gameObject.SetActive(loading);
            if (loading)
            {
                _loadBar.localScale = new Vector3(PlateWidth * loaded, 0.012f, 1f);
                _loadBar.localPosition = new Vector3(-PlateWidth / 2f + PlateWidth * loaded / 2f, -PlateDepth / 2f + 0.008f, -0.001f);
            }
        }

        public bool TryGetPlateTop(out Vector3 top)
        {
            top = _plate != null ? _plate.position + Vector3.up * (PlateDepth / 2f) : default;
            return _plate != null;
        }

        public void Destroy()
        {
            foreach (Material material in _materials) if (material != null) UnityEngine.Object.Destroy(material);
            _materials.Clear();
            if (Root != null) UnityEngine.Object.Destroy(Root.gameObject);
        }
    }

    /// <summary>
    /// Another client of the instance as this client sees it: the stand-in avatar posed from what that client sends,
    /// smoothed, and a capsule that world triggers, raycasts and collisions find as that player.
    /// </summary>
    internal sealed class SimulatedRemotePlayer : MonoBehaviour
    {
        private const float Smoothing = 14f;

        public VoltPlayerHandle Handle;
        public CapsuleCollider Collider { get; private set; }
        /// <summary>The local player's body, which walks through this one.</summary>
        public Collider PassThrough;
        public SimulatedPose Pose;
        private SimulatedPose _target;
        private bool _received;
        private SimulatedStandIn _look;

        public void Setup(VoltPlayerHandle handle)
        {
            Handle = handle;
            // Kinematic, so world triggers report the player; the local player walks through it (see IgnoreCollision).
            var body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            Collider = gameObject.AddComponent<CapsuleCollider>();
            Collider.height = 1.8f;
            Collider.radius = 0.3f;
            Collider.center = new Vector3(0f, 0.9f, 0f);
            Pose = new SimulatedPose { HeadRotation = Quaternion.identity, LeftRotation = Quaternion.identity, RightRotation = Quaternion.identity, EyeHeight = 1.6f, Grounded = true };
            _look = SimulatedStandIn.Build(transform, handle.DisplayName, new Color(0.76f, 0.62f, 0.58f), true, 1.6f);
            gameObject.SetActive(false); // hidden until its first pose arrives
        }

        public void Receive(in SimulatedPose pose)
        {
            _target = pose;
            if (!_received)
            {
                Pose = pose;
                _received = true;
                gameObject.SetActive(true);
                // Only once both are active: ignoring is forgotten when a collider is switched off.
                if (PassThrough != null) UnityEngine.Physics.IgnoreCollision(PassThrough, Collider);
            }
        }

        private void Update()
        {
            float t = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
            Pose.Feet = Vector3.Lerp(Pose.Feet, _target.Feet, t);
            Pose.Yaw = Mathf.LerpAngle(Pose.Yaw, _target.Yaw, t);
            Pose.Head = Vector3.Lerp(Pose.Head, _target.Head, t);
            Pose.HeadRotation = Quaternion.Slerp(Pose.HeadRotation, _target.HeadRotation, t);
            Pose.LeftHand = Vector3.Lerp(Pose.LeftHand, _target.LeftHand, t);
            Pose.LeftRotation = Quaternion.Slerp(Pose.LeftRotation, _target.LeftRotation, t);
            Pose.RightHand = Vector3.Lerp(Pose.RightHand, _target.RightHand, t);
            Pose.RightRotation = Quaternion.Slerp(Pose.RightRotation, _target.RightRotation, t);
            Pose.Velocity = _target.Velocity;
            Pose.EyeHeight = _target.EyeHeight > 0.2f ? _target.EyeHeight : 1.6f;
            Pose.Grounded = _target.Grounded;
            Pose.InVR = _target.InVR;
            Pose.Fps = _target.Fps;

            transform.SetPositionAndRotation(Pose.Feet, Quaternion.Euler(0f, Pose.Yaw, 0f));
            float height = Pose.EyeHeight + 0.15f;
            Collider.height = height;
            Collider.center = new Vector3(0f, height / 2f, 0f);
            _look.Pose(Pose.Feet, Pose.Head, Pose.HeadRotation, true, Pose.LeftHand, true, Pose.RightHand);
        }

        public bool TryGetPlateTop(out Vector3 top) => _look.TryGetPlateTop(out top);

        public bool TryGetTracking(VoltTrackingPoint point, out Vector3 position, out Quaternion rotation)
        {
            switch (point)
            {
                case VoltTrackingPoint.Head: position = Pose.Head; rotation = Pose.HeadRotation; return true;
                case VoltTrackingPoint.LeftHand: position = Pose.LeftHand; rotation = Pose.LeftRotation; return true;
                case VoltTrackingPoint.RightHand: position = Pose.RightHand; rotation = Pose.RightRotation; return true;
                case VoltTrackingPoint.Origin: position = Pose.Feet; rotation = Quaternion.Euler(0f, Pose.Yaw, 0f); return true;
                default: position = Pose.Feet; rotation = Quaternion.identity; return false;
            }
        }

        private void OnDestroy() => _look?.Destroy();
    }
}
