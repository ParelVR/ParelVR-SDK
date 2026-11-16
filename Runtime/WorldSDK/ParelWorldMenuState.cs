using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// The live values of a world's menu for the local player (each player has their own), shared by ParelVR and the
    /// SDK's simulator: the toggles, sliders and option switchers with what they do without code, plus what scripts
    /// changed (names, visibility, whether a control can be used). The menu's page draws from it; scripts read and
    /// set it through VoltWorldMenu and hear the player's use of it through <see cref="Used"/>.
    /// </summary>
    public sealed class ParelWorldMenuState
    {
        public enum Use { Button = 0, Toggle = 1, Slider = 2, Option = 3 }

        private sealed class Entry
        {
            public ParelWorldMenuControl Control;
            public bool On;
            public float Value;
            public int Option;
            public string Label;      // set by a script; null = the menu's own
            public string Line;
            public bool Hidden;
            public bool Locked;
        }

        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<ParelWorldMenuControl, Entry> _byControl = new Dictionary<ParelWorldMenuControl, Entry>();

        public ParelWorldMenu Menu { get; }
        /// <summary>The page open in the menu.</summary>
        public ParelWorldMenuPage Page { get; private set; }
        private readonly Stack<ParelWorldMenuPage> _history = new Stack<ParelWorldMenuPage>();

        /// <summary>The player used a control (kind, control id, value: toggle 0/1, slider value, option index).</summary>
        public event Action<Use, string, float> Used;
        /// <summary>Something the page shows changed (a value, a name, visibility, the open page).</summary>
        public event Action Changed;
        /// <summary>A script asked for the menu to be opened on a page.</summary>
        public event Action<ParelWorldMenuPage> OpenRequested;

        public ParelWorldMenuState(ParelWorldMenu menu)
        {
            Menu = menu;
            if (menu == null || menu.pages == null) return;
            foreach (ParelWorldMenuPage page in menu.pages)
            {
                if (page?.controls == null) continue;
                foreach (ParelWorldMenuControl c in page.controls)
                {
                    if (c == null) continue;
                    var e = new Entry
                    {
                        Control = c,
                        On = c.startOn,
                        Value = Clamp(c, c.startValue),
                        Option = c.options != null && c.options.Length > 0 ? Mathf.Clamp(c.startOption, 0, c.options.Length - 1) : 0
                    };
                    _byControl[c] = e;
                    if (!string.IsNullOrEmpty(c.id) && !_byId.ContainsKey(c.id)) _byId.Add(c.id, e);
                    Apply(e);
                }
            }
            Page = FirstPage();
        }

        /// <summary>The menu's name, never empty.</summary>
        public string Name => Menu != null && !string.IsNullOrWhiteSpace(Menu.menuName) ? Trim(Menu.menuName) : "World";

        /// <summary>The pages listed on the left (not hidden ones).</summary>
        public IEnumerable<ParelWorldMenuPage> ListedPages
        {
            get
            {
                if (Menu?.pages == null) yield break;
                int n = 0;
                foreach (ParelWorldMenuPage p in Menu.pages)
                {
                    if (p == null || p.hidden) continue;
                    if (++n > ParelWorldMenu.MaxPages) yield break;
                    yield return p;
                }
            }
        }

        /// <summary>The controls of a page the player sees (not hidden by a script), in order.</summary>
        public IEnumerable<ParelWorldMenuControl> Controls(ParelWorldMenuPage page)
        {
            if (page?.controls == null) yield break;
            int n = 0;
            foreach (ParelWorldMenuControl c in page.controls)
            {
                if (c == null || (_byControl.TryGetValue(c, out Entry e) && e.Hidden)) continue;
                if (++n > ParelWorldMenu.MaxControlsPerPage) yield break;
                yield return c;
            }
        }

        private ParelWorldMenuPage FirstPage()
        {
            if (Menu?.pages == null) return null;
            foreach (ParelWorldMenuPage p in Menu.pages) if (p != null) return p;
            return null;
        }

        // ── what the page shows ──────────────────────────────────────────────

        public string Label(ParelWorldMenuControl c) => _byControl.TryGetValue(c, out Entry e) && e.Label != null ? e.Label : Trim(c.label);
        public string Line(ParelWorldMenuControl c) => _byControl.TryGetValue(c, out Entry e) && e.Line != null ? e.Line : Trim(c.description);
        public bool Interactable(ParelWorldMenuControl c) => !_byControl.TryGetValue(c, out Entry e) || !e.Locked;
        public bool IsOn(ParelWorldMenuControl c) => _byControl.TryGetValue(c, out Entry e) && e.On;
        public float ValueOf(ParelWorldMenuControl c) => _byControl.TryGetValue(c, out Entry e) ? e.Value : 0f;
        public int OptionOf(ParelWorldMenuControl c) => _byControl.TryGetValue(c, out Entry e) ? e.Option : 0;

        /// <summary>The slider's value as text: 0-100 with % (show as percent), or the value with its unit.</summary>
        public string ValueText(ParelWorldMenuControl c)
        {
            float v = ValueOf(c);
            if (c.showAsPercent)
            {
                float t = Mathf.Approximately(c.max, c.min) ? 0f : Mathf.InverseLerp(c.min, c.max, v);
                return Mathf.RoundToInt(t * 100f) + "%";
            }
            string number = c.wholeNumbers || Mathf.Abs(c.max - c.min) >= 20f ? Mathf.RoundToInt(v).ToString() : v.ToString("0.0#");
            return number + (string.IsNullOrEmpty(c.unit) ? string.Empty : (c.unit.Length > 1 ? " " : string.Empty) + Trim(c.unit));
        }

        public string OptionText(ParelWorldMenuControl c)
        {
            if (c.options == null || c.options.Length == 0) return string.Empty;
            return Trim(c.options[Mathf.Clamp(OptionOf(c), 0, c.options.Length - 1)]);
        }

        // ── the player ───────────────────────────────────────────────────────

        public void Press(ParelWorldMenuControl c)
        {
            if (c == null || !Interactable(c)) return;
            if (c.type == ParelWorldMenuControlType.PageLink) { Open(Menu.FindPage(c.page), true); return; }
            if (c.type != ParelWorldMenuControlType.Button) return;
            foreach (GameObject o in c.objects ?? Array.Empty<GameObject>()) if (o != null) o.SetActive(!o.activeSelf);
            Used?.Invoke(Use.Button, c.id, 1f);
        }

        public void Toggle(ParelWorldMenuControl c)
        {
            if (c == null || !Interactable(c) || !_byControl.TryGetValue(c, out Entry e)) return;
            e.On = !e.On;
            Apply(e);
            Changed?.Invoke();
            Used?.Invoke(Use.Toggle, c.id, e.On ? 1f : 0f);
        }

        public void Slide(ParelWorldMenuControl c, float value)
        {
            if (c == null || !Interactable(c) || !_byControl.TryGetValue(c, out Entry e)) return;
            float v = Clamp(c, value);
            if (Mathf.Approximately(v, e.Value)) return;
            e.Value = v;
            Apply(e);
            Used?.Invoke(Use.Slider, c.id, v);
        }

        public void Step(ParelWorldMenuControl c, int by)
        {
            if (c == null || !Interactable(c) || !_byControl.TryGetValue(c, out Entry e) || c.options == null || c.options.Length == 0) return;
            int n = Mathf.Min(c.options.Length, ParelWorldMenu.MaxOptions);
            e.Option = ((e.Option + by) % n + n) % n;
            Apply(e);
            Changed?.Invoke();
            Used?.Invoke(Use.Option, c.id, e.Option);
        }

        /// <summary>Opens a page (the player picking it, a page link or a script). <paramref name="remember"/>: Back returns here.</summary>
        public void Open(ParelWorldMenuPage page, bool remember)
        {
            if (page == null || page == Page) return;
            if (remember && Page != null) _history.Push(Page);
            if (!remember) _history.Clear();
            Page = page;
            Changed?.Invoke();
        }

        public bool CanGoBack => _history.Count > 0;

        public void Back()
        {
            if (_history.Count == 0) return;
            Page = _history.Pop();
            Changed?.Invoke();
        }

        // ── scripts (VoltWorldMenu) ──────────────────────────────────────────

        public bool GetToggle(string id) => _byId.TryGetValue(id ?? string.Empty, out Entry e) && e.On;
        public float GetSlider(string id) => _byId.TryGetValue(id ?? string.Empty, out Entry e) ? e.Value : 0f;
        public int GetOption(string id) => _byId.TryGetValue(id ?? string.Empty, out Entry e) ? e.Option : 0;

        public void SetToggle(string id, bool on)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e) || e.On == on) return;
            e.On = on;
            Apply(e);
            Changed?.Invoke();
        }

        public void SetSlider(string id, float value)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e) || float.IsNaN(value)) return;
            e.Value = Clamp(e.Control, value);
            Apply(e);
            Changed?.Invoke();
        }

        public void SetOption(string id, int index)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e) || e.Control.options == null || e.Control.options.Length == 0) return;
            e.Option = Mathf.Clamp(index, 0, Mathf.Min(e.Control.options.Length, ParelWorldMenu.MaxOptions) - 1);
            Apply(e);
            Changed?.Invoke();
        }

        public void SetLabel(string id, string text)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e)) return;
            e.Label = text == null ? null : Trim(text);
            Changed?.Invoke();
        }

        public void SetLine(string id, string text)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e)) return;
            e.Line = text == null ? null : Trim(text);
            Changed?.Invoke();
        }

        public void SetVisible(string id, bool visible)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e) || e.Hidden == !visible) return;
            e.Hidden = !visible;
            Changed?.Invoke();
        }

        public void SetInteractable(string id, bool interactable)
        {
            if (!_byId.TryGetValue(id ?? string.Empty, out Entry e) || e.Locked == !interactable) return;
            e.Locked = !interactable;
            Changed?.Invoke();
        }

        /// <summary>A script asks for the menu to open (on a page, or where it was for an empty id).</summary>
        public void RequestOpen(string pageId)
        {
            ParelWorldMenuPage page = string.IsNullOrEmpty(pageId) ? Page : Menu?.FindPage(pageId);
            if (page == null) return;
            Open(page, false);
            OpenRequested?.Invoke(page);
        }

        // ── what the controls do without code ────────────────────────────────

        private static void Apply(Entry e)
        {
            ParelWorldMenuControl c = e.Control;
            switch (c.type)
            {
                case ParelWorldMenuControlType.Toggle:
                    foreach (GameObject o in c.objects ?? Array.Empty<GameObject>()) if (o != null) o.SetActive(e.On);
                    foreach (GameObject o in c.objectsWhenOff ?? Array.Empty<GameObject>()) if (o != null) o.SetActive(!e.On);
                    break;
                case ParelWorldMenuControlType.Slider:
                    if (c.volumeOf != null && c.volumeOf.Length > 0)
                    {
                        float t = Mathf.Approximately(c.max, c.min) ? 0f : Mathf.InverseLerp(c.min, c.max, e.Value);
                        foreach (AudioSource a in c.volumeOf) if (a != null) a.volume = t;
                    }
                    break;
                case ParelWorldMenuControlType.Options:
                    if (c.objectPerOption != null)
                        for (int i = 0; i < c.objectPerOption.Length; i++)
                            if (c.objectPerOption[i] != null) c.objectPerOption[i].SetActive(i == e.Option);
                    break;
            }
        }

        private static float Clamp(ParelWorldMenuControl c, float v)
        {
            float lo = Mathf.Min(c.min, c.max), hi = Mathf.Max(c.min, c.max);
            v = Mathf.Clamp(float.IsNaN(v) ? lo : v, lo, hi);
            return c.wholeNumbers ? Mathf.Round(v) : v;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > ParelWorldMenu.MaxText ? s.Substring(0, ParelWorldMenu.MaxText) : s;
        }
    }
}
