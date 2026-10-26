using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// Points at world-space UI with a ray (the desktop crosshair or a VR hand) and drives it through the EventSystem
    /// the way a mouse would: hover, press, click, drag, scroll and selection. The scene's own input modules are
    /// switched off while the simulator runs, so nothing clicks twice.
    /// </summary>
    internal sealed class SimulatedUIPointer
    {
        private static readonly List<Canvas> Canvases = new List<Canvas>();
        private static float _canvasesFound = float.MinValue;
        private static SimulatedRaycaster _raycaster;

        private readonly int _id;
        private PointerEventData _data;
        private GameObject _hover;
        private bool _wasDown;

        public SimulatedUIPointer(int id) { _id = id; }

        /// <summary>What the last Process hit, and where.</summary>
        public GameObject Target { get; private set; }
        public Vector3 Point { get; private set; }
        public float Distance { get; private set; } = float.PositiveInfinity;

        private static EventSystem _system;
        private static float _modulesChecked = float.MinValue;

        internal static EventSystem EventSystemFor()
        {
            if (_system == null) _system = EventSystem.current != null ? EventSystem.current : UnityEngine.Object.FindAnyObjectByType<EventSystem>();
            if (_system == null) _system = new GameObject("[Volt EventSystem]").AddComponent<EventSystem>();
            // The simulator is the pointer now; a module of the scene would read the locked mouse as a second one.
            // Checked now and then, for modules scripts switch on later.
            if (Time.unscaledTime - _modulesChecked > 2f)
            {
                _modulesChecked = Time.unscaledTime;
                foreach (BaseInputModule module in UnityEngine.Object.FindObjectsByType<BaseInputModule>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    module.enabled = false;
            }
            EventSystem system = _system;
            if (_raycaster == null)
            {
                var go = new GameObject("[Volt UI Raycaster]") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                _raycaster = go.AddComponent<SimulatedRaycaster>();
            }
            return system;
        }

        internal static void ForgetCanvases() => _canvasesFound = float.MinValue;

        private static void FindCanvases()
        {
            if (Time.unscaledTime - _canvasesFound < 1f) return;
            _canvasesFound = Time.unscaledTime;
            Canvases.Clear();
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (canvas.rootCanvas != null && canvas.rootCanvas.renderMode == RenderMode.WorldSpace) Canvases.Add(canvas);
        }

        private static bool BlockedByGroup(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var group = t.GetComponent<CanvasGroup>();
                if (group == null || !group.enabled) continue;
                if (!group.blocksRaycasts) return true;
                if (group.ignoreParentGroups) return false;
            }
            return false;
        }

        /// <summary>The nearest raycast-target UI element on the ray, as a mouse over it would find it.</summary>
        public float Cast(Ray ray, float maxDistance, out GameObject target, out Vector3 point, out Vector2 screen)
        {
            target = null;
            point = default;
            screen = default;
            Camera view = Camera.main;
            FindCanvases();
            float best = maxDistance;
            int bestDepth = int.MinValue;
            foreach (Canvas canvas in Canvases)
            {
                if (canvas == null || !canvas.isActiveAndEnabled) continue;
                Transform t = canvas.transform;
                // Only from the front, as it is read.
                if (Vector3.Dot(ray.direction, t.forward) <= 0f) continue;
                if (!new Plane(t.forward, t.position).Raycast(ray, out float distance) || distance < 0f || distance > best + 0.0001f) continue;
                Vector3 hit = ray.GetPoint(distance);
                Vector3 onScreen = view != null ? view.WorldToScreenPoint(hit) : Vector3.zero;
                bool useScreen = view != null && onScreen.z > 0f;
                Graphic top = null;
                IList<Graphic> graphics = GraphicRegistry.GetRaycastableGraphicsForCanvas(canvas);
                for (int i = 0; i < graphics.Count; i++)
                {
                    Graphic g = graphics[i];
                    if (g == null || !g.raycastTarget || !g.isActiveAndEnabled || g.canvasRenderer.cull) continue;
                    if (top != null && g.depth <= top.depth) continue;
                    if (useScreen)
                    {
                        if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, onScreen, view) || !g.Raycast(onScreen, view)) continue;
                    }
                    else if (!g.rectTransform.rect.Contains(g.rectTransform.InverseTransformPoint(hit))) continue;
                    if (BlockedByGroup(g.transform)) continue;
                    top = g;
                }
                if (top == null) continue; // an empty part of a canvas does not stop the ray
                if (distance < best - 0.0001f || top.depth > bestDepth)
                {
                    best = distance;
                    bestDepth = top.depth;
                    target = top.gameObject;
                    point = hit;
                    screen = onScreen;
                }
            }
            return target != null ? best : float.PositiveInfinity;
        }

        private static GameObject CommonRoot(GameObject a, GameObject b)
        {
            if (a == null || b == null) return null;
            for (Transform x = a.transform; x != null; x = x.parent)
                for (Transform y = b.transform; y != null; y = y.parent)
                    if (x == y) return x.gameObject;
            return null;
        }

        /// <summary>One frame of pointing: the ray, whether the button is down, and wheel movement.</summary>
        public void Process(Ray ray, bool down, float maxDistance, Vector2 scroll)
        {
            EventSystem system = EventSystemFor();
            Distance = Cast(ray, maxDistance, out GameObject target, out Vector3 point, out Vector2 screen);
            Target = target;
            Point = point;
            if (_data == null) _data = new PointerEventData(system) { pointerId = _id, button = PointerEventData.InputButton.Left };
            _data.Reset();
            _data.delta = screen - _data.position;
            _data.position = screen;
            _data.scrollDelta = scroll;
            _data.pointerCurrentRaycast = new RaycastResult
            {
                gameObject = target, module = _raycaster, distance = Distance, worldPosition = point, screenPosition = screen,
                worldNormal = target != null ? -target.transform.forward : Vector3.zero
            };
            _raycaster.Camera = Camera.main;

            if (target != _hover)
            {
                GameObject common = CommonRoot(_hover, target);
                for (Transform t = _hover != null ? _hover.transform : null; t != null && t.gameObject != common; t = t.parent)
                    ExecuteEvents.Execute(t.gameObject, _data, ExecuteEvents.pointerExitHandler);
                for (Transform t = target != null ? target.transform : null; t != null && t.gameObject != common; t = t.parent)
                    ExecuteEvents.Execute(t.gameObject, _data, ExecuteEvents.pointerEnterHandler);
                _data.pointerEnter = target;
                _hover = target;
            }

            if (down && !_wasDown)
            {
                _data.pressPosition = screen;
                _data.pointerPressRaycast = _data.pointerCurrentRaycast;
                _data.eligibleForClick = true;
                _data.useDragThreshold = true;
                _data.delta = Vector2.zero;
                // As the input modules do: pressing anything but the selected object (or nothing) ends its selection.
                GameObject selectable = target != null ? ExecuteEvents.GetEventHandler<ISelectHandler>(target) : null;
                if (selectable != system.currentSelectedGameObject) system.SetSelectedGameObject(null, _data);
                if (target != null)
                {
                    GameObject press = ExecuteEvents.ExecuteHierarchy(target, _data, ExecuteEvents.pointerDownHandler);
                    GameObject click = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
                    _data.pointerPress = press != null ? press : click;
                    _data.rawPointerPress = target;
                    _data.pointerClick = click;
                    _data.clickTime = Time.unscaledTime;
                    _data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(target);
                    if (_data.pointerDrag != null) ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.initializePotentialDrag);
                }
            }
            else if (down && _data.pointerDrag != null)
            {
                if (!_data.dragging && (screen - _data.pressPosition).sqrMagnitude >= system.pixelDragThreshold * system.pixelDragThreshold)
                {
                    ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.beginDragHandler);
                    _data.dragging = true;
                }
                if (_data.dragging) ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.dragHandler);
            }
            else if (!down && _wasDown)
            {
                if (_data.pointerPress != null) ExecuteEvents.Execute(_data.pointerPress, _data, ExecuteEvents.pointerUpHandler);
                GameObject clickNow = target != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) : null;
                if (_data.pointerClick != null && _data.pointerClick == clickNow && _data.eligibleForClick)
                    ExecuteEvents.Execute(_data.pointerClick, _data, ExecuteEvents.pointerClickHandler);
                if (_data.dragging && _data.pointerDrag != null)
                {
                    if (target != null) ExecuteEvents.ExecuteHierarchy(target, _data, ExecuteEvents.dropHandler);
                    ExecuteEvents.Execute(_data.pointerDrag, _data, ExecuteEvents.endDragHandler);
                }
                _data.eligibleForClick = false;
                _data.pointerPress = null;
                _data.rawPointerPress = null;
                _data.pointerClick = null;
                _data.pointerDrag = null;
                _data.dragging = false;
            }

            if (target != null && scroll.sqrMagnitude > 0f)
                ExecuteEvents.ExecuteHierarchy(target, _data, ExecuteEvents.scrollHandler);
            _wasDown = down;
        }

        /// <summary>Lets go of everything: the pointer left the UI for the world, or the simulator stopped.</summary>
        public void Release(Ray ray) => Process(new Ray(ray.origin, Vector3.zero), false, 0f, Vector2.zero);
    }

    /// <summary>Gives pointer events a camera, so sliders and scroll views turn the press into a spot on themselves.</summary>
    internal sealed class SimulatedRaycaster : BaseRaycaster
    {
        public Camera Camera;
        public override Camera eventCamera => Camera != null ? Camera : Camera.main;
        public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList) { }
    }

    /// <summary>
    /// The on-screen keyboard: shown in front of the player while a text field of the world is being typed in, so it
    /// can be used in VR (and with the crosshair on desktop, where the physical keyboard also works). It types through
    /// the field's own key handling, for legacy and TextMeshPro fields alike.
    /// </summary>
    internal sealed class SimulatedKeyboard
    {
        private static readonly string[] Rows = { "1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,.?" };
        private static readonly Color Panel = new Color(0.09f, 0.09f, 0.11f, 0.96f);
        private static readonly Color Key = new Color(0.19f, 0.19f, 0.23f, 1f);
        private static readonly Color KeyHover = new Color(0.29f, 0.29f, 0.35f, 1f);
        private static readonly Color Accent = new Color(0.36f, 0.42f, 0.95f, 1f);

        private readonly Dictionary<GameObject, string> _keys = new Dictionary<GameObject, string>();
        private readonly List<Text> _letters = new List<Text>();
        private GameObject _root;
        private Text _preview;
        private Component _field;
        private GameObject _hover;
        private bool _shift;
        private float _liveAt;
        private static Font _font;

        /// <summary>The text field being typed in, or null.</summary>
        public Component Field => _field;
        public bool Visible => _root != null && _root.activeSelf;

        private static Font TheFont
        {
            get
            {
                if (_font != null) return _font;
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
                if (_font == null) try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { }
                return _font;
            }
        }

        /// <summary>The focused text field of the selected object: a legacy InputField or a TMP_InputField.</summary>
        public static Component FocusedField()
        {
            EventSystem system = EventSystem.current;
            GameObject selected = system != null ? system.currentSelectedGameObject : null;
            if (selected == null) return null;
            var legacy = selected.GetComponent<InputField>();
            if (legacy != null) return legacy.isFocused || legacy.IsActive() ? legacy : null;
            foreach (Component c in selected.GetComponents<Component>())
                if (c != null && c.GetType().FullName == "TMPro.TMP_InputField") return c;
            return null;
        }

        /// <summary>Called every frame with where the player's head is; shows, places and refreshes the keyboard.</summary>
        public void Update(Transform head)
        {
            if (_root != null && _root.activeSelf && EscapePressed())
            {
                _field = null;
                _root.SetActive(false);
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
                return;
            }

            Component field = FocusedField();
            if (field != _field)
            {
                _field = field;
                if (field != null && head != null) Show(head);
                else if (_root != null) _root.SetActive(false);
            }
            if (_field == null || _preview == null) return;
            string text = Read(_field);
            _preview.text = string.IsNullOrEmpty(text) ? "<color=#8a8a99>Type here</color>" : text + "|";
            // The selected field is driven by the simulator; legacy and TMP fields read their keys in this event.
            if (EventSystem.current != null)
                ExecuteEvents.Execute(EventSystem.current.currentSelectedGameObject, new BaseEventData(EventSystem.current), ExecuteEvents.updateSelectedHandler);
        }

        private void Show(Transform head)
        {
            if (_root == null) Build();
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = head.up;
            forward.Normalize();
            _root.transform.position = head.position + forward * 0.62f + Vector3.down * 0.36f;
            _root.transform.rotation = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(20f, 0f, 0f);
            _root.SetActive(true);
            Canvas.ForceUpdateCanvases();
            SimulatedUIPointer.ForgetCanvases();
            // The click that focused the field ends over the keys (the middle one is "h"): it must not type.
            _liveAt = Time.unscaledTime + 0.35f;
        }

        private static bool EscapePressed()
        {
            try
            {
                Type keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                object keyboard = keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                object key = keyboard?.GetType().GetProperty("escapeKey")?.GetValue(keyboard);
                object pressed = key?.GetType().GetProperty("wasPressedThisFrame")?.GetValue(key);
                return pressed is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        public bool Owns(GameObject target) => target != null && _keys.ContainsKey(target);

        /// <summary>The pointer is over a key (or not): only the key under it lights up.</summary>
        public void Hover(GameObject target)
        {
            if (_hover == target) return;
            if (_hover != null && _hover.TryGetComponent(out Image was)) was.color = _keys.TryGetValue(_hover, out string k) && k == "done" ? Accent : Key;
            _hover = Owns(target) ? target : null;
            if (_hover != null && _hover.TryGetComponent(out Image now) && _keys[_hover] != "done") now.color = KeyHover;
        }

        public void Press(GameObject target)
        {
            if (_field == null || !_keys.TryGetValue(target, out string key) || Time.unscaledTime < _liveAt) return;
            switch (key)
            {
                case "shift":
                    _shift = !_shift;
                    foreach (Text letter in _letters) letter.text = _shift ? letter.text.ToUpperInvariant() : letter.text.ToLowerInvariant();
                    return;
                case "back": Send(_field, Event.KeyboardEvent("backspace")); break;
                case "space": Send(_field, Character(' ')); break;
                case "done":
                    Send(_field, Event.KeyboardEvent("return"));
                    if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
                    break;
                default:
                    char c = key[0];
                    Send(_field, Character(_shift ? char.ToUpperInvariant(c) : c));
                    if (_shift) Press(_keys.FirstKey("shift"));
                    break;
            }
        }

        private static Event Character(char c) => new Event { type = EventType.KeyDown, character = c, keyCode = KeyCode.None };

        // Both field types take keys through ProcessEvent; the TMP one is reached by name so the SDK needs no TextMeshPro.
        private static void Send(Component field, Event key)
        {
            if (field is InputField legacy)
            {
                legacy.ProcessEvent(key);
                legacy.ForceLabelUpdate();
                return;
            }
            Type type = field.GetType();
            try
            {
                type.GetMethod("ProcessEvent", new[] { typeof(Event) })?.Invoke(field, new object[] { key });
                type.GetMethod("ForceLabelUpdate", Type.EmptyTypes)?.Invoke(field, null);
            }
            catch (TargetInvocationException e) { Debug.LogWarning("[Volt] The keyboard could not type into " + field.name + ": " + e.InnerException?.Message); }
        }

        private static string Read(Component field)
        {
            if (field is InputField legacy) return legacy.text;
            return field.GetType().GetProperty("text")?.GetValue(field) as string ?? string.Empty;
        }

        private void Build()
        {
            _root = new GameObject("[Volt Keyboard]") { layer = 5 };
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 32000;
            var rect = (RectTransform)_root.transform;
            rect.sizeDelta = new Vector2(1000f, 480f);
            rect.localScale = Vector3.one * 0.0006f;

            Image background = Box(_root.transform, "Panel", Vector2.zero, rect.sizeDelta, Panel);
            background.raycastTarget = true;
            _preview = Label(Box(_root.transform, "Preview", new Vector2(0f, 190f), new Vector2(960f, 70f), Key).transform, string.Empty, 34, TextAnchor.MiddleLeft);
            _preview.supportRichText = true;

            const float size = 88f, gap = 8f;
            for (int r = 0; r < Rows.Length; r++)
            {
                string row = Rows[r];
                float width = row.Length * size + (row.Length - 1) * gap;
                for (int i = 0; i < row.Length; i++)
                {
                    var at = new Vector2(-width / 2f + size / 2f + i * (size + gap), 100f - r * (size + gap));
                    Image key = Box(_root.transform, "Key " + row[i], at, new Vector2(size, size), Key);
                    Text letter = Label(key.transform, row[i].ToString(), 36, TextAnchor.MiddleCenter);
                    if (char.IsLetter(row[i])) _letters.Add(letter);
                    _keys[key.gameObject] = row[i].ToString();
                }
            }
            float bottom = 100f - Rows.Length * (size + gap);
            AddWide("shift", "Shift", new Vector2(-340f, bottom), 180f, Key);
            AddWide("space", "Space", new Vector2(-30f, bottom), 400f, Key);
            AddWide("back", "Delete", new Vector2(270f, bottom), 160f, Key);
            AddWide("done", "Done", new Vector2(410f, bottom), 100f, Accent);
            _root.SetActive(false);
        }

        private void AddWide(string key, string text, Vector2 at, float width, Color colour)
        {
            Image box = Box(_root.transform, "Key " + text, at, new Vector2(width, 88f), colour);
            Label(box.transform, text, 30, TextAnchor.MiddleCenter);
            _keys[box.gameObject] = key;
        }

        private static Image Box(Transform parent, string name, Vector2 at, Vector2 size, Color colour)
        {
            var go = new GameObject(name) { layer = 5 };
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchoredPosition = at;
            rect.sizeDelta = size;
            var image = go.AddComponent<Image>();
            image.color = colour;
            return image;
        }

        private static Text Label(Transform parent, string text, int size, TextAnchor anchor)
        {
            var go = new GameObject("Text") { layer = 5 };
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(16f, 0f);
            rect.offsetMax = new Vector2(-16f, 0f);
            var label = go.AddComponent<Text>();
            label.font = TheFont;
            label.text = text;
            label.fontSize = size;
            label.alignment = anchor;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }
    }

    internal static class SimulatedKeyboardExtensions
    {
        public static GameObject FirstKey(this Dictionary<GameObject, string> keys, string name)
        {
            foreach (KeyValuePair<GameObject, string> pair in keys)
                if (pair.Value == name) return pair.Key;
            return null;
        }
    }
}
