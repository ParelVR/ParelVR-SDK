using System;
using System.Collections.Generic;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.Http;
using ParelVR.SDK.Core.Util;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ParelVR.SDK.Backend
{
    /// <summary>
    /// The "Upload for someone else" box above a Builder's upload button: a toggle, then a search for the
    /// ParelVR account to upload for (picture, name and @username), then the request's state while it runs
    /// (pending, approved, denied). The pick survives script reloads for the rest of the Unity session.
    /// </summary>
    public sealed class ParelUploadForOther : VisualElement
    {
        [Serializable]
        public sealed class Pick
        {
            public string id;
            public string username;
            public string displayName;
            public string avatarUrl;

            public string Label => string.IsNullOrWhiteSpace(displayName) ? "@" + username : displayName;
            public string LabelWithHandle => string.IsNullOrWhiteSpace(displayName) || displayName == username ? "@" + username : $"{displayName} (@{username})";
        }

        [Serializable] private sealed class UserResult { public string parelId; public string playFabId; public string username; public string displayName; public string avatarUrl; }
        [Serializable] private sealed class SearchResponse { public bool success; public string message; public UserResult[] results; }
        [Serializable] private sealed class Saved { public bool on; public Pick pick; }

        public enum Tone { Wait, Good, Bad, Neutral }

        private const int MinimumSearchLength = 2;
        private const int SearchDelayMs = 350;
        private const int MaximumResults = 6;

        private readonly string _noun;
        private readonly string _stateKey;
        private readonly Toggle _toggle;
        private readonly VisualElement _body;
        private readonly VisualElement _picker;
        private readonly VisualElement _status;
        private int _searchVersion;
        private bool _locked;

        /// <summary>The toggle or the picked account changed.</summary>
        public event Action Changed;

        /// <summary>The toggle is on (an account may not be picked yet).</summary>
        public bool Enabled => _toggle.value;

        /// <summary>The account picked to upload for, or null.</summary>
        public Pick Target { get; private set; }

        /// <summary>On, with an account picked: the upload goes to them as a request.</summary>
        public bool Ready => Enabled && Target != null;

        /// <param name="noun">"avatar" or "world", for the wording.</param>
        public ParelUploadForOther(string noun)
        {
            _noun = noun;
            _stateKey = "ParelVR.UploadForOther." + noun;
            style.marginTop = 10;

            VisualElement header = Row();
            header.style.alignItems = Align.Center;
            _toggle = new Toggle();
            _toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.target != _toggle) return;
                Refresh();
                Save();
                Changed?.Invoke();
            });
            header.Add(_toggle);
            var title = new Label("Upload for someone else");
            title.AddToClassList("bk-label");
            title.style.marginLeft = 4;
            title.RegisterCallback<ClickEvent>(_ => { if (_toggle.enabledSelf) _toggle.value = !_toggle.value; });
            header.Add(title);
            Add(header);

            _body = new VisualElement();
            var hint = new Label($"Build it here and upload it to another player's {noun}s. ParelVR emails them, and nothing is uploaded until they approve it on their dashboard.");
            hint.AddToClassList("bk-hint");
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginTop = 2;
            _body.Add(hint);
            _picker = new VisualElement();
            _body.Add(_picker);
            Add(_body);

            _status = new VisualElement();
            _status.AddToClassList("bk-person");
            _status.style.display = DisplayStyle.None;
            Add(_status);

            Load();
            Refresh();

            // A request cut off by a reload or Unity closing is cancelled on load; say so here once.
            schedule.Execute(() =>
            {
                string notice = ParelUploadRequestFlow.TakeInterruptedNotice();
                if (notice != null) ShowStatus(Tone.Neutral, "Upload request cancelled", notice, "Dismiss", HideStatus);
            }).Every(1500);
        }

        /// <summary>Stops changes while a build or request is running.</summary>
        public void SetLocked(bool locked)
        {
            _locked = locked;
            _toggle.SetEnabled(!locked);
            _picker.SetEnabled(!locked);
        }

        // ---- the request's state ----------------------------------------------------------------

        /// <summary>Shows a state box under the picker (pending, approved, denied...). button may be null.</summary>
        public void ShowStatus(Tone tone, string title, string text, string buttonText = null, Action onButton = null)
        {
            _status.Clear();
            _status.style.display = DisplayStyle.Flex;
            _status.style.borderLeftColor = tone switch
            {
                Tone.Good => new Color(0.30f, 0.69f, 0.42f),
                Tone.Bad => new Color(0.86f, 0.33f, 0.36f),
                Tone.Wait => new Color(0.85f, 0.64f, 0.25f),
                _ => new StyleColor(StyleKeyword.Null)
            };

            VisualElement top = Row();
            top.style.alignItems = Align.Center;
            if (Target != null) top.Add(Avatar(Target.avatarUrl, Target.Label));
            var heading = new Label(title);
            heading.AddToClassList("bk-person-name");
            heading.EnableInClassList("bk-text-good", tone == Tone.Good);
            heading.EnableInClassList("bk-text-bad", tone == Tone.Bad);
            heading.style.flexGrow = 1;
            heading.style.flexShrink = 1;
            heading.style.whiteSpace = WhiteSpace.Normal;
            top.Add(heading);
            if (!string.IsNullOrEmpty(buttonText) && onButton != null)
            {
                var button = new Button(onButton) { text = buttonText };
                button.AddToClassList("bk-btn");
                if (tone == Tone.Wait) button.AddToClassList("bk-btn-danger");
                top.Add(button);
            }
            _status.Add(top);

            if (!string.IsNullOrEmpty(text))
            {
                var body = new Label(text);
                body.AddToClassList("bk-hint");
                body.style.whiteSpace = WhiteSpace.Normal;
                body.style.marginTop = 4;
                body.selection.isSelectable = true;
                _status.Add(body);
            }
        }

        public void HideStatus()
        {
            _status.Clear();
            _status.style.display = DisplayStyle.None;
        }

        // ---- picking the account ----------------------------------------------------------------

        private void Refresh()
        {
            _body.style.display = _toggle.value ? DisplayStyle.Flex : DisplayStyle.None;
            _picker.Clear();
            if (!_toggle.value) return;
            if (Target != null) BuildChip();
            else BuildSearch();
        }

        private void BuildChip()
        {
            var box = new VisualElement();
            box.AddToClassList("bk-person");
            VisualElement row = Row();
            row.style.alignItems = Align.Center;
            row.Add(Avatar(Target.avatarUrl, Target.Label));
            row.Add(Names(Target.displayName, Target.username));
            row.Add(new VisualElement { style = { flexGrow = 1 } });
            var change = new Button(() =>
            {
                Target = null;
                Refresh();
                Save();
                Changed?.Invoke();
            }) { text = "Change" };
            change.AddToClassList("bk-btn");
            row.Add(change);
            box.Add(row);
            var note = new Label($"Uploads to {Target.LabelWithHandle}'s {_noun}s after they approve it.");
            note.AddToClassList("bk-note");
            note.style.marginTop = 4;
            note.style.whiteSpace = WhiteSpace.Normal;
            box.Add(note);
            _picker.Add(box);
        }

        private void BuildSearch()
        {
            var box = new VisualElement();
            box.AddToClassList("bk-person");
            var search = new TextField();
            search.AddToClassList("bk-search");
            search.textEdition.placeholder = "Search ParelVR players by name or username";
            box.Add(search);

            var status = new Label($"Type at least {MinimumSearchLength} characters, then click the player you're uploading for.");
            status.AddToClassList("bk-note");
            status.style.marginTop = 4;
            status.style.whiteSpace = WhiteSpace.Normal;
            box.Add(status);

            var results = new VisualElement();
            box.Add(results);

            IVisualElementScheduledItem pending = null;
            search.RegisterValueChangedCallback(evt =>
            {
                string query = (evt.newValue ?? string.Empty).Trim().TrimStart('@');
                pending?.Pause();
                int version = ++_searchVersion;
                if (query.Length < MinimumSearchLength)
                {
                    results.Clear();
                    status.text = $"Type at least {MinimumSearchLength} characters, then click the player you're uploading for.";
                    return;
                }
                status.text = "Searching...";
                pending = search.schedule.Execute(() => Search(query, version, results, status)).StartingIn(SearchDelayMs);
            });
            _picker.Add(box);
        }

        private async void Search(string query, int version, VisualElement results, Label status)
        {
            var found = new List<UserResult>();
            try
            {
                if (!ParelSession.IsLoggedIn) throw new Exception("sign in to the ParelVR SDK first.");
                SearchResponse response = await ParelApiClient.GetJsonAsync<SearchResponse>("/api/parelvr/users/search?q=" + Uri.EscapeDataString(query));
                if (version != _searchVersion) return; // a newer search has started since
                if (response?.results != null) found.AddRange(response.results);
            }
            catch (Exception e)
            {
                if (version != _searchVersion) return;
                results.Clear();
                status.text = "Search failed: " + e.Message;
                return;
            }

            found.RemoveAll(u => u == null || string.IsNullOrWhiteSpace(u.username) || string.IsNullOrWhiteSpace(IdOf(u)) ||
                                 string.Equals(u.username, ParelSession.Username, StringComparison.OrdinalIgnoreCase));
            results.Clear();
            if (found.Count == 0)
            {
                status.text = $"No ParelVR player matches \"{query}\".";
                return;
            }

            status.text = found.Count > MaximumResults ? $"Showing the first {MaximumResults} matches. Keep typing to narrow it down." : "Click the player you're uploading for.";
            for (int i = 0; i < found.Count && i < MaximumResults; i++)
            {
                UserResult user = found[i];
                VisualElement item = Row();
                item.AddToClassList("bk-search-result");
                item.style.alignItems = Align.Center;
                item.Add(Avatar(user.avatarUrl, string.IsNullOrWhiteSpace(user.displayName) ? user.username : user.displayName));
                item.Add(Names(user.displayName, user.username));
                item.RegisterCallback<ClickEvent>(_ =>
                {
                    if (_locked) return;
                    Target = new Pick
                    {
                        id = IdOf(user),
                        username = user.username.Trim().TrimStart('@'),
                        displayName = (user.displayName ?? string.Empty).Trim(),
                        avatarUrl = user.avatarUrl ?? string.Empty,
                    };
                    Refresh();
                    Save();
                    Changed?.Invoke();
                });
                results.Add(item);
            }
        }

        private static string IdOf(UserResult user) => !string.IsNullOrWhiteSpace(user.parelId) ? user.parelId : user.playFabId;

        // ---- remembered for the Unity session ---------------------------------------------------

        private void Save()
        {
            SessionState.SetString(_stateKey, JsonUtility.ToJson(new Saved { on = _toggle.value, pick = Target }));
        }

        private void Load()
        {
            string json = SessionState.GetString(_stateKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                Saved saved = JsonUtility.FromJson<Saved>(json);
                _toggle.SetValueWithoutNotify(saved != null && saved.on);
                Target = saved?.pick != null && !string.IsNullOrEmpty(saved.pick.id) ? saved.pick : null;
            }
            catch (Exception) { /* start empty */ }
        }

        // ---- small pieces (same look as the World page's credit search) --------------------------

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList("bk-row");
            return row;
        }

        private static VisualElement Names(string displayName, string username)
        {
            var names = new VisualElement { style = { flexShrink = 1 } };
            var name = new Label(string.IsNullOrWhiteSpace(displayName) ? username : displayName);
            name.AddToClassList("bk-person-name");
            names.Add(name);
            var handle = new Label("@" + username);
            handle.AddToClassList("bk-person-handle");
            names.Add(handle);
            return names;
        }

        /// <summary>The account's picture, or its initial until the picture arrives or when there is none.</summary>
        private static VisualElement Avatar(string url, string name)
        {
            var avatar = new VisualElement();
            avatar.AddToClassList("bk-avatar");
            string letters = (name ?? string.Empty).Trim().TrimStart('@');
            var initial = new Label(letters.Length == 0 ? "?" : letters.Substring(0, 1).ToUpperInvariant());
            initial.AddToClassList("bk-avatar-initial");
            avatar.Add(initial);
            if (string.IsNullOrWhiteSpace(url)) return avatar;

            Action<Texture2D> show = texture =>
            {
                if (texture == null) return;
                avatar.style.backgroundImage = new StyleBackground(texture);
                initial.style.display = DisplayStyle.None;
            };
            show(ParelTextureCache.GetOrFetch(url, show));
            return avatar;
        }
    }
}
