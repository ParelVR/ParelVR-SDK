using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using global::ParelVR.SDK.Core.Auth;
using global::ParelVR.SDK.Core.Http;
using global::ParelVR.SDK.Core.Settings;
using global::ParelVR.SDK.Core.Updates;

namespace ParelVR.SDK.Core.ControlPanel
{
    /// <summary>
    /// The Control Panel's Feedback tab: a bug, an idea or anything else about the SDK, sent to ParelVR Support as a
    /// ticket with up to three pictures, so the reply comes back by email. It works signed out too; signed in, the
    /// ticket is linked to your account. The draft lives through script reloads.
    /// </summary>
    internal sealed class ParelFeedbackTab
    {
        private enum Kind { Bug, Idea, Other }

        private const string TicketsPath = "/api/parelvr/support/tickets";
        private const int MaxSubject = 120;
        private const int MaxDetails = 1500;
        private const int MaxPictures = 3;
        private const int MaxPictureBytes = 8 * 1024 * 1024;
        private const int MaxRecent = 5;

        private const string Prefix = "ParelVR.SDK.Feedback.";
        private const string EmailKey = Prefix + "Email";
        private const string NameKey = Prefix + "Name";
        private const string IncludeKey = Prefix + "IncludeDetails";
        private const string RecentKey = Prefix + "Recent";
        private const string PictureFolderKey = Prefix + "PictureFolder";

        private static readonly string[] Areas = { "Avatars", "Worlds", "Volt Scripting", "Build & Upload", "Control Panel", "Docs", "Other" };
        private static readonly string[] Frequencies = { "Every time", "Sometimes", "Once" };

        private sealed class Picture
        {
            public string Name;
            public byte[] Bytes;
            public string ContentType;
            public Texture2D Preview;
        }

        private sealed class SentTicket
        {
            public string ShortId;
            public string Email;
            public int PicturesFailed;
        }

        [Serializable] private sealed class TicketBody { public string email, username, category, title, description; }
        [Serializable] private sealed class TicketInfo { public string id; }
        [Serializable] private sealed class TicketResponse { public bool success; public string message; public TicketInfo ticket; }
        [Serializable] private sealed class RecentItem { public string id, subject, kind, at; }
        [Serializable] private sealed class RecentList { public List<RecentItem> items = new List<RecentItem>(); }

        private readonly List<Picture> _pictures = new List<Picture>();
        private SentTicket _sent;
        private bool _sending;

        private VisualElement _container;
        private VisualElement _footer;
        private Label _error;
        private Label _status;
        private Button _send;
        private Label _counter;
        private Label _detailsHint;
        private VisualElement _frequencyBlock;
        private VisualElement _pictureRow;
        private Button _addPicture;
        private Button _snapScene;
        private readonly Dictionary<Kind, VisualElement> _kindTiles = new Dictionary<Kind, VisualElement>();

        // ---- the draft: SessionState keeps it through script reloads, EditorPrefs keeps who you are ------------

        private static Kind DraftKind
        {
            get => (Kind)Mathf.Clamp(SessionState.GetInt(Prefix + "Kind", 0), 0, 2);
            set => SessionState.SetInt(Prefix + "Kind", (int)value);
        }

        private static string DraftArea
        {
            get => SessionState.GetString(Prefix + "Area", ParelModeManager.Current == ParelProjectType.Avatar ? "Avatars" : "Worlds");
            set => SessionState.SetString(Prefix + "Area", value);
        }

        private static string DraftFrequency
        {
            get => SessionState.GetString(Prefix + "Frequency", Frequencies[0]);
            set => SessionState.SetString(Prefix + "Frequency", value);
        }

        private static string DraftSubject
        {
            get => SessionState.GetString(Prefix + "Subject", string.Empty);
            set => SessionState.SetString(Prefix + "Subject", value ?? string.Empty);
        }

        private static string DraftDetails
        {
            get => SessionState.GetString(Prefix + "Details", string.Empty);
            set => SessionState.SetString(Prefix + "Details", value ?? string.Empty);
        }

        // =========================================================================================
        // Tab
        // =========================================================================================

        public void Build(VisualElement container)
        {
            _container = container;
            _footer = ParelControlPanel.FooterFor(container);
        }

        public void Shown() => Render();

        private void Render()
        {
            if (_container == null) return;
            _container.Clear();
            _footer?.Clear();
            _kindTiles.Clear();

            if (_sent != null)
            {
                RenderSent();
                SetFooterVisible(false);
                return;
            }

            BuildKindCard();
            BuildMessageCard();
            BuildPicturesCard();
            BuildReplyCard();
            BuildRecentCard(_container);
            BuildFooter();
            SetFooterVisible(true);
            RefreshKind();
        }

        private void SetFooterVisible(bool visible)
        {
            if (_footer == null) return;
            DisplayStyle display = visible && _footer.childCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _footer.style.display = display;
            if (_footer.parent != null) _footer.parent.style.display = display;
        }

        // =========================================================================================
        // The form
        // =========================================================================================

        private void BuildKindCard()
        {
            VisualElement card = Card("Send Feedback");
            var intro = new Label("Found a bug, or have an idea for the SDK? Send it here. It goes straight to ParelVR Support as a ticket, and the reply comes to your email.");
            intro.AddToClassList("bk-subheading");
            intro.style.marginBottom = 10;
            card.Add(intro);

            var tiles = new VisualElement();
            tiles.AddToClassList("bk-kind-row");
            tiles.Add(KindTile(Kind.Bug, "Bug", "Something in the SDK is broken or acts wrong."));
            tiles.Add(KindTile(Kind.Idea, "Idea", "A feature, a change or a tool you would like."));
            VisualElement other = KindTile(Kind.Other, "Other", "A question, the docs, or anything else.");
            other.style.marginRight = 0;
            tiles.Add(other);
            card.Add(tiles);

            card.Add(FieldLabel("Area"));
            card.Add(ChipRow(Areas, DraftArea, value => DraftArea = value));

            _frequencyBlock = new VisualElement();
            _frequencyBlock.Add(FieldLabel("How Often"));
            _frequencyBlock.Add(ChipRow(Frequencies, DraftFrequency, value => DraftFrequency = value));
            card.Add(_frequencyBlock);
        }

        private VisualElement KindTile(Kind kind, string title, string description)
        {
            var tile = new VisualElement();
            tile.AddToClassList("bk-mode-option");
            tile.pickingMode = PickingMode.Position;
            var name = new Label(title);
            name.AddToClassList("bk-mode-option-title");
            tile.Add(name);
            var text = new Label(description);
            text.AddToClassList("bk-mode-option-desc");
            tile.Add(text);
            tile.RegisterCallback<ClickEvent>(_ =>
            {
                DraftKind = kind;
                RefreshKind();
            });
            _kindTiles[kind] = tile;
            return tile;
        }

        /// <summary>Shows the chosen kind: its tile lit, the bug-only questions, the hint and the button's words.</summary>
        private void RefreshKind()
        {
            Kind kind = DraftKind;
            foreach (KeyValuePair<Kind, VisualElement> pair in _kindTiles)
                pair.Value.EnableInClassList("bk-mode-option-active", pair.Key == kind);
            if (_frequencyBlock != null) _frequencyBlock.style.display = kind == Kind.Bug ? DisplayStyle.Flex : DisplayStyle.None;
            if (_detailsHint != null)
            {
                _detailsHint.text = kind == Kind.Bug
                    ? "What you did, what you expected, and what happened instead. Paste the Console error if there is one."
                    : kind == Kind.Idea
                        ? "What you are making, what is in the way, and how it could work."
                        : "Anything you want the ParelVR team to know.";
            }
            UpdateSendButton();
        }

        private void BuildMessageCard()
        {
            VisualElement card = Card("Message");

            card.Add(FieldLabel("Subject"));
            var subject = new TextField { value = DraftSubject, maxLength = MaxSubject };
            subject.AddToClassList("bk-input");
            subject.RegisterValueChangedCallback(evt => DraftSubject = evt.newValue);
            card.Add(subject);

            var labelRow = new VisualElement();
            labelRow.AddToClassList("bk-row");
            labelRow.style.marginTop = 10;
            Label detailsLabel = FieldLabel("Details");
            detailsLabel.style.marginTop = 0;
            labelRow.Add(detailsLabel);
            labelRow.Add(new VisualElement { style = { flexGrow = 1 } });
            _counter = new Label();
            _counter.AddToClassList("bk-counter");
            labelRow.Add(_counter);
            card.Add(labelRow);

            var details = new TextField { value = DraftDetails, multiline = true, maxLength = MaxDetails };
            details.AddToClassList("bk-input");
            details.AddToClassList("bk-input-multiline");
            details.verticalScrollerVisibility = ScrollerVisibility.Auto;
            details.RegisterValueChangedCallback(evt =>
            {
                DraftDetails = evt.newValue;
                UpdateCounter(evt.newValue);
            });
            card.Add(details);
            UpdateCounter(details.value);

            _detailsHint = new Label();
            _detailsHint.AddToClassList("bk-hint");
            card.Add(_detailsHint);
        }

        private void UpdateCounter(string text)
        {
            if (_counter == null) return;
            int length = text?.Length ?? 0;
            _counter.text = length + " / " + MaxDetails;
            _counter.EnableInClassList("bk-counter-over", length >= MaxDetails);
        }

        private void BuildPicturesCard()
        {
            VisualElement card = Card("Pictures");
            var hint = new Label("Optional. Up to " + MaxPictures + " PNG or JPG pictures: the Console, the Inspector or the Scene view showing the problem helps a lot.");
            hint.AddToClassList("bk-hint");
            hint.style.marginTop = 0;
            hint.style.marginBottom = 8;
            card.Add(hint);

            _pictureRow = new VisualElement();
            _pictureRow.AddToClassList("bk-chip-row");
            card.Add(_pictureRow);

            var buttons = new VisualElement();
            buttons.AddToClassList("bk-row");
            _addPicture = new Button(AddPictureFromFile) { text = "Add Picture…" };
            _addPicture.AddToClassList("bk-btn");
            _addPicture.style.marginLeft = 0;
            buttons.Add(_addPicture);
            _snapScene = new Button(SnapSceneView) { text = "Snap Scene View" };
            _snapScene.AddToClassList("bk-btn");
            buttons.Add(_snapScene);
            card.Add(buttons);

            RenderPictures();
        }

        private void RenderPictures()
        {
            if (_pictureRow == null) return;
            _pictureRow.Clear();
            _pictureRow.style.display = _pictures.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (Picture picture in _pictures)
            {
                Picture captured = picture;
                var thumb = new VisualElement();
                thumb.AddToClassList("bk-thumb");
                if (picture.Preview != null) thumb.style.backgroundImage = new StyleBackground(picture.Preview);
                thumb.tooltip = picture.Name + " (" + EditorUtility.FormatBytes(picture.Bytes.Length) + ")";
                var name = new Label(picture.Name);
                name.AddToClassList("bk-thumb-name");
                thumb.Add(name);
                var remove = new Button(() => RemovePicture(captured)) { text = "×", tooltip = "Remove" };
                remove.AddToClassList("bk-thumb-remove");
                thumb.Add(remove);
                _pictureRow.Add(thumb);
            }
            bool room = _pictures.Count < MaxPictures;
            _addPicture?.SetEnabled(room);
            _snapScene?.SetEnabled(room);
        }

        private void BuildReplyCard()
        {
            VisualElement card = Card("Reply To");

            card.Add(FieldLabel("Email"));
            var email = new TextField { value = EditorPrefs.GetString(EmailKey, string.Empty), maxLength = 200 };
            email.AddToClassList("bk-input");
            email.RegisterValueChangedCallback(evt => EditorPrefs.SetString(EmailKey, evt.newValue.Trim()));
            card.Add(email);
            card.Add(Hint("ParelVR Support replies here, and you get an email when the ticket is opened."));

            if (ParelSession.IsLoggedIn)
            {
                var account = new VisualElement();
                account.AddToClassList("bk-row");
                account.style.marginTop = 10;
                var who = new Label("Signed in as " + AccountName() + ". The ticket is linked to your account.");
                who.AddToClassList("bk-hint");
                who.style.marginTop = 0;
                who.style.flexShrink = 1;
                account.Add(who);
                account.Add(new VisualElement { style = { flexGrow = 1 } });
                var pill = new Label("LINKED");
                pill.AddToClassList("bk-pill");
                pill.AddToClassList("bk-pill-good");
                pill.style.marginLeft = 8;
                account.Add(pill);
                card.Add(account);
            }
            else
            {
                Label nameLabel = FieldLabel("Your Name");
                nameLabel.style.marginTop = 10;
                card.Add(nameLabel);
                var name = new TextField { value = EditorPrefs.GetString(NameKey, string.Empty), maxLength = 100 };
                name.AddToClassList("bk-input");
                name.RegisterValueChangedCallback(evt => EditorPrefs.SetString(NameKey, evt.newValue.Trim()));
                card.Add(name);
                card.Add(Hint("Your ParelVR username, if you have one. Sign in on the Authentication tab to link the ticket to your account."));
            }

            var include = new Toggle { text = "Include SDK and system details", value = EditorPrefs.GetBool(IncludeKey, true) };
            include.style.marginTop = 10;
            include.style.marginLeft = 0;
            var preview = new Label(string.Join("\n", SystemDetails()));
            preview.AddToClassList("bk-feedback-details");
            preview.selection.isSelectable = true;
            preview.style.display = include.value ? DisplayStyle.Flex : DisplayStyle.None;
            include.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(IncludeKey, evt.newValue);
                preview.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
            });
            card.Add(include);
            card.Add(preview);
        }

        private void BuildRecentCard(VisualElement host)
        {
            List<RecentItem> recent = LoadRecent().items;
            if (recent.Count == 0) return;

            VisualElement card = Card("Sent From This Computer", host);
            foreach (RecentItem item in recent)
            {
                var row = new VisualElement();
                row.AddToClassList("bk-feedback-recent");
                var pill = new Label((item.kind ?? "Other").ToUpperInvariant());
                pill.AddToClassList("bk-pill");
                pill.AddToClassList(item.kind == nameof(Kind.Bug) ? "bk-pill-bad" : item.kind == nameof(Kind.Idea) ? "bk-pill-accent" : "bk-pill-neutral");
                pill.style.width = 44;
                row.Add(pill);
                var subject = new Label(item.subject);
                subject.AddToClassList("bk-feedback-recent-subject");
                row.Add(subject);
                var meta = new Label("#" + item.id + "  ·  " + Ago(item.at));
                meta.AddToClassList("bk-version-meta");
                meta.style.flexGrow = 0;
                meta.style.flexShrink = 0;
                row.Add(meta);
                string id = item.id;
                row.tooltip = "Click to copy the ticket number";
                row.RegisterCallback<ClickEvent>(_ => EditorGUIUtility.systemCopyBuffer = "#" + id);
                card.Add(row);
            }
        }

        private void BuildFooter()
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            card.style.marginBottom = 0;

            _error = new Label();
            _error.AddToClassList("bk-error-banner");
            _error.style.marginBottom = 8;
            card.Add(_error);

            var row = new VisualElement();
            row.AddToClassList("bk-row");
            _status = new Label();
            _status.AddToClassList("bk-hint");
            _status.style.marginTop = 0;
            _status.style.flexGrow = 1;
            _status.style.flexShrink = 1;
            row.Add(_status);
            var clear = new Button(ClearDraft) { text = "Clear" };
            clear.AddToClassList("bk-btn");
            row.Add(clear);
            _send = new Button(Send);
            _send.AddToClassList("bk-btn");
            _send.AddToClassList("bk-btn-primary");
            _send.style.marginRight = 0;
            _send.style.minWidth = 140;
            row.Add(_send);
            card.Add(row);

            // Docked under the page like the Builder's Build section; at the end of the page if there is no dock.
            (_footer ?? _container).Add(card);
            UpdateSendButton();
        }

        private void UpdateSendButton()
        {
            if (_send == null) return;
            Kind kind = DraftKind;
            _send.text = _sending ? "Sending…" : kind == Kind.Bug ? "Send Bug Report" : kind == Kind.Idea ? "Send Idea" : "Send Feedback";
            _send.SetEnabled(!_sending);
        }

        // =========================================================================================
        // Sent
        // =========================================================================================

        private void RenderSent()
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            card.AddToClassList("bk-feedback-sent");
            _container.Add(card);

            var mark = new VisualElement();
            mark.AddToClassList("bk-feedback-mark");
            mark.generateVisualContent += DrawCheck;
            card.Add(mark);

            var heading = new Label("Thanks, it's on its way");
            heading.AddToClassList("bk-heading");
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            card.Add(heading);

            var number = new Label("#" + _sent.ShortId);
            number.AddToClassList("bk-feedback-ticket");
            number.selection.isSelectable = true;
            card.Add(number);

            var text = new Label("Your ticket is with ParelVR Support. The reply goes to " + _sent.Email + ", and an email confirming the ticket is on its way.");
            text.AddToClassList("bk-subheading");
            text.style.unityTextAlign = TextAnchor.MiddleCenter;
            text.style.maxWidth = 420;
            card.Add(text);

            if (_sent.PicturesFailed > 0)
            {
                var warn = new Label(_sent.PicturesFailed == 1
                    ? "One picture did not upload. Reply to the confirmation email to send it."
                    : _sent.PicturesFailed + " pictures did not upload. Reply to the confirmation email to send them.");
                warn.AddToClassList("bk-pill");
                warn.AddToClassList("bk-pill-warn");
                warn.style.whiteSpace = WhiteSpace.Normal;
                warn.style.paddingTop = warn.style.paddingBottom = 6;
                warn.style.marginBottom = 12;
                card.Add(warn);
            }

            var buttons = new VisualElement();
            buttons.AddToClassList("bk-row");
            string shortId = _sent.ShortId;
            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = "#" + shortId) { text = "Copy Ticket Number" };
            copy.AddToClassList("bk-btn");
            buttons.Add(copy);
            var again = new Button(() =>
            {
                _sent = null;
                Render();
            }) { text = "Send More Feedback" };
            again.AddToClassList("bk-btn");
            again.AddToClassList("bk-btn-primary");
            buttons.Add(again);
            card.Add(buttons);

            BuildRecentCard(_container);
        }

        private static void DrawCheck(MeshGenerationContext context)
        {
            VisualElement element = context.visualElement;
            float w = element.layout.width, h = element.layout.height;
            Painter2D painter = context.painter2D;
            painter.strokeColor = element.resolvedStyle.color;
            painter.lineWidth = Mathf.Max(2f, w * 0.08f);
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            painter.MoveTo(new Vector2(w * 0.29f, h * 0.52f));
            painter.LineTo(new Vector2(w * 0.44f, h * 0.67f));
            painter.LineTo(new Vector2(w * 0.72f, h * 0.37f));
            painter.Stroke();
        }

        // =========================================================================================
        // Sending
        // =========================================================================================

        private async void Send()
        {
            if (_sending) return;
            HideError();

            Kind kind = DraftKind;
            string subject = DraftSubject.Trim();
            string details = DraftDetails.Trim();
            string email = EditorPrefs.GetString(EmailKey, string.Empty).Trim();
            string username = ParelSession.IsLoggedIn ? ParelSession.Username : EditorPrefs.GetString(NameKey, string.Empty).Trim();

            string problem =
                subject.Length < 3 ? "Give it a short subject." :
                details.Length < 10 ? "Tell us a bit more in Details." :
                !LooksLikeEmail(email) ? "Enter the email address ParelVR Support should reply to." :
                string.IsNullOrEmpty(username) ? "Enter your name, or sign in on the Authentication tab." :
                null;
            if (problem != null)
            {
                ShowError(problem);
                return;
            }

            _sending = true;
            UpdateSendButton();
            SetStatus("Sending to ParelVR Support…");
            try
            {
                var body = new TicketBody
                {
                    email = email,
                    username = username,
                    category = kind == Kind.Bug ? "Bug" : "Other",
                    title = Clip((kind == Kind.Bug ? "[SDK Bug] " : kind == Kind.Idea ? "[SDK Idea] " : "[SDK] ") + subject, 150),
                    description = Describe(kind, details),
                };
                var response = await ParelApiClient.PostJsonAsync<TicketResponse>(TicketsPath, body);
                string id = response?.ticket?.id;
                if (string.IsNullOrEmpty(id)) throw new ParelApiException("ParelVR Support did not send back a ticket number. Try again in a moment.", 0);

                int failed = 0;
                for (int i = 0; i < _pictures.Count; i++)
                {
                    Picture picture = _pictures[i];
                    SetStatus("Uploading picture " + (i + 1) + " of " + _pictures.Count + "…");
                    try
                    {
                        await ParelApiClient.PutBytesRawAsync(
                            TicketsPath + "/" + UnityWebRequest.EscapeURL(id) + "/attachments?type=image&filename=" + UnityWebRequest.EscapeURL(picture.Name),
                            picture.Bytes, picture.ContentType);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Debug.LogWarning("[ParelVR SDK] Feedback picture " + picture.Name + " did not upload: " + ex.Message);
                    }
                }

                string shortId = id.Length > 8 ? id.Substring(0, 8) : id;
                Remember(shortId, subject, kind);
                _sent = new SentTicket { ShortId = shortId, Email = email, PicturesFailed = failed };
                ResetDraft();
                Render();
            }
            catch (ParelApiException ex)
            {
                ShowError(ex.StatusCode == 0 ? "Could not reach ParelVR Support: " + ex.Message : ex.Message);
            }
            catch (Exception ex)
            {
                ShowError("The feedback was not sent: " + ex.Message);
                Debug.LogException(ex);
            }
            finally
            {
                _sending = false;
                SetStatus(string.Empty);
                UpdateSendButton();
            }
        }

        /// <summary>What the ticket says: the message, then where it is about and the SDK it came from.</summary>
        private static string Describe(Kind kind, string details)
        {
            var text = new StringBuilder();
            text.AppendLine(Clip(details, MaxDetails));
            text.AppendLine();
            text.AppendLine("Area: " + DraftArea);
            if (kind == Kind.Bug) text.AppendLine("How often: " + DraftFrequency);
            text.AppendLine("Sent from the ParelVR SDK Feedback tab.");
            if (EditorPrefs.GetBool(IncludeKey, true))
            {
                text.AppendLine();
                foreach (string line in SystemDetails()) text.AppendLine(line);
            }
            return Clip(text.ToString().TrimEnd(), 2000);
        }

        private static IEnumerable<string> SystemDetails()
        {
            ParelSdkUpdater.Installed installed = ParelSdkUpdater.ReadInstalled();
            yield return "SDK: " + installed.Version + " (" + installed.Describe() + ")";
            yield return "Unity: " + Application.unityVersion;
            yield return "Project: " + ParelModeManager.Current + ", building for " + EditorUserBuildSettings.activeBuildTarget;
            yield return "Server: " + ParelEnvironment.Current;
            yield return "System: " + SystemInfo.operatingSystem;
            yield return "Graphics: " + SystemInfo.graphicsDeviceType + ", " + SystemInfo.graphicsDeviceName;
        }

        private void ResetDraft()
        {
            DraftSubject = string.Empty;
            DraftDetails = string.Empty;
            DraftFrequency = Frequencies[0];
            foreach (Picture picture in _pictures)
                if (picture.Preview != null) UnityEngine.Object.DestroyImmediate(picture.Preview);
            _pictures.Clear();
        }

        private void ClearDraft()
        {
            if (_sending) return;
            ResetDraft();
            DraftKind = Kind.Bug;
            Render();
        }

        // =========================================================================================
        // Pictures
        // =========================================================================================

        private void AddPictureFromFile()
        {
            string folder = EditorPrefs.GetString(PictureFolderKey, string.Empty);
            string path = EditorUtility.OpenFilePanelWithFilters("Add a Picture", folder, new[] { "Pictures", "png,jpg,jpeg" });
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString(PictureFolderKey, Path.GetDirectoryName(path));
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                bool jpg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
                AddPicture(Path.GetFileName(path), bytes, jpg ? "image/jpeg" : "image/png");
            }
            catch (Exception ex)
            {
                ShowError("That picture could not be read: " + ex.Message);
            }
        }

        private void SnapSceneView()
        {
            SceneView view = SceneView.lastActiveSceneView;
            Camera camera = view != null ? view.camera : null;
            if (camera == null || camera.pixelWidth <= 0)
            {
                ShowError("Open a Scene view first.");
                return;
            }

            float scale = Mathf.Min(1f, 1920f / camera.pixelWidth);
            int width = Mathf.Max(16, Mathf.RoundToInt(camera.pixelWidth * scale));
            int height = Mathf.Max(16, Mathf.RoundToInt(camera.pixelHeight * scale));
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var shot = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                shot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                shot.Apply();
                AddPicture("Scene View " + DateTime.Now.ToString("HH-mm-ss") + ".png", shot.EncodeToPNG(), "image/png");
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(shot);
            }
        }

        private void AddPicture(string name, byte[] bytes, string contentType)
        {
            if (_pictures.Count >= MaxPictures) return;
            var preview = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (bytes == null || !preview.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(preview);
                ShowError("That file is not a PNG or JPG picture.");
                return;
            }
            // Too big to send as it is: the same picture as a JPG is a fraction of the size.
            if (bytes.Length > MaxPictureBytes)
            {
                bytes = preview.EncodeToJPG(85);
                contentType = "image/jpeg";
                name = Path.GetFileNameWithoutExtension(name) + ".jpg";
                if (bytes.Length > MaxPictureBytes)
                {
                    UnityEngine.Object.DestroyImmediate(preview);
                    ShowError("That picture is too big. Pictures can be up to " + EditorUtility.FormatBytes(MaxPictureBytes) + ".");
                    return;
                }
            }
            HideError();
            _pictures.Add(new Picture { Name = name, Bytes = bytes, ContentType = contentType, Preview = preview });
            RenderPictures();
        }

        private void RemovePicture(Picture picture)
        {
            if (_sending) return;
            _pictures.Remove(picture);
            if (picture.Preview != null) UnityEngine.Object.DestroyImmediate(picture.Preview);
            RenderPictures();
        }

        // =========================================================================================
        // Sent from this computer
        // =========================================================================================

        private static RecentList LoadRecent()
        {
            string json = EditorPrefs.GetString(RecentKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return new RecentList();
            try { return JsonUtility.FromJson<RecentList>(json) ?? new RecentList(); }
            catch (ArgumentException) { return new RecentList(); }
        }

        private static void Remember(string shortId, string subject, Kind kind)
        {
            RecentList list = LoadRecent();
            list.items.Insert(0, new RecentItem { id = shortId, subject = subject, kind = kind.ToString(), at = DateTime.UtcNow.ToString("o") });
            if (list.items.Count > MaxRecent) list.items.RemoveRange(MaxRecent, list.items.Count - MaxRecent);
            EditorPrefs.SetString(RecentKey, JsonUtility.ToJson(list));
        }

        // =========================================================================================
        // Small pieces
        // =========================================================================================

        private VisualElement Card(string title, VisualElement host = null)
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            var heading = new Label(title);
            heading.AddToClassList("bk-card-title");
            card.Add(heading);
            (host ?? _container).Add(card);
            return card;
        }

        private static Label FieldLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("bk-label");
            label.style.marginTop = 10;
            return label;
        }

        private static Label Hint(string text)
        {
            var hint = new Label(text);
            hint.AddToClassList("bk-hint");
            return hint;
        }

        /// <summary>A row of chips where exactly one is chosen.</summary>
        private static VisualElement ChipRow(string[] options, string selected, Action<string> choose)
        {
            var row = new VisualElement();
            row.AddToClassList("bk-chip-row");
            var chips = new List<Label>();
            foreach (string option in options)
            {
                var chip = new Label(option);
                chip.AddToClassList("bk-chip");
                chip.pickingMode = PickingMode.Position;
                chip.EnableInClassList("bk-chip-active", option == selected);
                string captured = option;
                chip.RegisterCallback<ClickEvent>(_ =>
                {
                    choose(captured);
                    foreach (Label other in chips) other.EnableInClassList("bk-chip-active", other == chip);
                });
                chips.Add(chip);
                row.Add(chip);
            }
            return row;
        }

        private void ShowError(string message)
        {
            if (_error == null)
            {
                Debug.LogWarning("[ParelVR SDK] " + message);
                return;
            }
            _error.text = message;
            _error.AddToClassList("bk-visible");
        }

        private void HideError() => _error?.RemoveFromClassList("bk-visible");

        private void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? string.Empty;
        }

        private static string AccountName()
        {
            string display = ParelSession.DisplayName;
            string user = ParelSession.Username;
            if (string.IsNullOrEmpty(display) || display == user) return user;
            return display + " (@" + user + ")";
        }

        private static bool LooksLikeEmail(string email)
        {
            if (string.IsNullOrEmpty(email) || email.Contains(" ")) return false;
            int at = email.IndexOf('@');
            return at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1 && !email.EndsWith(".");
        }

        private static string Clip(string text, int max) => text == null || text.Length <= max ? text ?? string.Empty : text.Substring(0, max);

        private static string Ago(string iso)
        {
            if (!DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime time)) return string.Empty;
            TimeSpan age = DateTime.UtcNow - time.ToUniversalTime();
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalMinutes < 60) return (int)age.TotalMinutes + "m ago";
            if (age.TotalHours < 24) return (int)age.TotalHours + "h ago";
            if (age.TotalDays < 30) return (int)age.TotalDays + "d ago";
            return time.ToLocalTime().ToString("MMM d");
        }
    }
}
