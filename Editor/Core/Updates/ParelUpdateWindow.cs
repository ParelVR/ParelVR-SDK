using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace ParelVR.SDK.Core.Updates
{
    /// <summary>
    /// The SDK's update pane: checks GitHub for a newer SDK, shows what changed, and installs it. Opened from the
    /// control panel's Settings tab or ParelVR SDK > Check for Updates.
    /// </summary>
    public sealed class ParelUpdateWindow : EditorWindow
    {
        private enum Phase { Checking, UpToDate, Available, Updating, Updated, Failed, Managed }

        private static string UiRoot => ParelPackagePaths.Combine("Editor/UI/");

        private VisualElement _body, _footer, _state;
        private Label _installedLabel, _sourceLabel;
        private ParelStatusGlyph _glyph;
        private VisualElement _progressFill;
        private Label _stepLabel;
        private ParelSdkUpdater.CheckResult _check;
        private string _updatedVersion;
        private bool _busy;

        [MenuItem("ParelVR SDK/Check for Updates...", priority = 900)]
        public static void Open()
        {
            ParelUpdateWindow window = Create();
            window.StartCheck();
        }

        internal static void ShowUpdated(string version)
        {
            ParelUpdateWindow window = Create();
            window._updatedVersion = version;
            window.Render(Phase.Updated);
        }

        private static ParelUpdateWindow Create()
        {
            var window = GetWindow<ParelUpdateWindow>(true, "ParelVR SDK Updates", true);
            window.minSize = window.maxSize = new Vector2(480f, 560f);
            return window;
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.AddToClassList("bk-root");
            root.AddToClassList(EditorGUIUtility.isProSkin ? "bk-dark" : "bk-light");
            foreach (string sheet in new[] { "Theme.uss", "Updates.uss" })
            {
                var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(UiRoot + sheet);
                if (style != null) root.styleSheets.Add(style);
            }

            var hero = new VisualElement();
            hero.AddToClassList("pu-hero");
            var logo = new Image { image = AssetDatabase.LoadAssetAtPath<Texture2D>(UiRoot + "logo.png"), scaleMode = ScaleMode.ScaleToFit };
            logo.AddToClassList("pu-logo");
            hero.Add(logo);
            var heroText = new VisualElement();
            heroText.AddToClassList("pu-hero-text");
            heroText.Add(Text("ParelVR SDK", "pu-product"));
            _installedLabel = Text(string.Empty, "pu-installed");
            heroText.Add(_installedLabel);
            hero.Add(heroText);
            root.Add(hero);

            _body = new VisualElement();
            _body.AddToClassList("pu-body");
            root.Add(_body);

            _footer = new VisualElement();
            _footer.AddToClassList("pu-footer");
            root.Add(_footer);

            RefreshInstalled();
            if (_updatedVersion != null) Render(Phase.Updated);
            else if (_check == null) StartCheck();
        }

        private void RefreshInstalled()
        {
            if (_installedLabel == null) return;
            ParelSdkUpdater.Installed installed = ParelSdkUpdater.ReadInstalled();
            _installedLabel.text = $"Version {installed.Version}  ·  {installed.Describe()}";
        }

        // ------------------------------------------------------------------------------------------ flow

        private async void StartCheck()
        {
            if (_busy || _body == null) return;
            _busy = true;
            _updatedVersion = null;
            Render(Phase.Checking);
            DateTime started = DateTime.UtcNow;

            ParelSdkUpdater.CheckResult check = await ParelSdkUpdater.CheckAsync();
            // Long enough to read: a check that answers instantly would otherwise only flash the spinner.
            double shown = (DateTime.UtcNow - started).TotalMilliseconds;
            if (shown < 700) await System.Threading.Tasks.Task.Delay(700 - (int)shown);

            _busy = false;
            if (this == null) return;
            _check = check;
            if (check.Error != null) Render(Phase.Failed);
            else if (check.Installed.Kind == ParelSdkUpdater.InstallKind.Managed) Render(Phase.Managed);
            else Render(check.UpdateAvailable ? Phase.Available : Phase.UpToDate);
        }

        private async void StartUpdate()
        {
            if (_busy || _check?.Latest == null) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            _busy = true;
            Render(Phase.Updating);
            try
            {
                await ParelSdkUpdater.UpdateAsync(_check, SetProgress);
                // Unity reloads its scripts next; ParelSdkUpdateResume reopens this pane to confirm.
            }
            catch (Exception e)
            {
                _busy = false;
                if (this == null) return;
                _check.Error = e.Message;
                Render(Phase.Failed, updating: true);
            }
        }

        private void SetProgress(float amount, string step)
        {
            if (_progressFill != null) _progressFill.style.width = Length.Percent(Mathf.Clamp01(amount) * 100f);
            if (_stepLabel != null) _stepLabel.text = step;
        }

        // ------------------------------------------------------------------------------------------ states

        private void Render(Phase phase, bool updating = false)
        {
            if (_body == null) return;
            RefreshInstalled();
            _body.Clear();
            _footer.Clear();
            _progressFill = null;
            _stepLabel = null;

            _state = new VisualElement();
            _state.AddToClassList("pu-state");
            _state.AddToClassList("pu-state-enter");
            _body.Add(_state);
            // Fades and lifts each state in rather than swapping it hard.
            _state.schedule.Execute(() => _state?.RemoveFromClassList("pu-state-enter")).StartingIn(16);

            var status = new VisualElement();
            status.AddToClassList("pu-status");
            _state.Add(status);
            _glyph = new ParelStatusGlyph();
            _glyph.AddToClassList("pu-glyph");
            status.Add(_glyph);

            ParelSdkUpdater.Latest latest = _check?.Latest;
            ParelSdkUpdater.Installed installed = _check?.Installed ?? ParelSdkUpdater.ReadInstalled();

            switch (phase)
            {
                case Phase.Checking:
                    _glyph.Mode = ParelStatusGlyph.Kind.Spinner;
                    status.Add(Text("Checking for updates", "pu-title"));
                    status.Add(Text("Looking on GitHub for a newer ParelVR SDK...", "pu-text"));
                    Footer(("Close", Close, false));
                    break;

                case Phase.UpToDate:
                    _glyph.Mode = ParelStatusGlyph.Kind.Check;
                    status.Add(Text("You're all caught up", "pu-title"));
                    status.Add(Text($"ParelVR SDK {installed.Version} is the newest version.", "pu-text"));
                    if (latest != null) status.Add(Text($"Latest release {Ago(latest.Date)}  ·  {latest.ShortCommit}", "pu-meta"));
                    Notes(latest, "In this version");
                    Footer(("Check Again", StartCheck, false), ("Close", Close, true));
                    break;

                case Phase.Available:
                    _glyph.Mode = ParelStatusGlyph.Kind.Update;
                    status.Add(Text("An update is available", "pu-title"));
                    status.Add(Text("A newer ParelVR SDK is ready to install.", "pu-text"));
                    _state.Add(Compare(installed.Version, latest.Version));
                    _state.Add(Text($"Released {Ago(latest.Date)}  ·  {latest.ShortCommit}" + (string.IsNullOrEmpty(latest.Title) ? string.Empty : "  ·  " + latest.Title), "pu-meta"));
                    Notes(latest, "What's new");
                    _state.Add(Text(installed.Kind == ParelSdkUpdater.InstallKind.Git
                        ? "Updates through the Package Manager. Your scenes and settings stay as they are."
                        : "Downloads the SDK from GitHub, no Git needed. Your scenes and settings stay as they are.", "pu-hint"));
                    Footer(("Not Now", Close, false), ($"Update to {latest.Version}", StartUpdate, true));
                    break;

                case Phase.Updating:
                    _glyph.Mode = ParelStatusGlyph.Kind.Spinner;
                    status.Add(Text($"Updating to {latest?.Version}", "pu-title"));
                    status.Add(Text("Keep Unity open. It reloads the SDK when the update is in.", "pu-text"));
                    var track = new VisualElement();
                    track.AddToClassList("bk-progress-track");
                    track.AddToClassList("pu-progress");
                    _progressFill = new VisualElement();
                    _progressFill.AddToClassList("bk-progress-fill");
                    track.Add(_progressFill);
                    _state.Add(track);
                    _stepLabel = Text("Starting...", "pu-step");
                    _state.Add(_stepLabel);
                    break;

                case Phase.Updated:
                    _glyph.Mode = ParelStatusGlyph.Kind.Check;
                    status.Add(Text("SDK updated", "pu-title"));
                    status.Add(Text($"You're now on ParelVR SDK {_updatedVersion ?? installed.Version}.", "pu-text"));
                    Footer(("Check Again", StartCheck, false), ("Done", Close, true));
                    break;

                case Phase.Failed:
                    _glyph.Mode = ParelStatusGlyph.Kind.Warning;
                    status.Add(Text(updating ? "The update didn't finish" : "Couldn't check for updates", "pu-title"));
                    status.Add(Text(_check?.Error ?? "Something went wrong.", "pu-text"));
                    if (updating) status.Add(Text("Nothing was changed. Your current SDK is still installed.", "pu-meta"));
                    Footer(("Close", Close, false), ("Try Again", updating ? (Action)StartUpdate : StartCheck, true));
                    break;

                case Phase.Managed:
                    _glyph.Mode = ParelStatusGlyph.Kind.Check;
                    status.Add(Text("Updated with this project", "pu-title"));
                    status.Add(Text("This copy of the SDK is part of the ParelVR project and is updated by hand, not from here.", "pu-text"));
                    if (latest != null) status.Add(Text($"Newest on GitHub: {latest.Version}  ·  {latest.ShortCommit}", "pu-meta"));
                    Footer(("Close", Close, true));
                    break;
            }
        }

        private VisualElement Compare(string from, string to)
        {
            var row = new VisualElement();
            row.AddToClassList("pu-compare");
            row.Add(Chip("Installed", from, false));
            var arrow = new ParelStatusGlyph { Mode = ParelStatusGlyph.Kind.Arrow };
            arrow.AddToClassList("pu-compare-arrow");
            row.Add(arrow);
            row.Add(Chip("New", to, true));
            return row;
        }

        private static VisualElement Chip(string caption, string value, bool fresh)
        {
            var chip = new VisualElement();
            chip.AddToClassList("pu-chip");
            if (fresh) chip.AddToClassList("pu-chip-new");
            chip.Add(Text(caption, "pu-chip-label"));
            chip.Add(Text(value, "pu-chip-value"));
            return chip;
        }

        /// <summary>The newest changelog section as a card: "###" lines become headings, "- " lines bullets.</summary>
        private void Notes(ParelSdkUpdater.Latest latest, string title)
        {
            if (string.IsNullOrWhiteSpace(latest?.Notes)) return;
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            card.AddToClassList("pu-notes");
            card.Add(Text(title, "bk-card-title"));
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("pu-notes-scroll");
            card.Add(scroll);

            Label bullet = null;
            foreach (string raw in latest.Notes.Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("## ", StringComparison.Ordinal) || line.Length == 0) { bullet = null; continue; }
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    scroll.Add(Text(line.Substring(4), "pu-note-heading"));
                    bullet = null;
                }
                else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    var item = new VisualElement();
                    item.AddToClassList("pu-note-item");
                    var dot = new VisualElement();
                    dot.AddToClassList("pu-note-dot");
                    item.Add(dot);
                    bullet = Text(Clean(line.TrimStart().Substring(2)), "pu-note-text");
                    item.Add(bullet);
                    scroll.Add(item);
                }
                else if (bullet != null)
                {
                    bullet.text += " " + Clean(line.Trim());   // a wrapped bullet continues on the next line
                }
                else
                {
                    scroll.Add(Text(Clean(line.Trim()), "pu-note-text"));
                }
            }
            _state.Add(card);
        }

        private void Footer(params (string label, Action action, bool primary)[] buttons)
        {
            var source = Text("github.com/" + ParelSdkUpdater.Repo, "pu-source");
            source.RegisterCallback<ClickEvent>(_ => Application.OpenURL(ParelSdkUpdater.RepoUrl));
            source.tooltip = "Open the SDK on GitHub";
            _footer.Add(source);
            foreach ((string label, Action action, bool primary) in buttons)
            {
                var button = new Button(action) { text = label };
                button.AddToClassList("bk-btn");
                if (primary) button.AddToClassList("bk-btn-primary");
                _footer.Add(button);
            }
        }

        private static Label Text(string text, string style)
        {
            var label = new Label(text);
            label.AddToClassList(style);
            return label;
        }

        private static string Clean(string markdown) => markdown.Replace("**", string.Empty).Replace("`", string.Empty);

        private static string Ago(DateTime when)
        {
            if (when == default) return "recently";
            TimeSpan span = DateTime.UtcNow - when.ToUniversalTime();
            if (span.TotalMinutes < 2) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} minutes ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
            return when.ToLocalTime().ToString("MMM d, yyyy");
        }
    }

    /// <summary>
    /// The pane's status mark, drawn rather than taken from a font: a turning arc while working, a check when done,
    /// an upward arrow for an update, a warning on failure, and a small arrow between two versions.
    /// </summary>
    public sealed class ParelStatusGlyph : VisualElement
    {
        public enum Kind { Spinner, Check, Update, Warning, Arrow }

        private Kind _mode;
        private float _angle;
        private IVisualElementScheduledItem _spin;

        public Kind Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                EnableInClassList("pu-glyph-good", value == Kind.Check);
                EnableInClassList("pu-glyph-accent", value == Kind.Spinner || value == Kind.Update);
                EnableInClassList("pu-glyph-bad", value == Kind.Warning);
                EnableInClassList("pu-glyph-faint", value == Kind.Arrow);
                _spin?.Pause();
                if (value == Kind.Spinner)
                {
                    _spin = schedule.Execute(() => { _angle = (_angle + 6f) % 360f; MarkDirtyRepaint(); }).Every(16);
                }
                MarkDirtyRepaint();
            }
        }

        public ParelStatusGlyph()
        {
            generateVisualContent += Draw;
            pickingMode = PickingMode.Ignore;
        }

        private void Draw(MeshGenerationContext context)
        {
            Rect r = contentRect;
            float size = Mathf.Min(r.width, r.height);
            if (size <= 0f) return;
            Vector2 c = r.center;
            float radius = size * 0.5f - 3f;
            Color color = resolvedStyle.color;
            Painter2D p = context.painter2D;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            switch (_mode)
            {
                case Kind.Spinner:
                    p.lineWidth = 4f;
                    p.strokeColor = new Color(color.r, color.g, color.b, 0.18f);
                    p.BeginPath();
                    p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Stroke();
                    p.strokeColor = color;
                    p.BeginPath();
                    p.Arc(c, radius, Angle.Degrees(_angle), Angle.Degrees(_angle + 100f));
                    p.Stroke();
                    break;

                case Kind.Check:
                case Kind.Update:
                case Kind.Warning:
                    p.fillColor = new Color(color.r, color.g, color.b, 0.14f);
                    p.BeginPath();
                    p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Fill();
                    p.lineWidth = 3f;
                    p.strokeColor = color;
                    p.BeginPath();
                    p.Arc(c, radius, Angle.Degrees(0f), Angle.Degrees(360f));
                    p.Stroke();
                    p.lineWidth = Mathf.Max(3f, size * 0.07f);
                    p.BeginPath();
                    if (_mode == Kind.Check)
                    {
                        p.MoveTo(c + new Vector2(-0.30f, 0.02f) * size);
                        p.LineTo(c + new Vector2(-0.08f, 0.22f) * size);
                        p.LineTo(c + new Vector2(0.30f, -0.18f) * size);
                    }
                    else if (_mode == Kind.Update)
                    {
                        p.MoveTo(c + new Vector2(0f, 0.25f) * size);
                        p.LineTo(c + new Vector2(0f, -0.23f) * size);
                        p.MoveTo(c + new Vector2(-0.19f, -0.05f) * size);
                        p.LineTo(c + new Vector2(0f, -0.25f) * size);
                        p.LineTo(c + new Vector2(0.19f, -0.05f) * size);
                    }
                    else
                    {
                        p.MoveTo(c + new Vector2(0f, -0.22f) * size);
                        p.LineTo(c + new Vector2(0f, 0.06f) * size);
                        p.MoveTo(c + new Vector2(0f, 0.21f) * size);
                        p.LineTo(c + new Vector2(0f, 0.215f) * size);
                    }
                    p.Stroke();
                    break;

                case Kind.Arrow:
                    p.lineWidth = 2f;
                    p.strokeColor = color;
                    p.BeginPath();
                    p.MoveTo(c + new Vector2(-0.35f, 0f) * size);
                    p.LineTo(c + new Vector2(0.35f, 0f) * size);
                    p.MoveTo(c + new Vector2(0.12f, -0.22f) * size);
                    p.LineTo(c + new Vector2(0.35f, 0f) * size);
                    p.LineTo(c + new Vector2(0.12f, 0.22f) * size);
                    p.Stroke();
                    break;
            }
        }
    }
}
