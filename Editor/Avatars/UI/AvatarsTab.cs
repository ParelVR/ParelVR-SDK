using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Build;
using ParelVR.SDK.Avatars.Validation;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.ControlPanel;
using ParelVR.SDK.Core.Http;
using ParelVR.SDK.Core.Settings;
using ParelVR.SDK.Core.Util;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace ParelVR.SDK.Avatars.UI
{
    /// <summary>
    /// The avatar Builder and Content Manager tabs of the ParelVR SDK Control Panel, laid out like
    /// the VRChat SDK's.
    /// Builder: pick an avatar in the open scenes, fill in its details and thumbnail, work through
    /// the Validations list, then Build &amp; Test (local only) or Build &amp; Publish.
    /// Content Manager: every avatar you've uploaded with its visibility, platforms and performance,
    /// plus Copy ID / Attach / Delete.
    /// </summary>
    public abstract class AvatarsTab : IParelTab
    {
        private readonly bool _contentMode;

        protected AvatarsTab(bool contentMode)
        {
            _contentMode = contentMode;
        }

        public string TabName => _contentMode ? "Content Manager" : "Builder";
        public int TabOrder => _contentMode ? 20 : 10;
        public ParelProjectType RequiredMode => ParelProjectType.Avatar;

        // Shared between the Builder and Content Manager tabs.
        private static ParelAvatarDescriptor _pendingSelection;
        private static ParelAvatarDescriptor _builderSelection;
        private static event Action ContentInvalidated;
        private static event Action SelectionRequested;

        /// <summary>Opens the Control Panel's Builder with this avatar selected.</summary>
        public static void SelectAvatar(ParelAvatarDescriptor descriptor)
        {
            _pendingSelection = descriptor;
            if (descriptor != null) Remember(descriptor);
            ParelControlPanel.ShowTab("Builder");
            SelectionRequested?.Invoke();
        }

        private const string SelectedAvatarKey = "ParelVR.SDK.Avatars.SelectedAvatar";
        private const string AndroidPrefKey = "ParelVR.SDK.Avatars.BuildAndroid";
        private const long MaxWindowsBundleBytes = 200L * 1024 * 1024;
        private const long MaxAndroidBundleBytes = 10L * 1024 * 1024;
        private const int MaxThumbnailBytes = 8 * 1024 * 1024;
        private const int MaxTags = 10;

        private static readonly string[] VisibilityLabels = { "Private", "Public", "Friends Only" };
        private static readonly string[] VisibilityValues = { "private", "public", "friends-only" };
        private static readonly string[] WarningLabels =
        {
            "Sexually Suggestive", "Adult Languages and Themes", "Graphic Violence", "Excessive Gore", "Extreme Horror",
        };

        private static readonly Color FrameColor = new Color(0.08f, 0.08f, 0.11f, 1f);

        // Ticked once per editor session, like the VRChat SDK's upload agreement.
        private static bool _rightsAccepted;

        // ---- UI ------------------------------------------------------------------------------
        private VisualElement _container;
        private VisualElement _builderPage;
        private VisualElement _contentPage;

        private VisualElement _setupCard;
        private VisualElement _builderBody;
        private DropdownField _avatarDropdown;
        private Image _thumbImage;
        private Label _publishTitle;
        private Label _blueprintLabel;
        private VisualElement _statPills;
        private Label _statLine;
        private TextField _nameField;
        private TextField _descriptionField;
        private DropdownField _visibilityField;
        private Toggle _cloneToggle;
        private TextField _tagInput;
        private VisualElement _tagsRow;
        private readonly List<Toggle> _warningToggles = new List<Toggle>();
        private Label _validationSummary;
        private Button _fixAllButton;
        private VisualElement _validationList;
        private Toggle _androidToggle;
        private Toggle _rightsToggle;
        private Button _testButton;
        private Button _publishButton;
        private VisualElement _progressCard;
        private Label _progressLabel;
        private ProgressBar _progressBar;
        private Button _cancelButton;

        private TextField _searchField;
        private Label _contentStatus;
        private VisualElement _contentList;

        // ---- State ---------------------------------------------------------------------------
        private readonly List<ParelAvatarDescriptor> _descriptors = new List<ParelAvatarDescriptor>();
        private ParelAvatarDescriptor _current;
        private string _currentBlueprint;
        private AvatarRecord _existingRecord;
        private bool _blueprintMissing;
        private int _recordRequest;
        private readonly List<string> _tags = new List<string>();
        private Texture2D _pendingTexture;
        private byte[] _pendingThumbnail;
        private bool _pendingThumbnailIsPng;
        private ValidationReport _report;
        private ParelAvatarPerformance _performance;
        private bool _busy;
        private CancellationTokenSource _cts;
        private bool _refreshQueued;
        private bool _subscribed;
        private List<AvatarRecord> _myAvatars;
        private int _contentRequest;
        private readonly Dictionary<string, List<Action<Texture2D>>> _thumbWaiters = new Dictionary<string, List<Action<Texture2D>>>();

        // =========================================================================================
        // IParelTab
        // =========================================================================================

        public void BuildUI(VisualElement container)
        {
            _container = container;

            var root = new VisualElement();
            root.style.flexGrow = 1;
            container.Add(root);

            if (_contentMode)
            {
                _contentPage = new VisualElement();
                root.Add(_contentPage);
                BuildContentPage(_contentPage);
            }
            else
            {
                _builderPage = new VisualElement();
                root.Add(_builderPage);
                BuildBuilderPage(_builderPage);
            }

            container.RegisterCallback<AttachToPanelEvent>(_ => Subscribe());
            container.RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            Subscribe();
        }

        public void OnShown()
        {
            if (_contentMode)
            {
                if (_myAvatars == null) _ = RefreshContentAsync();
                else RenderContent();
            }
            else
            {
                RefreshAvatars();
            }
        }

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            EditorApplication.hierarchyChanged += QueueRefresh;
            Undo.undoRedoPerformed += QueueRefresh;
            ObjectChangeEvents.changesPublished += OnObjectChangesPublished;
            ParelSession.OnSessionChanged += OnSessionChanged;
            ContentInvalidated += OnContentInvalidated;
            SelectionRequested += OnSelectionRequested;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            EditorApplication.hierarchyChanged -= QueueRefresh;
            Undo.undoRedoPerformed -= QueueRefresh;
            ObjectChangeEvents.changesPublished -= OnObjectChangesPublished;
            ParelSession.OnSessionChanged -= OnSessionChanged;
            ContentInvalidated -= OnContentInvalidated;
            SelectionRequested -= OnSelectionRequested;
        }

        private void OnContentInvalidated()
        {
            _myAvatars = null;
            if (_contentMode && IsVisible()) _ = RefreshContentAsync();
        }

        private void OnSelectionRequested()
        {
            if (!_contentMode) QueueRefresh();
        }

        /// <summary>False while this tab (or the Control Panel) is hidden.</summary>
        private bool IsVisible()
        {
            if (_container == null || _container.panel == null) return false;
            for (VisualElement e = _container; e != null; e = e.parent)
            {
                if (e.style.display.value == DisplayStyle.None) return false;
            }
            return true;
        }

        private void OnObjectChangesPublished(ref ObjectChangeEventStream stream) => QueueRefresh();

        private void OnSessionChanged()
        {
            _myAvatars = null;
            _existingRecord = null;
            _currentBlueprint = null;
            if (ParelSession.IsLoggedIn) QueueRefresh();
        }

        /// <summary>Coalesces scene / inspector changes into one refresh on the next editor tick.</summary>
        private void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _refreshQueued = false;
                if (_busy || !IsVisible()) return; // OnShown refreshes when the tab comes back
                if (_contentMode) RenderContent();
                else RefreshAvatars();
            };
        }

        // =========================================================================================
        // Builder -- layout
        // =========================================================================================

        private void BuildBuilderPage(VisualElement page)
        {
            // ---- No avatar in the scene yet ------------------------------------------------------
            _setupCard = Card(page);
            _setupCard.Add(Title("No Avatar in the Scene"));
            _setupCard.Add(Hint("Drag your avatar model into the scene (its Rig set to Humanoid), select it in the Hierarchy and click below. ParelVR adds a Parel Avatar Descriptor and fills in the view point, lip sync and blinking for you."));
            Button setup = new Button(SetUpSelectedObject) { text = "Set Up Selected Object as Avatar" };
            setup.AddToClassList("bk-btn");
            setup.AddToClassList("bk-btn-primary");
            setup.style.marginTop = 10;
            _setupCard.Add(setup);

            _builderBody = new VisualElement();
            page.Add(_builderBody);

            // ---- Avatar picker ------------------------------------------------------------------
            VisualElement pickCard = Card(_builderBody);
            VisualElement pickRow = Row();
            Label pickLabel = Bold("Selected Avatar");
            pickLabel.style.width = 120;
            pickLabel.style.marginBottom = 0;
            pickRow.Add(pickLabel);
            _avatarDropdown = new DropdownField();
            _avatarDropdown.style.flexGrow = 1;
            _avatarDropdown.RegisterValueChangedCallback(_ => OnAvatarPicked());
            pickRow.Add(_avatarDropdown);
            pickRow.Add(SmallButton("Select", () =>
            {
                if (_current == null) return;
                Selection.activeGameObject = _current.gameObject;
                EditorGUIUtility.PingObject(_current.gameObject);
            }));
            pickCard.Add(pickRow);

            // ---- Thumbnail + summary -------------------------------------------------------------
            VisualElement infoCard = Card(_builderBody);
            VisualElement infoRow = Row();
            infoRow.style.alignItems = Align.FlexStart;

            var thumbColumn = new VisualElement();
            thumbColumn.style.width = 224;
            thumbColumn.style.flexShrink = 0;
            VisualElement frame = ThumbnailFrame(224, 168, out _thumbImage, "No thumbnail yet");
            thumbColumn.Add(frame);
            VisualElement thumbButtons = Row();
            thumbButtons.style.marginTop = 6;
            thumbButtons.Add(SmallButton("Portrait", CapturePortrait, true, 0));
            thumbButtons.Add(SmallButton("Scene View", CaptureSceneView, true));
            thumbButtons.Add(SmallButton("File...", PickThumbnailFile, true));
            thumbColumn.Add(thumbButtons);
            infoRow.Add(thumbColumn);

            var summary = new VisualElement();
            summary.style.flexGrow = 1;
            summary.style.flexShrink = 1;
            summary.style.marginLeft = 16;
            _publishTitle = Bold("New Avatar");
            _publishTitle.style.fontSize = 15;
            summary.Add(_publishTitle);
            _blueprintLabel = Hint(string.Empty);
            summary.Add(_blueprintLabel);
            _statPills = Row();
            _statPills.style.flexWrap = Wrap.Wrap;
            _statPills.style.marginTop = 10;
            summary.Add(_statPills);
            _statLine = Hint(string.Empty);
            summary.Add(_statLine);
            summary.Add(Hint("Portrait frames the avatar's head and shoulders automatically. Scene View uses whatever your Scene View camera is looking at. New avatars without a thumbnail get a Portrait when you publish."));
            infoRow.Add(summary);
            infoCard.Add(infoRow);

            // ---- Details --------------------------------------------------------------------------
            VisualElement details = Card(_builderBody);
            details.Add(Title("Avatar Info"));
            details.Add(FieldLabel("Name *"));
            _nameField = new TextField { maxLength = 64 };
            _nameField.AddToClassList("bk-input");
            details.Add(_nameField);

            details.Add(FieldLabel("Description"));
            _descriptionField = new TextField { multiline = true, maxLength = 1000 };
            _descriptionField.AddToClassList("bk-input");
            _descriptionField.style.minHeight = 54;
            _descriptionField.style.whiteSpace = WhiteSpace.Normal;
            details.Add(_descriptionField);

            VisualElement visibilityRow = Row();
            visibilityRow.style.alignItems = Align.FlexEnd;
            var visibilityColumn = new VisualElement();
            visibilityColumn.style.flexGrow = 1;
            visibilityColumn.Add(FieldLabel("Visibility"));
            _visibilityField = new DropdownField(new List<string>(VisibilityLabels), 0);
            visibilityColumn.Add(_visibilityField);
            visibilityRow.Add(visibilityColumn);
            _cloneToggle = new Toggle("Allow Cloning");
            _cloneToggle.style.marginLeft = 16;
            _cloneToggle.style.marginBottom = 4;
            visibilityRow.Add(_cloneToggle);
            details.Add(visibilityRow);
            details.Add(Hint("Private avatars are only visible to you. Public avatars can be found and worn by anyone; Friends Only limits that to your friends. Allow Cloning lets others wear it from your pedestal or profile."));

            details.Add(FieldLabel("Tags"));
            VisualElement tagRow = Row();
            _tagInput = new TextField { maxLength = 32 };
            _tagInput.AddToClassList("bk-input");
            _tagInput.style.flexGrow = 1;
            _tagInput.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                AddTag();
                evt.StopPropagation();
            });
            tagRow.Add(_tagInput);
            tagRow.Add(SmallButton("Add Tag", AddTag));
            details.Add(tagRow);
            _tagsRow = Row();
            _tagsRow.style.flexWrap = Wrap.Wrap;
            _tagsRow.style.marginTop = 6;
            details.Add(_tagsRow);

            var warnings = new Foldout { text = "Content Warnings", value = false };
            warnings.style.marginTop = 10;
            foreach (string label in WarningLabels)
            {
                var toggle = new Toggle(label);
                _warningToggles.Add(toggle);
                warnings.Add(toggle);
            }
            details.Add(warnings);

            // ---- Validations ------------------------------------------------------------------------
            VisualElement validation = Card(_builderBody);
            VisualElement validationHeader = Row();
            validationHeader.AddToClassList("bk-card-titlebar");
            validationHeader.Add(Bold("Validations"));
            validationHeader.Add(Spacer());
            _fixAllButton = SmallButton("Auto Fix All", FixAll);
            validationHeader.Add(_fixAllButton);
            validationHeader.Add(SmallButton("Re-check", () => Revalidate()));
            validation.Add(validationHeader);
            _validationSummary = Hint(string.Empty);
            validation.Add(_validationSummary);
            _validationList = new VisualElement();
            _validationList.style.marginTop = 6;
            validation.Add(_validationList);

            // ---- Build ------------------------------------------------------------------------------
            VisualElement build = Card(_builderBody);
            build.Add(Title("Build"));
            bool androidAvailable = ParelAvatarBuilder.CanBuildFor(BuildTarget.Android);
            _androidToggle = new Toggle("Also publish an Android (Quest) build")
            {
                value = androidAvailable && EditorPrefs.GetBool(AndroidPrefKey, false),
            };
            _androidToggle.SetEnabled(androidAvailable);
            _androidToggle.RegisterValueChangedCallback(evt => EditorPrefs.SetBool(AndroidPrefKey, evt.newValue));
            build.Add(_androidToggle);
            if (!androidAvailable)
            {
                build.Add(Hint("Install Android Build Support in Unity Hub (Installs > Add Modules) to publish for Quest too."));
            }

            VisualElement rightsRow = Row();
            rightsRow.style.alignItems = Align.FlexStart;
            rightsRow.style.marginTop = 10;
            _rightsToggle = new Toggle { value = _rightsAccepted };
            _rightsToggle.RegisterValueChangedCallback(evt =>
            {
                _rightsAccepted = evt.newValue;
                UpdateButtons();
            });
            rightsRow.Add(_rightsToggle);
            var rightsText = new Label("The information provided above is accurate and I have the rights to upload this content to ParelVR.");
            rightsText.style.whiteSpace = WhiteSpace.Normal;
            rightsText.style.flexShrink = 1;
            rightsText.style.marginLeft = 4;
            rightsText.RegisterCallback<ClickEvent>(_ => _rightsToggle.value = !_rightsToggle.value);
            rightsRow.Add(rightsText);
            build.Add(rightsRow);

            VisualElement buttons = Row();
            buttons.style.marginTop = 12;
            _testButton = new Button(() => _ = BuildAndTestAsync()) { text = "Build & Test" };
            _testButton.AddToClassList("bk-btn");
            _testButton.style.flexGrow = 1;
            _testButton.style.height = 34;
            buttons.Add(_testButton);
            _publishButton = new Button(() => _ = PublishAsync()) { text = "Build & Publish" };
            _publishButton.AddToClassList("bk-btn");
            _publishButton.AddToClassList("bk-btn-primary");
            _publishButton.style.flexGrow = 2;
            _publishButton.style.height = 34;
            _publishButton.style.marginLeft = 8;
            _publishButton.style.fontSize = 13;
            buttons.Add(_publishButton);
            build.Add(buttons);

            VisualElement testRow = Row();
            testRow.style.marginTop = 6;
            Label testHint = Hint("Build & Test only builds locally: launch ParelVR on this PC and the avatar is on your Avatars page as \"[Test] <name>\" -- only you see it.");
            testHint.style.flexShrink = 1;
            testHint.style.flexGrow = 1;
            testRow.Add(testHint);
            testRow.Add(SmallButton("Open Test Folder", () =>
            {
                Directory.CreateDirectory(ParelAvatarBuilder.TestAvatarFolder);
                EditorUtility.RevealInFinder(ParelAvatarBuilder.TestAvatarFolder);
            }));
            build.Add(testRow);

            // ---- Progress (outside the body so it stays enabled while busy) ---------------------
            _progressCard = Card(page);
            _progressCard.style.display = DisplayStyle.None;
            VisualElement progressHeader = Row();
            _progressLabel = Bold("Working...");
            _progressLabel.style.flexGrow = 1;
            progressHeader.Add(_progressLabel);
            _cancelButton = SmallButton("Cancel", () => _cts?.Cancel());
            progressHeader.Add(_cancelButton);
            _progressCard.Add(progressHeader);
            _progressBar = new ProgressBar { lowValue = 0, highValue = 100 };
            _progressBar.style.marginTop = 8;
            _progressCard.Add(_progressBar);
        }

        // =========================================================================================
        // Builder -- avatar selection
        // =========================================================================================

        private void CollectDescriptors()
        {
            _descriptors.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    _descriptors.AddRange(root.GetComponentsInChildren<ParelAvatarDescriptor>(true));
                }
            }
        }

        private void RefreshAvatars()
        {
            if (_builderBody == null) return;
            CollectDescriptors();

            bool any = _descriptors.Count > 0;
            _setupCard.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
            _builderBody.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            if (!any)
            {
                _current = null;
                _report = null;
                UpdateButtons();
                return;
            }

            var names = new List<string>();
            var seen = new Dictionary<string, int>();
            foreach (ParelAvatarDescriptor descriptor in _descriptors)
            {
                string name = descriptor.gameObject.name;
                seen.TryGetValue(name, out int count);
                seen[name] = count + 1;
                names.Add(count == 0 ? name : $"{name} ({count + 1})");
            }
            _avatarDropdown.choices = names;

            ParelAvatarDescriptor pick = null;
            if (_pendingSelection != null && _descriptors.Contains(_pendingSelection)) pick = _pendingSelection;
            _pendingSelection = null;
            if (pick == null && _current != null && _descriptors.Contains(_current)) pick = _current;
            if (pick == null) pick = FindRemembered();
            if (pick == null) pick = _descriptors[0];

            _avatarDropdown.SetValueWithoutNotify(names[_descriptors.IndexOf(pick)]);
            SelectDescriptor(pick);
        }

        private ParelAvatarDescriptor FindRemembered()
        {
            string key = SessionState.GetString(SelectedAvatarKey, string.Empty);
            if (string.IsNullOrEmpty(key)) return null;
            foreach (ParelAvatarDescriptor descriptor in _descriptors)
            {
                if (GlobalObjectId.GetGlobalObjectIdSlow(descriptor).ToString() == key) return descriptor;
            }
            return null;
        }

        private static void Remember(ParelAvatarDescriptor descriptor)
        {
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(descriptor);
            if (id.identifierType != 0) SessionState.SetString(SelectedAvatarKey, id.ToString());
        }

        private void OnAvatarPicked()
        {
            int index = _avatarDropdown.index;
            if (index < 0 || index >= _descriptors.Count || _descriptors[index] == null) return;
            Remember(_descriptors[index]);
            SelectDescriptor(_descriptors[index]);
        }

        private void SelectDescriptor(ParelAvatarDescriptor descriptor)
        {
            bool changed = descriptor != _current || descriptor.BlueprintId != (_currentBlueprint ?? string.Empty);
            _current = descriptor;
            _builderSelection = descriptor;
            if (changed)
            {
                _currentBlueprint = descriptor.BlueprintId;
                ResetForm(descriptor);
            }
            UpdateHeader();
            Revalidate();
        }

        private void ResetForm(ParelAvatarDescriptor descriptor)
        {
            _existingRecord = null;
            _blueprintMissing = false;
            _nameField.SetValueWithoutNotify(descriptor.gameObject.name);
            _descriptionField.SetValueWithoutNotify(string.Empty);
            _visibilityField.SetValueWithoutNotify(VisibilityLabels[0]);
            _cloneToggle.SetValueWithoutNotify(false);
            _tags.Clear();
            RenderTags();
            foreach (Toggle toggle in _warningToggles) toggle.SetValueWithoutNotify(false);
            ClearPendingThumbnail();
            _thumbImage.image = null;

            int request = ++_recordRequest;
            if (!string.IsNullOrEmpty(descriptor.BlueprintId)) _ = LoadExistingAsync(descriptor.BlueprintId, request);
        }

        private async Task LoadExistingAsync(string avatarId, int request)
        {
            _blueprintLabel.text = $"Blueprint ID: {avatarId} (loading...)";
            try
            {
                AvatarRecord record = await AvatarApiClient.GetAvatarAsync(avatarId);
                if (request != _recordRequest) return;
                if (record == null)
                {
                    _blueprintMissing = true;
                    return;
                }
                _existingRecord = record;
                FillForm(record, request);
            }
            catch (ParelApiException ex) when (ex.StatusCode == 403 || ex.StatusCode == 404)
            {
                if (request == _recordRequest) _blueprintMissing = true;
            }
            catch (Exception ex)
            {
                if (request == _recordRequest) Debug.LogWarning($"[ParelVR SDK] Couldn't load avatar {avatarId}: {ex.Message}");
            }
            finally
            {
                if (request == _recordRequest)
                {
                    UpdateHeader();
                    RenderStats();
                }
            }
        }

        private void FillForm(AvatarRecord record, int request)
        {
            _nameField.SetValueWithoutNotify(record.name ?? string.Empty);
            _descriptionField.SetValueWithoutNotify(record.description ?? string.Empty);
            int visibility = Array.IndexOf(VisibilityValues, record.releaseStatus);
            _visibilityField.SetValueWithoutNotify(VisibilityLabels[Mathf.Max(0, visibility)]);
            _cloneToggle.SetValueWithoutNotify(record.cloneable);

            _tags.Clear();
            if (record.tags != null) _tags.AddRange(record.tags.Where(t => !string.IsNullOrWhiteSpace(t)));
            RenderTags();

            var warnings = new HashSet<string>((record.contentWarnings ?? new List<string>()).Select(w => (w ?? string.Empty).ToLowerInvariant()));
            for (int i = 0; i < _warningToggles.Count; i++)
            {
                _warningToggles[i].SetValueWithoutNotify(warnings.Contains(WarningLabels[i].ToLowerInvariant()));
            }

            if (_pendingTexture == null && !string.IsNullOrEmpty(record.thumbnailUrl))
            {
                LoadThumb(record.thumbnailUrl, texture =>
                {
                    if (request == _recordRequest && _pendingTexture == null) _thumbImage.image = texture;
                });
            }
        }

        private void UpdateHeader()
        {
            if (_current == null) return;
            string avatarId = _current.BlueprintId;
            bool isNew = string.IsNullOrEmpty(avatarId);

            if (isNew)
            {
                _publishTitle.text = "New Avatar";
                _blueprintLabel.text = "Blueprint ID: assigned the first time you publish.";
                _publishButton.text = "Build & Publish";
            }
            else
            {
                _publishTitle.text = _existingRecord != null ? "Update: " + _existingRecord.name : "Update Avatar";
                _blueprintLabel.text = _blueprintMissing
                    ? $"Blueprint ID: {avatarId} -- not found on your account. Publishing will offer to upload it as a new avatar."
                    : $"Blueprint ID: {avatarId}";
                _publishButton.text = "Build & Publish Update";
            }
        }

        private void SetUpSelectedObject()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null || EditorUtility.IsPersistent(selected))
            {
                EditorUtility.DisplayDialog("Set Up Avatar", "Select your avatar in the Hierarchy first. If it isn't in the scene yet, drag the model in from the Project window.", "OK");
                return;
            }

            Animator animator = selected.GetComponentInParent<Animator>(true);
            if (animator == null) animator = selected.GetComponentInChildren<Animator>(true);
            GameObject root = animator != null ? animator.gameObject : selected;

            if (animator == null || !animator.isHuman)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Not a Humanoid",
                    $"'{root.name}' isn't a Humanoid rig. ParelVR avatars need Rig > Animation Type set to Humanoid on the model's import settings.\n\nAdd the descriptor anyway?",
                    "Add Anyway",
                    "Cancel");
                if (!proceed) return;
            }

            ParelAvatarDescriptor descriptor = root.GetComponent<ParelAvatarDescriptor>();
            if (descriptor == null) descriptor = Undo.AddComponent<ParelAvatarDescriptor>(root);
            Undo.RecordObject(descriptor, "Auto Detect Avatar");
            descriptor.AutoDetect();
            EditorUtility.SetDirty(descriptor);
            EditorSceneManager.MarkSceneDirty(root.scene);

            _pendingSelection = descriptor;
            Remember(descriptor);
            RefreshAvatars();
        }

        // =========================================================================================
        // Builder -- validation, stats, tags, thumbnail
        // =========================================================================================

        private ValidationReport Revalidate()
        {
            _validationList.Clear();
            if (_current == null)
            {
                _report = null;
                UpdateButtons();
                return null;
            }

            _report = ParelAvatarDescriptorValidator.Validate(_current);
            _performance = ParelAvatarPerformance.Measure(_current.gameObject);
            RenderStats();

            List<ValidationIssue> issues = _report.Issues.OrderByDescending(i => (int)i.Level).ToList();
            int errors = issues.Count(i => i.Level == ValidationIssueLevel.Error);
            int warnings = issues.Count(i => i.Level == ValidationIssueLevel.Warning);
            int fixable = issues.Count(i => i.HasAutoFix);

            _validationSummary.text = errors == 0 && warnings == 0
                ? "No problems found -- this avatar is ready to build."
                : $"{errors} error(s), {warnings} warning(s). Errors must be fixed before you can build.";
            _fixAllButton.style.display = fixable > 1 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (ValidationIssue issue in issues)
            {
                VisualElement row = Row();
                row.style.marginTop = 6;
                string level = issue.Level == ValidationIssueLevel.Error ? "ERROR" : issue.Level == ValidationIssueLevel.Warning ? "WARNING" : "INFO";
                string pillClass = issue.Level == ValidationIssueLevel.Error ? "bk-pill-bad" : issue.Level == ValidationIssueLevel.Warning ? "bk-pill-warn" : "bk-pill-neutral";
                Label pill = Pill(level, pillClass);
                pill.style.width = 70;
                pill.style.marginBottom = 0;
                pill.style.marginRight = 10;
                row.Add(pill);

                var message = new Label(issue.Message);
                message.style.whiteSpace = WhiteSpace.Normal;
                message.style.flexShrink = 1;
                message.style.flexGrow = 1;
                row.Add(message);

                if (issue.Context != null)
                {
                    UnityEngine.Object context = issue.Context;
                    row.Add(SmallButton("Select", () =>
                    {
                        if (context == null) return;
                        Selection.activeObject = context is Component component ? component.gameObject : context;
                        EditorGUIUtility.PingObject(Selection.activeObject);
                    }));
                }
                if (issue.HasAutoFix)
                {
                    ValidationIssue captured = issue;
                    row.Add(SmallButton("Auto Fix", () =>
                    {
                        RunFix(captured);
                        Revalidate();
                    }));
                }
                _validationList.Add(row);
            }

            UpdateButtons();
            return _report;
        }

        private void FixAll()
        {
            if (_report == null) return;
            foreach (ValidationIssue issue in _report.Issues.Where(i => i.HasAutoFix).ToList()) RunFix(issue);
            Revalidate();
        }

        private static void RunFix(ValidationIssue issue)
        {
            try
            {
                issue.AutoFix();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ParelVR SDK] Auto Fix failed for \"{issue.Message}\": {ex.Message}");
            }
        }

        private void RenderStats()
        {
            if (_statPills == null) return;
            _statPills.Clear();
            if (_performance != null)
            {
                ParelAvatarPerformance.Rank rank = _performance.OverallRank;
                _statPills.Add(Pill(ParelAvatarPerformance.RankLabel(rank), RankPillClass(ParelAvatarPerformance.RankLabel(rank))));
                _statLine.text = $"{_performance.Triangles:N0} triangles  ·  {_performance.MaterialSlots} material slots  ·  {_performance.SkinnedMeshes} skinned meshes  ·  {_performance.Bones} bones";
            }
            if (_existingRecord != null)
            {
                _statPills.Add(Pill(VisibilityLabel(_existingRecord.releaseStatus), VisibilityPillClass(_existingRecord.releaseStatus)));
                if (HasBuild(_existingRecord.platforms?.windows)) _statPills.Add(Pill("PC", "bk-pill-accent"));
                if (HasBuild(_existingRecord.platforms?.android)) _statPills.Add(Pill("Quest", "bk-pill-accent"));
            }
        }

        private void UpdateButtons()
        {
            if (_testButton == null) return;
            bool canBuild = !_busy && _current != null && _report != null && _report.CanBuild;
            _testButton.SetEnabled(canBuild);
            _publishButton.SetEnabled(canBuild && _rightsAccepted);
        }

        private void AddTag()
        {
            string tag = (_tagInput.value ?? string.Empty).Trim().ToLowerInvariant();
            if (tag.Length == 0) return;
            if (_tags.Count >= MaxTags)
            {
                EditorUtility.DisplayDialog("Tags", $"An avatar can have up to {MaxTags} tags.", "OK");
                return;
            }
            if (!_tags.Contains(tag)) _tags.Add(tag);
            _tagInput.SetValueWithoutNotify(string.Empty);
            RenderTags();
        }

        private void RenderTags()
        {
            _tagsRow.Clear();
            foreach (string tag in _tags)
            {
                string captured = tag;
                Label pill = Pill(tag + "   ×", "bk-pill-neutral");
                pill.tooltip = "Remove tag";
                pill.RegisterCallback<ClickEvent>(_ =>
                {
                    _tags.Remove(captured);
                    RenderTags();
                });
                _tagsRow.Add(pill);
            }
        }

        private void CapturePortrait()
        {
            if (_current == null) return;
            Texture2D texture = ParelAvatarThumbnail.CapturePortrait(_current.gameObject);
            if (texture == null)
            {
                EditorUtility.DisplayDialog("Thumbnail", "Couldn't render the avatar.", "OK");
                return;
            }
            SetPendingThumbnail(texture, texture.EncodeToJPG(92), false);
        }

        private void CaptureSceneView()
        {
            Texture2D texture = ParelAvatarThumbnail.CaptureSceneView();
            if (texture == null)
            {
                EditorUtility.DisplayDialog("Thumbnail", "Open a Scene View and frame your avatar the way you want the picture first.", "OK");
                return;
            }
            SetPendingThumbnail(texture, texture.EncodeToJPG(92), false);
        }

        private void PickThumbnailFile()
        {
            string path = EditorUtility.OpenFilePanel("Choose Thumbnail", string.Empty, "png,jpg,jpeg");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
                bool jpg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
                var texture = new Texture2D(2, 2);
                if ((!png && !jpg) || !texture.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    EditorUtility.DisplayDialog("Thumbnail", "That file isn't a PNG or JPEG image.", "OK");
                    return;
                }
                if (bytes.Length > MaxThumbnailBytes)
                {
                    bytes = texture.EncodeToJPG(90);
                    png = false;
                }
                SetPendingThumbnail(texture, bytes, png);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Thumbnail", "Couldn't read that file:\n" + ex.Message, "OK");
            }
        }

        private void SetPendingThumbnail(Texture2D texture, byte[] bytes, bool isPng)
        {
            ClearPendingThumbnail();
            _pendingTexture = texture;
            _pendingThumbnail = bytes;
            _pendingThumbnailIsPng = isPng;
            _thumbImage.image = texture;
        }

        private void ClearPendingThumbnail()
        {
            if (_pendingTexture != null) UnityEngine.Object.DestroyImmediate(_pendingTexture);
            _pendingTexture = null;
            _pendingThumbnail = null;
            _pendingThumbnailIsPng = false;
        }

        // =========================================================================================
        // Builder -- Build & Test / Build & Publish
        // =========================================================================================

        private bool CanStartBuild(out ParelAvatarDescriptor descriptor)
        {
            descriptor = _current;
            if (_busy) return false;
            if (descriptor == null)
            {
                EditorUtility.DisplayDialog("ParelVR SDK", "Pick an avatar first.", "OK");
                return false;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("ParelVR SDK", "Exit Play Mode before building an avatar.", "OK");
                return false;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorUtility.DisplayDialog("ParelVR SDK", "Unity is still compiling or importing. Try again when it's done.", "OK");
                return false;
            }
            ValidationReport report = Revalidate();
            if (report == null || !report.CanBuild)
            {
                EditorUtility.DisplayDialog("ParelVR SDK", "Fix the errors listed under Validations first.", "OK");
                return false;
            }
            return true;
        }

        private async Task BuildAndTestAsync()
        {
            if (!CanStartBuild(out ParelAvatarDescriptor descriptor)) return;
            string avatarName = string.IsNullOrWhiteSpace(_nameField.value) ? descriptor.gameObject.name : _nameField.value.Trim();

            BeginBusy("Building test avatar...", false);
            try
            {
                await Task.Yield();
                string path = ParelAvatarBuilder.TestAvatarPath(avatarName);
                ParelAvatarBuildResult result = ParelAvatarBuilder.Build(descriptor, BuildTarget.StandaloneWindows64, path);
                SetProgress(100, "Test build ready");

                string rank = result.Performance != null ? ParelAvatarPerformance.RankLabel(result.Performance.OverallRank) : "Unknown";
                string message = $"'{avatarName}' is ready to test.\n\nIn ParelVR on this PC, open the Avatars page -- it's at the top of the list as \"[Test] {avatarName}\". Only you can see it; nothing was uploaded.\n\nSize: {EditorUtility.FormatBytes(result.BundleBytes)}    Performance: {rank}";
                if (result.RemovedComponents.Count > 0) message += "\n\nLeft out of the build: " + string.Join(", ", result.RemovedComponents.Distinct());
                if (result.Notes.Count > 0) message += "\n\nAvatar Tools:\n" + string.Join("\n", result.Notes.Distinct());
                EditorUtility.DisplayDialog("Build & Test", message, "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError("[ParelVR SDK] Avatar test build failed: " + ex);
                EditorUtility.DisplayDialog("Build Failed", ex.Message, "OK");
            }
            finally
            {
                EndBusy();
                QueueRefresh();
            }
        }

        private async Task PublishAsync()
        {
            if (!CanStartBuild(out ParelAvatarDescriptor descriptor)) return;

            string avatarName = (_nameField.value ?? string.Empty).Trim();
            if (avatarName.Length == 0)
            {
                EditorUtility.DisplayDialog("Build & Publish", "Give your avatar a name first.", "OK");
                return;
            }
            if (!_rightsAccepted)
            {
                EditorUtility.DisplayDialog("Build & Publish", "Tick the box confirming you have the rights to upload this avatar.", "OK");
                return;
            }
            if (!ParelSession.IsLoggedIn)
            {
                EditorUtility.DisplayDialog("Build & Publish", "Sign in to the ParelVR SDK first.", "OK");
                return;
            }

            bool buildAndroid = _androidToggle.value && ParelAvatarBuilder.CanBuildFor(BuildTarget.Android);
            string workDir = Path.Combine(Path.GetTempPath(), "ParelVRAvatarUpload", Guid.NewGuid().ToString("N"));
            var notes = new List<string>();

            BeginBusy("Building for Windows...", true);
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            try
            {
                // 1. Build first so a broken build never leaves an empty avatar on your account.
                await Task.Yield();
                ParelAvatarBuildResult windows = ParelAvatarBuilder.Build(descriptor, BuildTarget.StandaloneWindows64, Path.Combine(workDir, "windows.bundle"));
                if (windows.BundleBytes > MaxWindowsBundleBytes)
                {
                    throw new Exception($"The Windows build is {EditorUtility.FormatBytes(windows.BundleBytes)}; the limit is {EditorUtility.FormatBytes(MaxWindowsBundleBytes)}. Lower texture sizes or remove unused meshes.");
                }

                ParelAvatarBuildResult quest = null;
                if (buildAndroid)
                {
                    SetProgress(15, "Building for Android (Quest)...");
                    await Task.Yield();
                    quest = ParelAvatarBuilder.Build(descriptor, BuildTarget.Android, Path.Combine(workDir, "android.bundle"));
                    if (quest.BundleBytes > MaxAndroidBundleBytes)
                    {
                        notes.Add($"The Quest build is {EditorUtility.FormatBytes(quest.BundleBytes)}, over the {EditorUtility.FormatBytes(MaxAndroidBundleBytes)} limit, so it wasn't uploaded.");
                        quest = null;
                    }
                }
                if (windows.RemovedComponents.Count > 0)
                {
                    notes.Add("Left out of the upload: " + string.Join(", ", windows.RemovedComponents.Distinct()));
                }
                if (windows.Notes.Count > 0) notes.Add("Avatar Tools:\n" + string.Join("\n", windows.Notes.Distinct()));

                // 2. Create the avatar (first publish) or update its details.
                SetProgress(30, "Saving avatar details...");
                AvatarApiClient.AvatarUpsertRequest request = BuildRequest(avatarName, windows);
                AvatarRecord record = await UpsertAsync(descriptor, request, notes, ct);

                // 3. Bundles.
                float windowsSpan = quest != null ? 30f : 55f;
                SetProgress(35, "Uploading Windows build...");
                byte[] windowsBytes = File.ReadAllBytes(windows.BundlePath);
                AvatarResponse uploaded = await AvatarApiClient.UploadBundleAsync(
                    record.id, "windows", windowsBytes,
                    new Progress<float>(p => SetProgress(35 + p * windowsSpan, $"Uploading Windows build... {(int)(p * 100)}%")),
                    ct);
                if (uploaded?.avatar != null && !string.IsNullOrEmpty(uploaded.avatar.id)) record = uploaded.avatar;

                if (quest != null)
                {
                    byte[] questBytes = File.ReadAllBytes(quest.BundlePath);
                    AvatarResponse questUploaded = await AvatarApiClient.UploadBundleAsync(
                        record.id, "android", questBytes,
                        new Progress<float>(p => SetProgress(65 + p * 25, $"Uploading Quest build... {(int)(p * 100)}%")),
                        ct);
                    if (questUploaded?.avatar != null && !string.IsNullOrEmpty(questUploaded.avatar.id)) record = questUploaded.avatar;
                }

                // 4. Thumbnail -- the one you picked, or an automatic portrait for brand-new avatars.
                byte[] thumbnail = _pendingThumbnail;
                bool thumbnailIsPng = _pendingThumbnailIsPng;
                if (thumbnail == null && string.IsNullOrEmpty(record.thumbnailUrl))
                {
                    Texture2D portrait = ParelAvatarThumbnail.CapturePortrait(descriptor.gameObject);
                    if (portrait != null)
                    {
                        thumbnail = portrait.EncodeToJPG(92);
                        thumbnailIsPng = false;
                        SetPendingThumbnail(portrait, thumbnail, false);
                    }
                }
                if (thumbnail != null)
                {
                    SetProgress(92, "Uploading thumbnail...");
                    try
                    {
                        AvatarResponse image = await AvatarApiClient.UploadImageAsync(record.id, thumbnail, thumbnailIsPng, ct);
                        if (image?.avatar != null && !string.IsNullOrEmpty(image.avatar.id)) record = image.avatar;
                        else if (!string.IsNullOrEmpty(image?.thumbnailUrl)) record.thumbnailUrl = image.thumbnailUrl;
                        _pendingThumbnail = null; // keep the preview, but don't upload it again next time
                    }
                    catch (ParelApiException ex)
                    {
                        notes.Add($"The thumbnail didn't upload ({ex.Message}). Publish again to retry.");
                    }
                }

                SetProgress(100, "Published");
                _existingRecord = record;
                _currentBlueprint = descriptor.BlueprintId;
                _blueprintMissing = false;
                ContentInvalidated?.Invoke(); // the Content Manager refetches
                UpdateHeader();
                RenderStats();

                string message = $"'{record.name}' is live on ParelVR ({VisibilityLabel(record.releaseStatus)}).\n\nYou'll find it on the Avatars page in game.";
                if (notes.Count > 0) message += "\n\n" + string.Join("\n\n", notes);
                EditorUtility.DisplayDialog("Avatar Published", message, "OK");
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[ParelVR SDK] Avatar upload canceled.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[ParelVR SDK] Avatar publish failed: " + ex);
                EditorUtility.DisplayDialog("Publish Failed", ex.Message, "OK");
            }
            finally
            {
                EndBusy();
                TryDeleteDirectory(workDir);
                QueueRefresh();
            }
        }

        private AvatarApiClient.AvatarUpsertRequest BuildRequest(string avatarName, ParelAvatarBuildResult windows)
        {
            ParelAvatarPerformance perf = windows.Performance ?? _performance;
            int visibility = Mathf.Clamp(_visibilityField.index, 0, VisibilityValues.Length - 1);
            return new AvatarApiClient.AvatarUpsertRequest
            {
                name = avatarName,
                description = _descriptionField.value ?? string.Empty,
                authorName = string.IsNullOrEmpty(ParelSession.Username) ? ParelSession.DisplayName : ParelSession.Username,
                releaseStatus = VisibilityValues[visibility],
                cloneable = _cloneToggle.value,
                tags = new List<string>(_tags),
                contentWarnings = SelectedWarnings(),
                performance = new AvatarPerformanceRecord
                {
                    rank = perf != null ? ParelAvatarPerformance.RankLabel(perf.OverallRank) : "Unknown",
                    downloadBytes = windows.BundleBytes,
                    triangleCount = perf?.Triangles ?? 0,
                    materialCount = perf?.MaterialSlots ?? 0,
                    skinnedMeshCount = perf?.SkinnedMeshes ?? 0,
                },
            };
        }

        /// <summary>
        /// PATCHes the avatar the descriptor's Blueprint ID points at, or creates a new one (first
        /// publish, or the old one is gone and you agree to upload as new) and stores its id on the
        /// descriptor so the next publish updates it.
        /// </summary>
        private async Task<AvatarRecord> UpsertAsync(ParelAvatarDescriptor descriptor, AvatarApiClient.AvatarUpsertRequest request, List<string> notes, CancellationToken ct)
        {
            string avatarId = descriptor.BlueprintId;
            if (!string.IsNullOrEmpty(avatarId))
            {
                try
                {
                    AvatarRecord updated = await AvatarApiClient.UpdateAvatarAsync(avatarId, request, ct);
                    if (updated == null || string.IsNullOrEmpty(updated.id))
                    {
                        updated = await AvatarApiClient.GetAvatarAsync(avatarId, ct);
                    }
                    return updated ?? new AvatarRecord { id = avatarId, name = request.name, releaseStatus = request.releaseStatus };
                }
                catch (ParelApiException ex) when (ex.StatusCode == 403 || ex.StatusCode == 404)
                {
                    bool asNew = EditorUtility.DisplayDialog(
                        "Avatar Not Found",
                        $"This avatar's Blueprint ID ({avatarId}) doesn't match an avatar on your account -- it may have been deleted, or it belongs to someone else.\n\nUpload it as a new avatar instead?",
                        "Upload as New",
                        "Cancel");
                    if (!asNew) throw new OperationCanceledException();
                }
            }

            AvatarRecord created = await AvatarApiClient.CreateAvatarAsync(request, ct);
            if (created == null || string.IsNullOrEmpty(created.id)) throw new Exception("The server didn't return the new avatar.");

            ParelBlueprint.Assign(descriptor, created.id, "Assign Blueprint ID");
            _currentBlueprint = created.id;

            Scene scene = descriptor.gameObject.scene;
            EditorSceneManager.MarkSceneDirty(scene);
            if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
            else notes.Add("Save your scene so this avatar keeps its Blueprint ID -- otherwise your next publish creates a second avatar.");

            Debug.Log($"[ParelVR SDK] New avatar created: {created.id}");
            return created;
        }

        private List<string> SelectedWarnings()
        {
            var warnings = new List<string>();
            for (int i = 0; i < _warningToggles.Count; i++)
            {
                if (_warningToggles[i].value) warnings.Add(WarningLabels[i]);
            }
            return warnings;
        }

        private void BeginBusy(string status, bool cancellable)
        {
            _busy = true;
            _builderBody?.SetEnabled(false);
            _contentPage?.SetEnabled(false);
            _progressCard.style.display = DisplayStyle.Flex;
            _cancelButton.style.display = cancellable ? DisplayStyle.Flex : DisplayStyle.None;
            SetProgress(0, status);
            UpdateButtons();
        }

        private void EndBusy()
        {
            _busy = false;
            _builderBody?.SetEnabled(true);
            _contentPage?.SetEnabled(true);
            _progressCard.style.display = DisplayStyle.None;
            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }
            UpdateButtons();
        }

        private void SetProgress(float percent, string status)
        {
            _progressBar.value = percent;
            _progressBar.title = $"{(int)percent}%";
            _progressLabel.text = status;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
                // It's in the system temp folder; Windows cleans it up eventually.
            }
        }

        // =========================================================================================
        // Content Manager
        // =========================================================================================

        private void BuildContentPage(VisualElement page)
        {
            VisualElement toolbar = Card(page);
            VisualElement row = Row();
            Label searchLabel = Bold("Search");
            searchLabel.style.marginBottom = 0;
            searchLabel.style.marginRight = 8;
            row.Add(searchLabel);
            _searchField = new TextField();
            _searchField.AddToClassList("bk-input");
            _searchField.style.flexGrow = 1;
            _searchField.RegisterValueChangedCallback(_ => RenderContent());
            row.Add(_searchField);
            row.Add(SmallButton("Refresh", () => _ = RefreshContentAsync()));
            toolbar.Add(row);
            _contentStatus = Hint(string.Empty);
            toolbar.Add(_contentStatus);

            _contentList = new VisualElement();
            page.Add(_contentList);
        }

        private async Task RefreshContentAsync()
        {
            int request = ++_contentRequest;
            _contentStatus.text = "Loading your avatars...";
            try
            {
                List<AvatarRecord> avatars = await AvatarApiClient.GetMyAvatarsAsync();
                if (request != _contentRequest) return;
                _myAvatars = avatars ?? new List<AvatarRecord>();
                RenderContent();
            }
            catch (Exception ex)
            {
                if (request != _contentRequest) return;
                _contentStatus.text = "Couldn't load your avatars: " + ex.Message;
            }
        }

        private void RenderContent()
        {
            if (_contentList == null) return;
            _contentList.Clear();
            if (_myAvatars == null) return;

            CollectDescriptors();
            string filter = (_searchField.value ?? string.Empty).Trim();
            List<AvatarRecord> shown = _myAvatars
                .Where(a => a != null && (filter.Length == 0
                    || (a.name ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || (a.id ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            _contentStatus.text = _myAvatars.Count == 0
                ? "You haven't uploaded any avatars yet. Build & Publish one from the Builder."
                : $"Showing {shown.Count} of {_myAvatars.Count} avatar(s).";

            foreach (AvatarRecord record in shown) _contentList.Add(BuildContentRow(record));
        }

        private VisualElement BuildContentRow(AvatarRecord record)
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            VisualElement row = Row();
            row.style.alignItems = Align.FlexStart;

            VisualElement frame = ThumbnailFrame(144, 108, out Image image, "No thumbnail");
            LoadThumb(record.thumbnailUrl, texture => image.image = texture);
            row.Add(frame);

            var info = new VisualElement();
            info.style.flexGrow = 1;
            info.style.flexShrink = 1;
            info.style.marginLeft = 14;
            Label name = Bold(string.IsNullOrEmpty(record.name) ? "(unnamed)" : record.name);
            name.style.fontSize = 14;
            info.Add(name);
            info.Add(Hint(record.id));

            VisualElement pills = Row();
            pills.style.flexWrap = Wrap.Wrap;
            pills.style.marginTop = 8;
            pills.Add(Pill(VisibilityLabel(record.releaseStatus), VisibilityPillClass(record.releaseStatus)));
            bool pc = HasBuild(record.platforms?.windows);
            bool questBuild = HasBuild(record.platforms?.android);
            if (pc) pills.Add(Pill("PC", "bk-pill-accent"));
            if (questBuild) pills.Add(Pill("Quest", "bk-pill-accent"));
            if (!pc && !questBuild) pills.Add(Pill("No build uploaded", "bk-pill-bad"));
            string rank = record.performance?.rank;
            if (!string.IsNullOrEmpty(rank) && rank != "Unknown") pills.Add(Pill(rank, RankPillClass(rank)));
            info.Add(pills);

            string updated = FormatDate(string.IsNullOrEmpty(record.updatedAt) ? record.createdAt : record.updatedAt);
            if (updated != null) info.Add(Hint("Updated " + updated));

            ParelAvatarDescriptor inScene = _descriptors.FirstOrDefault(d => d != null && d.BlueprintId == record.id);
            if (inScene != null) info.Add(Hint($"In this scene: {inScene.gameObject.name}"));
            row.Add(info);

            var actions = new VisualElement();
            actions.style.width = 180;
            actions.style.flexShrink = 0;
            actions.style.marginLeft = 12;
            int visibility = Array.IndexOf(VisibilityValues, record.releaseStatus);
            var visibilityField = new DropdownField(new List<string>(VisibilityLabels), Mathf.Max(0, visibility));
            visibilityField.style.marginLeft = 4;
            visibilityField.style.marginBottom = 4;
            visibilityField.RegisterValueChangedCallback(evt => _ = ChangeVisibilityAsync(record, visibilityField, evt.previousValue));
            actions.Add(visibilityField);
            actions.Add(StackButton("Copy ID", () => EditorGUIUtility.systemCopyBuffer = record.id));
            if (inScene != null)
            {
                actions.Add(StackButton("Open in Builder", () => SelectAvatar(inScene)));
            }
            else
            {
                actions.Add(StackButton("Attach to Selected Avatar", () => AttachBlueprint(record)));
            }
            Button delete = StackButton("Delete", () => _ = DeleteAsync(record));
            delete.AddToClassList("bk-btn-danger");
            actions.Add(delete);
            row.Add(actions);

            card.Add(row);
            return card;
        }

        private async Task ChangeVisibilityAsync(AvatarRecord record, DropdownField field, string previousLabel)
        {
            int index = field.index;
            if (index < 0 || VisibilityValues[index] == record.releaseStatus) return;

            field.SetEnabled(false);
            try
            {
                AvatarApiClient.AvatarUpsertRequest request = RequestFromRecord(record);
                request.releaseStatus = VisibilityValues[index];
                AvatarRecord updated = await AvatarApiClient.UpdateAvatarAsync(record.id, request);
                record.releaseStatus = updated != null && !string.IsNullOrEmpty(updated.releaseStatus) ? updated.releaseStatus : request.releaseStatus;
                if (_existingRecord != null && _existingRecord.id == record.id) _existingRecord.releaseStatus = record.releaseStatus;
                RenderContent();
            }
            catch (Exception ex)
            {
                field.SetValueWithoutNotify(previousLabel);
                EditorUtility.DisplayDialog("Couldn't Change Visibility", ex.Message, "OK");
            }
            finally
            {
                field.SetEnabled(true);
            }
        }

        private static AvatarApiClient.AvatarUpsertRequest RequestFromRecord(AvatarRecord record)
        {
            return new AvatarApiClient.AvatarUpsertRequest
            {
                name = record.name,
                description = record.description ?? string.Empty,
                authorName = string.IsNullOrEmpty(record.authorName) ? ParelSession.Username : record.authorName,
                releaseStatus = string.IsNullOrEmpty(record.releaseStatus) ? "private" : record.releaseStatus,
                cloneable = record.cloneable,
                tags = record.tags != null ? new List<string>(record.tags) : new List<string>(),
                contentWarnings = record.contentWarnings != null ? new List<string>(record.contentWarnings) : new List<string>(),
                performance = record.performance ?? new AvatarPerformanceRecord { rank = "Unknown" },
            };
        }

        private void AttachBlueprint(AvatarRecord record)
        {
            CollectDescriptors();
            ParelAvatarDescriptor picked = _contentMode ? _builderSelection : _current;
            ParelAvatarDescriptor target = picked != null && _descriptors.Contains(picked) ? picked : null;
            if (target == null && Selection.activeGameObject != null) target = Selection.activeGameObject.GetComponentInParent<ParelAvatarDescriptor>();
            if (target == null)
            {
                EditorUtility.DisplayDialog("Attach Blueprint", "Pick the avatar to attach it to in the Builder (or select it in the Hierarchy) first.", "OK");
                return;
            }

            string message = string.IsNullOrEmpty(target.BlueprintId)
                ? $"Attach '{record.name}' ({record.id}) to '{target.gameObject.name}'?\n\nPublishing '{target.gameObject.name}' will then update '{record.name}' instead of creating a new avatar."
                : $"'{target.gameObject.name}' is attached to {target.BlueprintId}. Replace that with '{record.name}' ({record.id})?";
            if (!EditorUtility.DisplayDialog("Attach Blueprint", message, "Attach", "Cancel")) return;

            ParelBlueprint.Assign(target, record.id, "Attach Blueprint ID");
            SelectAvatar(target);
        }

        private async Task DeleteAsync(AvatarRecord record)
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Delete Avatar",
                $"Delete '{record.name}'?\n\nIt disappears from ParelVR for everyone, including anyone wearing it. This can't be undone.",
                "Delete",
                "Cancel");
            if (!confirmed) return;

            try
            {
                await AvatarApiClient.DeleteAvatarAsync(record.id);
                _myAvatars?.Remove(record);
                if (_existingRecord != null && _existingRecord.id == record.id)
                {
                    _existingRecord = null;
                    _blueprintMissing = true;
                    UpdateHeader();
                }
                RenderContent();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Couldn't Delete Avatar", ex.Message, "OK");
            }
        }

        // =========================================================================================
        // Helpers
        // =========================================================================================

        /// <summary>
        /// Loads a remote thumbnail through the shared cache. Every caller waiting on the same URL is
        /// served when it arrives (the cache itself only remembers the first caller's callback).
        /// </summary>
        private void LoadThumb(string url, Action<Texture2D> apply)
        {
            if (string.IsNullOrEmpty(url)) return;
            Texture2D cached = ParelTextureCache.GetOrFetch(url, texture =>
            {
                if (!_thumbWaiters.TryGetValue(url, out List<Action<Texture2D>> waiting)) return;
                _thumbWaiters.Remove(url);
                foreach (Action<Texture2D> callback in waiting) callback(texture);
            });
            if (cached != null)
            {
                apply(cached);
                return;
            }
            if (!_thumbWaiters.TryGetValue(url, out List<Action<Texture2D>> list))
            {
                list = new List<Action<Texture2D>>();
                _thumbWaiters[url] = list;
            }
            list.Add(apply);
        }

        private static VisualElement ThumbnailFrame(float width, float height, out Image image, string emptyText)
        {
            var frame = new VisualElement();
            frame.style.width = width;
            frame.style.height = height;
            frame.style.flexShrink = 0;
            frame.style.backgroundColor = FrameColor;
            frame.style.borderTopLeftRadius = 0;
            frame.style.borderTopRightRadius = 0;
            frame.style.borderBottomLeftRadius = 0;
            frame.style.borderBottomRightRadius = 0;
            frame.style.overflow = Overflow.Hidden;

            // The note sits underneath the image, so it shows only while there's no picture.
            Label empty = Hint(emptyText);
            empty.style.position = Position.Absolute;
            empty.style.left = 0;
            empty.style.right = 0;
            empty.style.top = 0;
            empty.style.bottom = 0;
            empty.style.marginTop = 0;
            empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            frame.Add(empty);

            image = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            image.style.position = Position.Absolute;
            image.style.left = 0;
            image.style.right = 0;
            image.style.top = 0;
            image.style.bottom = 0;
            frame.Add(image);
            return frame;
        }

        private static bool HasBuild(AvatarPlatformRecord platform) => platform != null && !string.IsNullOrEmpty(platform.bundleUrl);

        private static string VisibilityLabel(string status)
        {
            switch (status)
            {
                case "public": return "Public";
                case "friends-only": return "Friends Only";
                case "user-list": return "Selected Users";
                default: return "Private";
            }
        }

        private static string VisibilityPillClass(string status)
        {
            switch (status)
            {
                case "public": return "bk-pill-good";
                case "friends-only":
                case "user-list": return "bk-pill-accent";
                default: return "bk-pill-neutral";
            }
        }

        private static string RankPillClass(string rank)
        {
            switch ((rank ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant())
            {
                case "excellent":
                case "good": return "bk-pill-good";
                case "medium": return "bk-pill-accent";
                case "poor": return "bk-pill-warn";
                case "verypoor": return "bk-pill-bad";
                default: return "bk-pill-neutral";
            }
        }

        private static string FormatDate(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utc))
            {
                return utc.ToLocalTime().ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
            }
            return iso;
        }

        private static VisualElement Row()
        {
            var row = new VisualElement();
            row.AddToClassList("bk-row");
            return row;
        }

        private static VisualElement Spacer()
        {
            var spacer = new VisualElement();
            spacer.AddToClassList("bk-spacer");
            return spacer;
        }

        private static VisualElement Card(VisualElement parent)
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            parent.Add(card);
            return card;
        }

        private static Label Bold(string text)
        {
            var label = new Label(text);
            label.AddToClassList("bk-label");
            return label;
        }

        /// <summary>The title bar across the top of a card.</summary>
        private static Label Title(string text)
        {
            var label = new Label(text);
            label.AddToClassList("bk-card-title");
            return label;
        }

        private static Label FieldLabel(string text)
        {
            Label label = Bold(text);
            label.style.marginTop = 10;
            return label;
        }

        private static Label Hint(string text)
        {
            var label = new Label(text);
            label.AddToClassList("bk-hint");
            return label;
        }

        private static Label Pill(string text, string modifierClass)
        {
            var pill = new Label(text);
            pill.AddToClassList("bk-pill");
            pill.AddToClassList(modifierClass);
            pill.style.marginRight = 6;
            pill.style.marginBottom = 4;
            return pill;
        }

        private static Button SmallButton(string text, Action onClick, bool grow = false, float marginLeft = 4)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("bk-btn");
            button.style.marginLeft = marginLeft;
            if (grow) button.style.flexGrow = 1;
            return button;
        }

        private static Button StackButton(string text, Action onClick)
        {
            Button button = SmallButton(text, onClick);
            button.style.marginBottom = 4;
            return button;
        }
    }

    /// <summary>The Control Panel's avatar Builder tab.</summary>
    public sealed class AvatarBuilderTab : AvatarsTab
    {
        public AvatarBuilderTab() : base(false)
        {
        }
    }

    /// <summary>The Control Panel's avatar Content Manager tab.</summary>
    public sealed class AvatarContentManagerTab : AvatarsTab
    {
        public AvatarContentManagerTab() : base(true)
        {
        }
    }
}
