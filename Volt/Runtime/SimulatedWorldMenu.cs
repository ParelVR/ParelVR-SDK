using System;
using ParelVR.VoltHost;
using ParelVR.WorldSDK;
using UnityEngine;

namespace ParelVR.Volt.Simulator
{
    /// <summary>
    /// The world's menu (its ParelVR World Menu) in the simulator: the same values, actions and script events as in
    /// ParelVR, shown in a plain window instead of ParelVR's main menu. M (or the button at the top right) opens it;
    /// the page shows the menu's pages, controls and Back as the game does.
    /// </summary>
    internal sealed class SimulatedWorldMenu : MonoBehaviour, IVoltWorldMenuService
    {
        private ParelWorldMenuState _state;
        private bool _open;
        private CursorLockMode _cursorBefore;
        private Vector2 _scroll;
        private Rect _window = new Rect(20f, 60f, 440f, 540f);

        public event Action<int, string, float> Used;

        public void Setup(UnityEngine.SceneManagement.Scene scene)
        {
            ParelWorldMenu menu = ParelWorldMenu.Find(scene);
            if (menu == null) return;
            _state = new ParelWorldMenuState(menu);
            _state.Used += (kind, id, value) => Used?.Invoke((int)kind, id, value);
            _state.OpenRequested += page => SetOpen(true);
            Debug.Log("[Volt Simulator] World Menu '" + _state.Name + "': press M to open it.");
        }

        public bool IsAvailable => _state != null;
        public bool GetToggle(string id) => _state != null && _state.GetToggle(id);
        public void SetToggle(string id, bool on) => _state?.SetToggle(id, on);
        public float GetSlider(string id) => _state != null ? _state.GetSlider(id) : 0f;
        public void SetSlider(string id, float value) => _state?.SetSlider(id, value);
        public int GetOption(string id) => _state != null ? _state.GetOption(id) : 0;
        public void SetOption(string id, int index) => _state?.SetOption(id, index);
        public void SetLabel(string id, string text) => _state?.SetLabel(id, string.IsNullOrEmpty(text) ? null : text);
        public void SetDescription(string id, string text) => _state?.SetLine(id, string.IsNullOrEmpty(text) ? null : text);
        public void SetVisible(string id, bool visible) => _state?.SetVisible(id, visible);
        public void SetInteractable(string id, bool interactable) => _state?.SetInteractable(id, interactable);
        public void Open(string pageId) => _state?.RequestOpen(pageId);

        private void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            // The pointer is needed to use the window; the player's look comes back when it closes.
            if (open) { _cursorBefore = Cursor.lockState; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else { Cursor.lockState = _cursorBefore; Cursor.visible = _cursorBefore != CursorLockMode.Locked; }
        }

        private void OnGUI()
        {
            if (_state == null) return;
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.M) { SetOpen(!_open); e.Use(); }
            if (!_open)
            {
                if (GUI.Button(new Rect(Screen.width - 190f, 12f, 178f, 30f), _state.Name + " (M)")) SetOpen(true);
                return;
            }
            _window = GUILayout.Window(0x57A1, _window, DrawWindow, _state.Name);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            foreach (ParelWorldMenuPage page in _state.ListedPages)
            {
                bool chosen = page == _state.Page;
                GUI.color = chosen ? new Color(0.55f, 0.9f, 1f) : Color.white;
                if (GUILayout.Button(string.IsNullOrEmpty(page.title) ? page.id : page.title)) _state.Open(page, false);
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>" + (_state.Page != null ? _state.Page.title : string.Empty) + "</b>", new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15 });
            GUILayout.FlexibleSpace();
            if (_state.CanGoBack && GUILayout.Button("Back", GUILayout.Width(70f))) _state.Back();
            if (GUILayout.Button("Close", GUILayout.Width(70f))) SetOpen(false);
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (ParelWorldMenuControl c in _state.Controls(_state.Page))
            {
                GUI.enabled = _state.Interactable(c);
                string label = _state.Label(c);
                string line = _state.Line(c);
                switch (c.type)
                {
                    case ParelWorldMenuControlType.Label:
                        GUILayout.Label("<b>" + label + "</b>", new GUIStyle(GUI.skin.label) { richText = true });
                        break;
                    case ParelWorldMenuControlType.Toggle:
                        bool on = _state.IsOn(c);
                        if (GUILayout.Toggle(on, " " + label) != on) _state.Toggle(c);
                        break;
                    case ParelWorldMenuControlType.Slider:
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(label, GUILayout.Width(150f));
                        float v = GUILayout.HorizontalSlider(_state.ValueOf(c), Mathf.Min(c.min, c.max), Mathf.Max(c.min, c.max));
                        if (!Mathf.Approximately(v, _state.ValueOf(c))) _state.Slide(c, v);
                        GUILayout.Label(_state.ValueText(c), GUILayout.Width(60f));
                        GUILayout.EndHorizontal();
                        break;
                    case ParelWorldMenuControlType.Options:
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(label, GUILayout.Width(150f));
                        if (GUILayout.Button("<", GUILayout.Width(28f))) _state.Step(c, -1);
                        GUILayout.Label(_state.OptionText(c), new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
                        if (GUILayout.Button(">", GUILayout.Width(28f))) _state.Step(c, 1);
                        GUILayout.EndHorizontal();
                        break;
                    case ParelWorldMenuControlType.Button:
                        if (GUILayout.Button(label)) _state.Press(c);
                        break;
                    case ParelWorldMenuControlType.PageLink:
                        if (GUILayout.Button(label + "  >")) _state.Press(c);
                        break;
                }
                if (!string.IsNullOrEmpty(line) && c.type != ParelWorldMenuControlType.Label)
                    GUILayout.Label("<size=11><color=#a0a8b4>" + line + "</color></size>", new GUIStyle(GUI.skin.label) { richText = true });
                GUI.enabled = true;
                GUILayout.Space(4f);
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void OnDestroy()
        {
            if (_open) SetOpen(false);
        }
    }
}
