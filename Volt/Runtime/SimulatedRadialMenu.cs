using System;
using System.Collections.Generic;
using ParelVR.AvatarSDK;
using UnityEngine;
using UnityEngine.UI;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// ParelVR's radial menu with only the avatar's Expressions Menu on it: toggles, buttons, sub menus and the radial,
    /// two-axis and four-axis puppets, drawn like the in-game wheel. On desktop the mouse points and clicks; in VR the
    /// right stick points, the trigger chooses and A goes back.
    /// </summary>
    internal sealed class SimulatedRadialMenu
    {
        // Menu units: the wheel is 1000 across, as in the game.
        private const float R = 500f;
        private const float TileInner = 0.375f * R, TileOuter = 0.955f * R, TileGap = 6f;
        private const float HubRadius = 0.355f * R, IconRadius = 0.665f * R, OrbTravel = 0.22f * R;
        private const float DialInner = 0.58f * R, DialOuter = 0.86f * R;
        private const float CenterDeadzone = 0.32f, PuppetDeadzone = 0.12f;

        private static readonly Color Disc = new Color32(30, 31, 34, 242);
        private static readonly Color Tile = new Color32(46, 48, 52, 255);
        private static readonly Color TileOn = new Color32(66, 69, 74, 255);
        private static readonly Color Hover = new Color32(92, 95, 101, 255);
        private static readonly Color Hub = new Color32(40, 42, 46, 255);
        private static readonly Color HubHover = new Color32(66, 69, 74, 255);
        private static readonly Color Track = new Color32(46, 48, 52, 255);
        private static readonly Color Accent = new Color32(222, 224, 227, 255);
        private static readonly Color Label = new Color32(236, 237, 239, 255);
        private static readonly Color LabelDim = new Color32(150, 154, 161, 255);
        private static readonly Color IconTint = new Color32(240, 241, 243, 255);

        private enum Mode { Menu, Radial, TwoAxis, FourAxis }

        private sealed class Item
        {
            public ParelExpressionsMenu.Control Control;
            public RadialPiece Tile;
            public RadialPiece OnMark;
        }

        private sealed class PageEntry
        {
            public ParelExpressionsMenu Page;
            public string Title;
            public string OpenParameter;
        }

        public SimulatedPlayer Player;
        public bool IsOpen { get; private set; }

        private readonly SimulatedAvatar _avatar;
        private readonly ParelExpressionsMenu _rootPage;
        private readonly List<PageEntry> _stack = new List<PageEntry>();
        private readonly List<Item> _items = new List<Item>();
        private readonly List<GameObject> _built = new List<GameObject>();
        private GameObject _canvasObject;
        private Canvas _canvas;
        private RectTransform _wheel;
        private RadialPiece _hub, _orb, _dialFill, _padDot;
        private Text _hubLabel, _title, _value;
        private Font _font;
        private Mode _mode;
        private ParelExpressionsMenu.Control _puppet;
        private Vector2 _cursor;
        private int _hover = -1;

        private SimulatedRadialMenu(SimulatedAvatar avatar, ParelExpressionsMenu root)
        {
            _avatar = avatar;
            _rootPage = root;
        }

        public static SimulatedRadialMenu Create(SimulatedAvatar avatar, ParelExpressionsMenu root)
        {
            var menu = new SimulatedRadialMenu(avatar, root);
            menu.Build();
            return menu;
        }

        private void Build()
        {
            try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (_font == null) try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { }
            _canvasObject = new GameObject("[Avatar Simulator Radial Menu]");
            UnityEngine.Object.DontDestroyOnLoad(_canvasObject);
            _canvas = _canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32000;
            _wheel = NewRect("Wheel", _canvasObject.transform);
            _wheel.sizeDelta = new Vector2(1000f, 1000f);
            _canvasObject.SetActive(false);
        }

        // ── opening and pages ────────────────────────────────────────────────

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;
            _stack.Clear();
            _stack.Add(new PageEntry { Page = _rootPage, Title = "Expressions" });
            _mode = Mode.Menu;
            _canvasObject.SetActive(true);
            Player?.ReleaseMouse();
            Rebuild();
        }

        public void Close()
        {
            if (!IsOpen) return;
            ExitPuppet();
            while (_stack.Count > 0) PopPage();
            IsOpen = false;
            _canvasObject.SetActive(false);
            Player?.RestoreMouse();
        }

        private PageEntry Current => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        private void PushPage(ParelExpressionsMenu.Control control)
        {
            string parameter = control.parameter != null && !string.IsNullOrEmpty(control.parameter.name) ? control.parameter.name : null;
            if (parameter != null) _avatar.SetValue(parameter, control.value);
            _stack.Add(new PageEntry { Page = control.subMenu, Title = string.IsNullOrEmpty(control.name) ? "Sub Menu" : control.name, OpenParameter = parameter });
            Rebuild();
        }

        private void PopPage()
        {
            PageEntry top = Current;
            if (top == null) return;
            if (top.OpenParameter != null) _avatar.SetValue(top.OpenParameter, 0f);
            _stack.RemoveAt(_stack.Count - 1);
        }

        private void Back()
        {
            if (_mode != Mode.Menu)
            {
                ExitPuppet();
                Rebuild();
                return;
            }
            if (_stack.Count <= 1)
            {
                Close();
                return;
            }
            PopPage();
            Rebuild();
        }

        // ── input ────────────────────────────────────────────────────────────

        /// <summary>0 chooses, 1 goes back.</summary>
        public void Click(int button)
        {
            if (!IsOpen) return;
            if (button == 1) { Back(); return; }
            if (button != 0) return;
            if (_mode != Mode.Menu)
            {
                // A click ends a puppet, keeping what it set (the radial value stays, the sticks spring back).
                ExitPuppet();
                Rebuild();
                return;
            }
            if (_hover < 0)
            {
                Back();
                return;
            }
            if (_hover >= _items.Count) return;
            Use(_items[_hover].Control);
        }

        private static string Param(ParelExpressionsMenu.ControlParameter p) => p != null && !string.IsNullOrEmpty(p.name) ? p.name : null;

        private void Use(ParelExpressionsMenu.Control control)
        {
            string parameter = Param(control.parameter);
            switch (control.type)
            {
                case ParelExpressionsMenu.ControlType.Toggle:
                    if (parameter == null) return;
                    _avatar.SetValue(parameter, IsOn(control) ? 0f : control.value);
                    break;
                case ParelExpressionsMenu.ControlType.Button:
                    _avatar.PressButton(parameter, control.value);
                    break;
                case ParelExpressionsMenu.ControlType.SubMenu:
                    if (control.subMenu != null) PushPage(control);
                    break;
                case ParelExpressionsMenu.ControlType.RadialPuppet:
                    EnterPuppet(control, Mode.Radial);
                    break;
                case ParelExpressionsMenu.ControlType.TwoAxisPuppet:
                    EnterPuppet(control, Mode.TwoAxis);
                    break;
                case ParelExpressionsMenu.ControlType.FourAxisPuppet:
                    EnterPuppet(control, Mode.FourAxis);
                    break;
            }
        }

        private bool IsOn(ParelExpressionsMenu.Control control)
        {
            string parameter = Param(control.parameter);
            return parameter != null && Mathf.Abs(_avatar.GetValue(parameter) - control.value) < 0.01f;
        }

        private void EnterPuppet(ParelExpressionsMenu.Control control, Mode mode)
        {
            _puppet = control;
            _mode = mode;
            string parameter = Param(control.parameter);
            if (parameter != null) _avatar.SetValue(parameter, control.value);
            Rebuild();
        }

        private void ExitPuppet()
        {
            if (_puppet == null) return;
            string parameter = Param(_puppet.parameter);
            if (parameter != null) _avatar.SetValue(parameter, 0f);
            if (_mode == Mode.TwoAxis || _mode == Mode.FourAxis)
                for (int i = 0; i < 4; i++)
                {
                    string axis = _puppet.GetSubParameter(i);
                    if (axis != null) _avatar.SetValue(axis, 0f);
                }
            _puppet = null;
            _mode = Mode.Menu;
        }

        // ── per frame ────────────────────────────────────────────────────────

        public void Tick(float dt)
        {
            if (!IsOpen || Player == null) return;
            Camera view = Player.View;
            bool vr = Player.InVRNow;
            float pixels = Mathf.Min(Screen.height * 0.62f, 640f);
            if (vr && view != null)
            {
                if (_canvas.renderMode != RenderMode.WorldSpace)
                {
                    _canvas.renderMode = RenderMode.WorldSpace;
                    _canvas.worldCamera = view;
                }
                Transform eye = view.transform;
                _canvasObject.transform.SetPositionAndRotation(eye.position + eye.forward * 0.55f - eye.up * 0.08f, Quaternion.LookRotation(eye.forward, eye.up));
                _canvasObject.transform.localScale = Vector3.one * (0.36f / 1000f);
                _wheel.anchoredPosition = Vector2.zero;
                _wheel.localScale = Vector3.one;
                Player.VRMenuInput(out Vector2 stick, out bool choose, out bool back);
                _cursor = Vector2.ClampMagnitude(stick, 1f);
                if (choose) Click(0);
                else if (back) Click(1);
                if (!IsOpen) return;
            }
            else
            {
                if (_canvas.renderMode != RenderMode.ScreenSpaceOverlay) _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvasObject.transform.localScale = Vector3.one;
                _wheel.anchorMin = _wheel.anchorMax = new Vector2(0.5f, 0.5f);
                _wheel.anchoredPosition = Vector2.zero;
                _wheel.localScale = Vector3.one * (pixels / 1000f);
                Vector2 centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
                _cursor = Vector2.ClampMagnitude((Player.MousePosition() - centre) / (pixels / 2f), 1f);
            }

            if (_mode == Mode.Menu) UpdateHover();
            else UpdatePuppet();
            if (_orb != null) _orb.rectTransform.anchoredPosition = _cursor * (_mode == Mode.Menu ? OrbTravel : DialOuter);
        }

        private void UpdateHover()
        {
            int hover = -1;
            if (_cursor.magnitude > CenterDeadzone && _items.Count > 0)
            {
                float angle = AngleOf(_cursor);
                float step = 360f / _items.Count;
                hover = Mathf.FloorToInt(Mathf.Repeat(angle + step / 2f, 360f) / step) % _items.Count;
            }
            _hover = hover;
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                bool on = item.Control.type == ParelExpressionsMenu.ControlType.Toggle && IsOn(item.Control);
                Color tile = i == hover ? Hover : on ? TileOn : Tile;
                if (item.Tile.color != tile) item.Tile.color = tile;
                if (item.OnMark != null) item.OnMark.enabled = on;
            }
            Color hub = hover < 0 && _cursor.magnitude <= CenterDeadzone ? HubHover : Hub;
            if (_hub.color != hub) _hub.color = hub;
        }

        private void UpdatePuppet()
        {
            if (_puppet == null) return;
            switch (_mode)
            {
                case Mode.Radial:
                {
                    string parameter = _puppet.GetSubParameter(0);
                    if (parameter != null && _cursor.magnitude > PuppetDeadzone) _avatar.SetValue(parameter, AngleOf(_cursor) / 360f);
                    float value = parameter != null ? _avatar.GetValue(parameter) : 0f;
                    _dialFill.Sweep = Mathf.Clamp01(value) * 360f;
                    _dialFill.SetVerticesDirty();
                    _value.text = Mathf.RoundToInt(Mathf.Clamp01(value) * 100f) + "%";
                    break;
                }
                case Mode.TwoAxis:
                {
                    Vector2 stick = _cursor.magnitude > PuppetDeadzone ? _cursor : Vector2.zero;
                    SetAxis(0, stick.x);
                    SetAxis(1, stick.y);
                    _padDot.rectTransform.anchoredPosition = stick * DialOuter;
                    _value.text = stick.x.ToString("0.00") + "  " + stick.y.ToString("0.00");
                    break;
                }
                case Mode.FourAxis:
                {
                    Vector2 stick = _cursor.magnitude > PuppetDeadzone ? _cursor : Vector2.zero;
                    SetAxis(0, Mathf.Max(0f, stick.y));
                    SetAxis(1, Mathf.Max(0f, stick.x));
                    SetAxis(2, Mathf.Max(0f, -stick.y));
                    SetAxis(3, Mathf.Max(0f, -stick.x));
                    _padDot.rectTransform.anchoredPosition = stick * DialOuter;
                    _value.text = string.Empty;
                    break;
                }
            }
        }

        private void SetAxis(int index, float value)
        {
            string parameter = _puppet.GetSubParameter(index);
            if (parameter != null) _avatar.SetValue(parameter, value);
        }

        // ── drawing ──────────────────────────────────────────────────────────

        private void Rebuild()
        {
            foreach (GameObject go in _built) if (go != null) UnityEngine.Object.Destroy(go);
            _built.Clear();
            _items.Clear();
            _hover = -1;
            _dialFill = _padDot = null;

            Piece("Disc", 0f, R, 0f, 360f, 0f, Disc);
            PageEntry page = Current;
            if (_mode == Mode.Menu) BuildPage(page);
            else BuildPuppet();

            _hub = Piece("Hub", 0f, HubRadius, 0f, 360f, 0f, Hub);
            string hubText = _mode != Mode.Menu ? "Done" : _stack.Count > 1 ? "Back" : "Close";
            _hubLabel = AddText("Hub Label", hubText, new Vector2(0f, _mode == Mode.Menu ? 0f : -70f), 34, LabelDim);
            _value = AddText("Value", string.Empty, new Vector2(0f, 10f), 46, Label);
            _title = AddText("Title", _mode == Mode.Menu ? page?.Title ?? "Expressions" : _puppet?.name ?? string.Empty, new Vector2(0f, R + 46f), 40, Label);
            _orb = Piece("Cursor", 0f, 22f, 0f, 360f, 0f, Accent);
        }

        private void BuildPage(PageEntry page)
        {
            List<ParelExpressionsMenu.Control> controls = page?.Page != null ? page.Page.controls : null;
            int count = 0;
            if (controls != null)
                foreach (ParelExpressionsMenu.Control control in controls)
                    if (control != null && count < ParelExpressionsMenu.MaxControls) count++;
            if (count == 0)
            {
                Piece("Empty", TileInner, TileOuter, 0f, 360f, 0f, Tile);
                AddText("Empty Label", _rootPage == null ? "This avatar has no Expressions Menu" : "This page is empty", new Vector2(0f, IconRadius), 30, LabelDim);
                return;
            }
            float step = 360f / count;
            int index = 0;
            foreach (ParelExpressionsMenu.Control control in controls)
            {
                if (control == null || index >= count) continue;
                float centre = index * step;
                var item = new Item { Control = control };
                item.Tile = Piece("Tile " + index, TileInner, TileOuter, centre - step / 2f, step, count > 1 ? TileGap : 0f, Tile);
                if (control.type == ParelExpressionsMenu.ControlType.Toggle)
                    item.OnMark = Piece("On " + index, TileOuter - 14f, TileOuter - 4f, centre - step / 2f, step, count > 1 ? TileGap + 30f : 0f, Accent);
                Vector2 at = Dir(centre) * IconRadius;
                if (control.icon != null)
                {
                    RectTransform icon = NewRect("Icon " + index, _wheel);
                    _built.Add(icon.gameObject);
                    var image = icon.gameObject.AddComponent<RawImage>();
                    image.texture = control.icon;
                    image.color = IconTint;
                    image.raycastTarget = false;
                    icon.sizeDelta = new Vector2(96f, 96f);
                    icon.anchoredPosition = at + new Vector2(0f, 22f);
                    AddText("Label " + index, Short(control.name), at + new Vector2(0f, -52f), 26, Label);
                }
                else AddText("Label " + index, Short(control.name) + "\n<size=20>" + KindOf(control) + "</size>", at, 28, Label);
                _items.Add(item);
                index++;
            }
        }

        private void BuildPuppet()
        {
            if (_puppet == null) return;
            if (_mode == Mode.Radial)
            {
                Piece("Dial", DialInner, DialOuter, 0f, 360f, 0f, Track);
                _dialFill = Piece("Dial Fill", DialInner, DialOuter, 0f, 0f, 0f, Accent);
                return;
            }
            Piece("Pad", HubRadius + 8f, DialOuter + 40f, 0f, 360f, 0f, Track);
            for (int i = 0; i < 4; i++)
            {
                ParelExpressionsMenu.Label label = _puppet.GetLabel(i);
                string name = label != null && !string.IsNullOrEmpty(label.name) ? label.name : _mode == Mode.FourAxis ? _puppet.GetSubParameter(i) ?? string.Empty : string.Empty;
                AddText("Direction " + i, Short(name), Dir(i * 90f) * (DialOuter + 4f), 26, LabelDim);
            }
            _padDot = Piece("Stick", 0f, 30f, 0f, 360f, 0f, Accent);
        }

        private static string KindOf(ParelExpressionsMenu.Control control)
        {
            switch (control.type)
            {
                case ParelExpressionsMenu.ControlType.Toggle: return "Toggle";
                case ParelExpressionsMenu.ControlType.Button: return "Button";
                case ParelExpressionsMenu.ControlType.SubMenu: return "Menu";
                case ParelExpressionsMenu.ControlType.RadialPuppet: return "Radial";
                case ParelExpressionsMenu.ControlType.TwoAxisPuppet: return "Two Axis";
                default: return "Four Axis";
            }
        }

        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length > 18 ? text.Substring(0, 17) + "…" : text;
        }

        private static Vector2 Dir(float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
        }

        private static float AngleOf(Vector2 v)
        {
            float a = Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;
            return a < 0f ? a + 360f : a;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            return rect;
        }

        private RadialPiece Piece(string name, float inner, float outer, float start, float sweep, float gap, Color colour)
        {
            RectTransform rect = NewRect(name, _wheel);
            rect.sizeDelta = new Vector2(outer * 2f, outer * 2f);
            _built.Add(rect.gameObject);
            var piece = rect.gameObject.AddComponent<RadialPiece>();
            piece.Inner = inner;
            piece.Outer = outer;
            piece.Start = start;
            piece.Sweep = sweep;
            piece.Gap = gap;
            piece.color = colour;
            piece.raycastTarget = false;
            return piece;
        }

        private Text AddText(string name, string text, Vector2 at, int size, Color colour)
        {
            RectTransform rect = NewRect(name, _wheel);
            rect.sizeDelta = new Vector2(300f, 120f);
            rect.anchoredPosition = at;
            _built.Add(rect.gameObject);
            var label = rect.gameObject.AddComponent<Text>();
            label.font = _font;
            label.text = text;
            label.fontSize = size;
            label.color = colour;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.supportRichText = true;
            label.raycastTarget = false;
            return label;
        }

        public void Destroy()
        {
            if (IsOpen) Close();
            if (_canvasObject != null) UnityEngine.Object.Destroy(_canvasObject);
        }
    }

    /// <summary>A flat annular sector (or disc, or ring): 0 degrees is 12 o'clock, clockwise. Edges fade over a unit or two.</summary>
    [AddComponentMenu("")]
    internal sealed class RadialPiece : MaskableGraphic
    {
        public float Inner, Outer = 100f, Start, Sweep = 360f, Gap;
        private const float Feather = 1.6f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (Outer <= Inner || Sweep <= 0.01f) return;
            Color32 solid = color;
            Color32 clear = solid;
            clear.a = 0;
            bool full = Sweep >= 359.99f;
            int segments = Mathf.Clamp(Mathf.CeilToInt(Sweep / 2.5f), 2, 160);
            float[] radii = { Mathf.Max(0f, Inner - Feather), Inner, Outer, Outer + Feather };
            Color32[] colours = { Inner > 0.01f ? clear : solid, solid, solid, clear };
            for (int s = 0; s <= segments; s++)
            {
                float t = s / (float)segments;
                for (int k = 0; k < 4; k++)
                {
                    float r = radii[k];
                    float inset = full || r < 0.01f ? 0f : Mathf.Min(Sweep / 2f, Mathf.Rad2Deg * (Gap * 0.5f / r));
                    float angle = Mathf.Lerp(Start + inset, Start + Sweep - inset, t) * Mathf.Deg2Rad;
                    vh.AddVert(new Vector3(Mathf.Sin(angle) * r, Mathf.Cos(angle) * r), colours[k], Vector4.zero);
                }
            }
            for (int s = 0; s < segments; s++)
                for (int k = 0; k < 3; k++)
                {
                    int a = s * 4 + k, b = a + 1, c = a + 4, d = a + 5;
                    vh.AddTriangle(a, c, b);
                    vh.AddTriangle(b, c, d);
                }
        }
    }
}
