using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Build;
using ParelVR.SDK.Build.Quest;
using ParelVR.SDK.Core.ControlPanel;
using ParelVR.SDK.Core.Settings;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Worlds.Components;
using ParelVR.SDK.Worlds.Validation;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace ParelVR.SDK.Worlds.UI
{
    public class WorldsTab : IParelTab
    {
        public string TabName => "Builder";
        public int TabOrder => 10;
        public ParelProjectType RequiredMode => ParelProjectType.World;

        // --- UI References ---
        private VisualElement _container;

        private VisualElement _setupContainer;
        private Button _btnSetupScene;

        private VisualElement _publishContainer;
        private Label _publishTitle;
        private Label _blueprintIdLabel;
        private Button _btnDetachBlueprint;
        private TextField _worldName;
        private TextField _worldDesc;
        /// <summary>Every ParelVR lobby holds the same number of players; creators cannot change it.</summary>
        private const int LobbyCapacity = 50;

        private Toggle _worldAllowDebug;
        private TextField _worldTagInput;
        private Button _btnAddTag;
        private VisualElement _tagsContainer;
        private Toggle _warningSexual;
        private Toggle _warningAdult;
        private Toggle _warningViolence;
        private Toggle _warningGore;
        private Toggle _warningHorror;

        private Image _thumbnailPreview;
        private Button _btnCaptureThumbnail;
        private Button _btnSelectImage;
        private Button _btnBuildPublish;
        private Button _btnSaveDetails;
        private Toggle _platformPc;
        private Toggle _platformStandalone;
        private Label _platformStatus;
        /// <summary>The Meta / Standalone VR conversion of the world in the scene, as last fetched.</summary>
        private ParelQuestConversionRecord _questStatus;

        private VisualElement _validationErrorsContainer;
        private Label _validationHeader;
        private ScrollView _validationErrorsList;

        private VisualElement _buildSection;
        private readonly WorldCreditsTab _creditsSection = new WorldCreditsTab();
        private readonly WorldMonetizationTab _monetizationSection = new WorldMonetizationTab();

        private VisualElement _progressContainer;
        private Label _progressStatus;
        private ProgressBar _progressBar;

        // --- State ---
        private ParelWorldDescriptor _currentDescriptor;
        private byte[] _pendingThumbnailBytes;
        private CancellationTokenSource _uploadCts;
        private List<string> _currentTags = new List<string>();

        public void BuildUI(VisualElement container)
        {
            _container = container;
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                ParelVR.SDK.Core.ParelPackagePaths.Combine("Editor/Worlds/UI/WorldsTab.uxml"));
            if (uxml == null)
            {
                Debug.LogError("[WorldsTab] WorldsTab.uxml not found.");
                return;
            }

            var tree = uxml.Instantiate();
            tree.style.flexGrow = 1;
            container.Add(tree);

            // Setup state
            _setupContainer = tree.Q<VisualElement>("setup-container");
            _btnSetupScene = tree.Q<Button>("btn-setup-scene");

            // Publish state
            _publishContainer = tree.Q<VisualElement>("publish-container");
            _publishTitle = tree.Q<Label>("publish-title");
            _blueprintIdLabel = tree.Q<Label>("blueprint-id-label");
            _btnDetachBlueprint = tree.Q<Button>("btn-detach-blueprint");
            if (_btnDetachBlueprint != null) _btnDetachBlueprint.clicked += DetachBlueprint;
            _worldName = tree.Q<TextField>("world-name");
            _worldDesc = tree.Q<TextField>("world-desc");
            
            _worldAllowDebug = tree.Q<Toggle>("world-allow-debug");
            
            _worldTagInput = tree.Q<TextField>("world-tag-input");
            _btnAddTag = tree.Q<Button>("btn-add-tag");
            _tagsContainer = tree.Q<VisualElement>("tags-container");
            
            _warningSexual = tree.Q<Toggle>("warning-sexual");
            _warningAdult = tree.Q<Toggle>("warning-adult");
            _warningViolence = tree.Q<Toggle>("warning-violence");
            _warningGore = tree.Q<Toggle>("warning-gore");
            _warningHorror = tree.Q<Toggle>("warning-horror");

            _thumbnailPreview = tree.Q<Image>("thumbnail-preview");
            _btnCaptureThumbnail = tree.Q<Button>("btn-capture-thumbnail");
            _btnSelectImage = tree.Q<Button>("btn-select-image");
            _btnBuildPublish = tree.Q<Button>("btn-build-publish");
            _platformPc = tree.Q<Toggle>("platform-pc");
            _platformStandalone = tree.Q<Toggle>("platform-standalone");
            _platformStatus = tree.Q<Label>("platform-status");
            _platformPc?.RegisterValueChangedCallback(_ => RenderPlatformStatus());
            _platformStandalone?.RegisterValueChangedCallback(_ => RenderPlatformStatus());
            // While the standalone version is being made, its status is fetched again now and then (only while the window shows it).
            _platformStatus?.schedule.Execute(PollQuestStatus).Every(20000);

            // Validation
            _validationErrorsContainer = tree.Q<VisualElement>("validation-errors-container");
            _validationHeader = tree.Q<Label>("validation-header");
            _validationErrorsList = tree.Q<ScrollView>("validation-errors-list");

            // Progress state
            _progressContainer = tree.Q<VisualElement>("progress-container");
            _progressStatus = tree.Q<Label>("progress-status");
            _progressBar = tree.Q<ProgressBar>("progress-bar");

            // The Build section is docked at the bottom of the window, under the scrolling page.
            _buildSection = tree.Q<VisualElement>("build-section");
            VisualElement footer = ParelControlPanel.FooterFor(container);
            if (footer != null)
            {
                if (_buildSection != null) footer.Add(_buildSection);
                footer.Add(_progressContainer);
            }

            VisualElement creditsHost = tree.Q<VisualElement>("credits-host");
            if (creditsHost != null) _creditsSection.BuildUI(creditsHost);
            VisualElement monetizationHost = tree.Q<VisualElement>("monetization-host");
            if (monetizationHost != null) _monetizationSection.BuildUI(monetizationHost);

            // Wire events
            _btnSetupScene.clicked += SetupScene;
            _btnCaptureThumbnail.clicked += CaptureThumbnail;
            _btnSelectImage.clicked += SelectCustomThumbnail;
            _btnAddTag.clicked += AddTagFromInput;
            _worldTagInput.RegisterCallback<KeyDownEvent>(evt => 
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    AddTagFromInput();
                    evt.StopPropagation();
                }
            });
            _btnBuildPublish.clicked += () => _ = BuildAndPublishAsync();
            // Name, description, tags, thumbnail, credits and store, without building the world again.
            _btnSaveDetails = new Button(() => _ = SaveDetailsAsync()) { text = "Save Details (no rebuild)" };
            foreach (string c in _btnBuildPublish.GetClasses()) _btnSaveDetails.AddToClassList(c);
            _btnSaveDetails.style.marginTop = 6;
            _btnSaveDetails.style.display = DisplayStyle.None;
            if (_btnBuildPublish.parent != null) _btnBuildPublish.parent.Insert(_btnBuildPublish.parent.IndexOf(_btnBuildPublish) + 1, _btnSaveDetails);

            ParelSession.OnSessionChanged += RefreshState;
            EditorApplication.hierarchyChanged += RefreshState;
            EditorUserBuildSettings.activeBuildTargetChanged += OnBuildTargetChanged;
            container.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                ParelSession.OnSessionChanged -= RefreshState;
                EditorApplication.hierarchyChanged -= RefreshState;
                EditorUserBuildSettings.activeBuildTargetChanged -= OnBuildTargetChanged;
            });

            RefreshState();
        }

        private void OnBuildTargetChanged()
        {
            RefreshState();
        }

        /// <summary>
        /// Worlds are built on PC. A project left on another platform (the SDK used to switch it to Android for Quest
        /// builds) is offered the switch back; false when the build can't start yet.
        /// </summary>
        private static bool EnsureEditorOnPc()
        {
            if (WorldBuilder.EditorOnPc) return true;
            BuildTarget target = WorldBuilder.Target;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                EditorUtility.DisplayDialog("Windows Build Support Missing",
                    "Worlds are built on PC, and this Unity Editor has no Windows build support.\n\n" +
                    "Add it in Unity Hub > Installs > this version > Add Modules, then publish again.", "OK");
                return false;
            }
            if (EditorUtility.DisplayDialog("Switch to PC",
                    $"This project is set to build for {EditorUserBuildSettings.activeBuildTarget}. Worlds are built on PC; the Meta / Standalone VR " +
                    "version is made by ParelVR's servers.\n\nSwitch the project to Windows? Unity reimports the project's assets, which can take a while. Publish again when it's done.",
                    "Switch", "Cancel"))
                EditorUserBuildSettings.SwitchActiveBuildTargetAsync(group, target);
            return false;
        }

        private bool _pollingQuest;

        private async void PollQuestStatus()
        {
            string worldId = _currentDescriptor != null ? _currentDescriptor.blueprintId : null;
            if (_pollingQuest || _questStatus == null || !_questStatus.InProgress || string.IsNullOrEmpty(worldId) || !ParelSession.IsLoggedIn) return;
            _pollingQuest = true;
            try
            {
                var answer = await ParelQuestApiClient.GetStatusAsync(ParelQuestApiClient.Worlds, worldId);
                if (_currentDescriptor != null && _currentDescriptor.blueprintId == worldId)
                {
                    _questStatus = answer?.questConversion;
                    RenderPlatformStatus();
                }
            }
            catch (Exception)
            {
                // Offline or signed out: the next tick tries again.
            }
            finally
            {
                _pollingQuest = false;
            }
        }

        private List<string> SelectedTargets() => ParelPlatformTargets.From(_platformPc == null || _platformPc.value, _platformStandalone != null && _platformStandalone.value);

        /// <summary>The line under the platform toggles: where the standalone version is at, or what turning it on does.</summary>
        private void RenderPlatformStatus()
        {
            if (_platformStatus == null) return;
            bool pc = _platformPc == null || _platformPc.value;
            bool standalone = _platformStandalone != null && _platformStandalone.value;
            string text;
            if (!pc && !standalone) text = "Pick at least one platform.";
            else if (!standalone) text = "Meta / Standalone VR players won't be able to join this world.";
            else if (_questStatus != null && !string.IsNullOrEmpty(_questStatus.status))
            {
                text = "Meta / Standalone VR version: " + _questStatus.Label + ".";
                if (!string.IsNullOrEmpty(_questStatus.Failed ? _questStatus.message : _questStatus.summary))
                    text += " " + (_questStatus.Failed ? _questStatus.message : _questStatus.summary);
            }
            else text = "The Meta / Standalone VR version is made after each publish and usually takes a few minutes.";
            if (!WorldBuilder.EditorOnPc) text += "\nThis project is set to " + EditorUserBuildSettings.activeBuildTarget + "; publishing switches it back to Windows.";
            _platformStatus.text = text;
            _platformStatus.EnableInClassList("bk-text-bad", (!pc && !standalone) || (_questStatus != null && _questStatus.Failed && standalone));
        }

        public void OnShown()
        {
            RefreshState();
        }

        // ─────────────────────────────────────────────────────────────────
        //  State Management
        // ─────────────────────────────────────────────────────────────────

        private void RefreshState()
        {
            if (_progressContainer == null) return; // Prevent errors during window teardown

            RenderPlatformStatus();

            if (!ParelSession.IsLoggedIn)
            {
                _setupContainer.style.display = DisplayStyle.None;
                _publishContainer.style.display = DisplayStyle.None;
                ShowBuild(false);
                _currentDescriptor = null;
                return;
            }

            var newDescriptor = UnityEngine.Object.FindAnyObjectByType<ParelWorldDescriptor>();
            
            var allBehaviours = new List<MonoBehaviour>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    allBehaviours.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true));
                }
            }
            bool hasSpawnPoint = allBehaviours.Any(IsSpawnPoint);

            if (newDescriptor == null || !hasSpawnPoint)
            {
                // Missing one or both → show setup button
                _setupContainer.style.display = DisplayStyle.Flex;
                _publishContainer.style.display = DisplayStyle.None;
                ShowBuild(false);
                _currentDescriptor = null;
            }
            else
            {
                // Both found → show publish form
                _setupContainer.style.display = DisplayStyle.None;
                _publishContainer.style.display = DisplayStyle.Flex;

                // Only wipe out the form if we are detecting a NEW descriptor 
                // (e.g. switching scenes or first setup)
                bool isNewDescriptor = (_currentDescriptor != newDescriptor);
                _currentDescriptor = newDescriptor;
                if (_btnDetachBlueprint != null)
                    _btnDetachBlueprint.style.display = string.IsNullOrEmpty(newDescriptor.blueprintId) ? DisplayStyle.None : DisplayStyle.Flex;
                ShowBuild(true);
                if (isNewDescriptor)
                {
                    _creditsSection.OnShown();
                    _monetizationSection.OnShown();
                }

                if (string.IsNullOrEmpty(_currentDescriptor.blueprintId))
                {
                    _publishTitle.text = "Publish New World";
                    _blueprintIdLabel.text = "Blueprint ID: None (will be assigned on publish)";
                    _btnBuildPublish.text = "Build & Publish New World";
                    if (_btnSaveDetails != null) _btnSaveDetails.style.display = DisplayStyle.None;

                    if (isNewDescriptor)
                    {
                        _worldName.value = "";
                        _worldDesc.value = "";
                        _worldAllowDebug.value = false;
                        _currentTags.Clear();
                        RenderTags();
                        ClearWarnings();
                        ResetPlatforms();
                        _pendingThumbnailBytes = null;
                        if (_thumbnailPreview != null) _thumbnailPreview.image = null;
                    }
                }
                else
                {
                    _publishTitle.text = "Update Existing World";
                    _blueprintIdLabel.text = $"Blueprint ID: {_currentDescriptor.blueprintId}";
                    _btnBuildPublish.text = "Build & Update World";
                    if (_btnSaveDetails != null) _btnSaveDetails.style.display = DisplayStyle.Flex;

                    if (isNewDescriptor)
                    {
                        _worldName.value = "";
                        _worldDesc.value = "";
                        _worldAllowDebug.value = false;
                        _currentTags.Clear();
                        RenderTags();
                        ClearWarnings();
                        ResetPlatforms();
                        _pendingThumbnailBytes = null;
                        if (_thumbnailPreview != null) _thumbnailPreview.image = null;
                        _ = FetchExistingWorldDataAsync(_currentDescriptor.blueprintId);
                    }
                }
            }
        }

        /// <summary>
        /// Cuts the scene loose from the world it publishes to. The uploaded world is left as it is; the next
        /// publish from this scene makes a new one. What is typed in the form stays.
        /// </summary>
        private void DetachBlueprint()
        {
            if (_currentDescriptor == null || string.IsNullOrEmpty(_currentDescriptor.blueprintId)) return;
            if (!EditorUtility.DisplayDialog("Detach Blueprint ID",
                    "This scene will no longer be linked to the world " + _currentDescriptor.blueprintId + ".\n\n" +
                    "The uploaded world is not deleted or changed. The next Build & Publish from this scene creates a new world. " +
                    "You can link the scene back from the Content Manager with Use in This Scene.",
                    "Detach", "Cancel")) return;

            Undo.RecordObject(_currentDescriptor, "Detach Blueprint ID");
            _currentDescriptor.blueprintId = string.Empty;
            EditorUtility.SetDirty(_currentDescriptor);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(_currentDescriptor.gameObject.scene);
            RefreshState();
        }

        private void ShowBuild(bool show)
        {
            if (_buildSection != null) _buildSection.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void ResetPlatforms()
        {
            _platformPc?.SetValueWithoutNotify(true);
            _platformStandalone?.SetValueWithoutNotify(true);
            _questStatus = null;
            RenderPlatformStatus();
        }

        private void ClearWarnings()
        {
            _warningSexual.value = false;
            _warningAdult.value = false;
            _warningViolence.value = false;
            _warningGore.value = false;
            _warningHorror.value = false;
        }

        private void AddTagFromInput()
        {
            string tag = _worldTagInput.value.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(tag) && !_currentTags.Contains(tag))
            {
                _currentTags.Add(tag);
                _worldTagInput.value = "";
                RenderTags();
            }
        }

        private void RenderTags()
        {
            _tagsContainer.Clear();
            foreach (var tag in _currentTags)
            {
                var pill = new VisualElement();
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.AddToClassList("bk-pill");
                pill.AddToClassList("bk-pill-neutral");
                pill.style.paddingLeft = 8;
                pill.style.paddingRight = 4;
                pill.style.paddingTop = 2;
                pill.style.paddingBottom = 2;
                pill.style.marginRight = 4;
                pill.style.marginBottom = 4;

                var label = new Label(tag);
                label.style.fontSize = 11;
                pill.Add(label);

                var delBtn = new Button(() => {
                    _currentTags.Remove(tag);
                    RenderTags();
                });
                delBtn.text = "x";
                delBtn.style.backgroundColor = Color.clear;
                delBtn.style.borderTopWidth = 0;
                delBtn.style.borderBottomWidth = 0;
                delBtn.style.borderLeftWidth = 0;
                delBtn.style.borderRightWidth = 0;
                delBtn.style.color = new Color(0.8f, 0.4f, 0.4f);
                delBtn.style.fontSize = 10;
                delBtn.style.marginTop = 0;
                delBtn.style.marginBottom = 0;
                delBtn.style.paddingTop = 0;
                delBtn.style.paddingBottom = 0;
                pill.Add(delBtn);

                _tagsContainer.Add(pill);
            }
        }

        // The SDK's ParelVR Spawn Point (and the game's own SpawnPoint, in projects that have it).
        private static bool IsSpawnPoint(MonoBehaviour s)
        {
            return s != null && (s is global::ParelVR.WorldSDK.ParelVRSpawnPoint || s.GetType().Name == "SpawnPoint");
        }

        private void SetupScene()
        {
            var descriptor = UnityEngine.Object.FindAnyObjectByType<ParelWorldDescriptor>();
            
            var allBehaviours = new List<MonoBehaviour>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    allBehaviours.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true));
                }
            }
            var spawnPoint = allBehaviours.FirstOrDefault(IsSpawnPoint);

            if (descriptor == null && spawnPoint == null)
            {
                var go = new GameObject("[ParelVR World]");
                go.AddComponent<ParelWorldDescriptor>();
                var spawn = new GameObject("Spawn Point");
                spawn.transform.SetParent(go.transform, false);
                spawn.AddComponent<global::ParelVR.WorldSDK.ParelVRSpawnPoint>();
                Undo.RegisterCreatedObjectUndo(go, "Setup ParelVR World");
            }
            else if (descriptor == null && spawnPoint != null)
            {
                Undo.AddComponent<ParelWorldDescriptor>(spawnPoint.gameObject);
            }
            else if (descriptor != null && spawnPoint == null)
            {
                var spawn = new GameObject("Spawn Point");
                spawn.transform.SetParent(descriptor.transform, false);
                spawn.AddComponent<global::ParelVR.WorldSDK.ParelVRSpawnPoint>();
                Undo.RegisterCreatedObjectUndo(spawn, "Add Spawn Point");
            }

            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                SceneManager.GetActiveScene());
            RefreshState();
        }

        private async Task FetchExistingWorldDataAsync(string blueprintId)
        {
            try
            {
                var myWorlds = await WorldApiClient.GetMyWorldsAsync();
                var world = myWorlds.Find(w => w.id == blueprintId);
                if (world != null)
                {
                    _worldName.value = world.name;
                    _worldDesc.value = world.description ?? "";
                    
                    _worldAllowDebug.value = world.allowDebugging;
                    
                    if (world.tags != null)
                    {
                        _currentTags = new List<string>(world.tags);
                        RenderTags();
                    }
                    
                    ClearWarnings();
                    if (world.contentWarnings != null)
                    {
                        var warningsLower = world.contentWarnings.Select(w => w.ToLowerInvariant()).ToList();
                        if (warningsLower.Contains("sexually suggestive")) _warningSexual.value = true;
                        if (warningsLower.Contains("adult languages and themes")) _warningAdult.value = true;
                        if (warningsLower.Contains("graphic violence")) _warningViolence.value = true;
                        if (warningsLower.Contains("excessive gore")) _warningGore.value = true;
                        if (warningsLower.Contains("extreme horror")) _warningHorror.value = true;
                    }

                    _platformPc?.SetValueWithoutNotify(ParelPlatformTargets.Has(world.targetPlatforms, ParelPlatformTargets.Pc));
                    _platformStandalone?.SetValueWithoutNotify(ParelPlatformTargets.Has(world.targetPlatforms, ParelPlatformTargets.Standalone));
                    _questStatus = world.questConversion;
                    RenderPlatformStatus();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WorldsTab] Could not fetch existing world data: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Thumbnail
        // ─────────────────────────────────────────────────────────────────

        private void CaptureThumbnail()
        {
            Texture2D tex = ThumbnailCapturer.CaptureSceneView(800, 600);
            if (tex != null)
            {
                _thumbnailPreview.image = tex;
                _pendingThumbnailBytes = tex.EncodeToJPG(85);
            }
            else
            {
                EditorUtility.DisplayDialog("Error",
                    "Could not capture Scene View. Ensure a Scene View is open.", "OK");
            }
        }

        private void SelectCustomThumbnail()
        {
            string path = EditorUtility.OpenFilePanel("Select Custom Thumbnail", "", "png,jpg,jpeg");
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    byte[] fileBytes = File.ReadAllBytes(path);
                    Texture2D tex = new Texture2D(2, 2);
                    if (tex.LoadImage(fileBytes))
                    {
                        _thumbnailPreview.image = tex;
                        // Use the original bytes directly without re-encoding to save quality
                        _pendingThumbnailBytes = fileBytes;
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Error", "Selected file could not be loaded as an image.", "OK");
                    }
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Error", $"Failed to read file:\n{ex.Message}", "OK");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Validation
        // ─────────────────────────────────────────────────────────────────

        private async Task<bool> RunValidationAsync()
        {
            _validationErrorsContainer.style.display = DisplayStyle.None;
            _validationErrorsList.Clear();

            var report = await new WorldValidator().ValidateAsync();

            // Warnings don't stop the build, but they are listed so they can be fixed.
            if (report.Issues.All(i => i.Level == Core.Validation.ValidationIssueLevel.Info))
                return true;

            _validationErrorsContainer.style.display = DisplayStyle.Flex;
            if (_validationHeader != null)
                _validationHeader.text = report.CanBuild ? "Worth fixing (the build still works):" : "Fix these before building:";

            var fixable = report.Issues.Where(i => i.HasAutoFix).ToList();
            if (fixable.Count > 1)
            {
                _validationErrorsList.Add(new Button(() =>
                {
                    if (!EditorUtility.DisplayDialog("Auto Fix All",
                            $"Apply all {fixable.Count} fixes?", "Fix All", "Cancel"))
                        return;
                    foreach (var issue in fixable) issue.AutoFix();
                    _ = RunValidationAsync();
                }) { text = "Auto Fix All" });
            }

            foreach (var issue in report.Issues)
            {
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, marginTop = 4 }
                };

                var msg = new Label($"[{issue.Level}] {issue.Message}")
                {
                    style = { whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
                };
                if (issue.Level == Core.Validation.ValidationIssueLevel.Error)
                    msg.style.color = new Color(0.9f, 0.3f, 0.3f);
                else if (issue.Level == Core.Validation.ValidationIssueLevel.Warning)
                    msg.style.color = new Color(0.9f, 0.7f, 0.2f);

                row.Add(msg);

                if (issue.HasAutoFix)
                {
                    var fixBtn = new Button(() =>
                    {
                        issue.AutoFix();
                        _ = RunValidationAsync();
                    })
                    { text = "Auto Fix" };
                    row.Add(fixBtn);
                }

                _validationErrorsList.Add(row);
            }

            return report.CanBuild;
        }

        private List<string> GetSelectedWarnings()
        {
            var warnings = new List<string>();
            if (_warningSexual.value) warnings.Add("Sexually Suggestive");
            if (_warningAdult.value) warnings.Add("Adult Languages and Themes");
            if (_warningViolence.value) warnings.Add("Graphic Violence");
            if (_warningGore.value) warnings.Add("Excessive Gore");
            if (_warningHorror.value) warnings.Add("Extreme Horror");
            return warnings;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Build & Publish (single-button chain)
        // ─────────────────────────────────────────────────────────────────

        private async Task SaveDetailsAsync()
        {
            if (_currentDescriptor == null || string.IsNullOrEmpty(_currentDescriptor.blueprintId)) return;
            if (string.IsNullOrWhiteSpace(_worldName.value))
            {
                EditorUtility.DisplayDialog("Error", "World Name is required.", "OK");
                return;
            }
            _btnBuildPublish.SetEnabled(false);
            _btnSaveDetails.SetEnabled(false);
            _progressContainer.style.display = DisplayStyle.Flex;
            _progressBar.value = 10;
            _progressStatus.text = "Saving world details...";
            try
            {
                string blueprintId = _currentDescriptor.blueprintId;
                WorldPagePublisher.BuildJson(_currentDescriptor);
                var request = new WorldApiClient.CreateWorldRequest
                {
                    blueprintId = blueprintId,
                    name = _worldName.value,
                    description = _worldDesc.value,
                    authorName = ParelSession.Username ?? "Unknown",
                    releaseStatus = "public",
                    tags = new List<string>(_currentTags),
                    recommendedCapacity = LobbyCapacity,
                    maximumCapacity = LobbyCapacity,
                    allowDebugging = _worldAllowDebug.value,
                    portalEnabled = true,
                    contentWarnings = GetSelectedWarnings()
                };
                var worldRecord = await WorldApiClient.CreateWorldAsync(request);
                if (worldRecord == null) throw new Exception("Failed to save the world details.");

                _progressBar.value = 45;
                _progressStatus.text = "Saving credits and store...";
                await WorldPagePublisher.PublishAsync(_currentDescriptor, blueprintId, CancellationToken.None);

                if (_pendingThumbnailBytes != null)
                {
                    _progressBar.value = 75;
                    _progressStatus.text = "Uploading thumbnail...";
                    await WorldApiClient.UploadImageAsync(blueprintId, _pendingThumbnailBytes);
                    _pendingThumbnailBytes = null;
                }
                _progressBar.value = 100;
                EditorUtility.DisplayDialog("Saved", $"World '{worldRecord.name}' details saved. The world itself was not rebuilt.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorldsTab] Save details failed: {ex.Message}");
                EditorUtility.DisplayDialog("Save Error", ex.Message, "OK");
            }
            finally
            {
                _btnBuildPublish.SetEnabled(true);
                _btnSaveDetails.SetEnabled(true);
                _progressContainer.style.display = DisplayStyle.None;
            }
        }

        private async Task BuildAndPublishAsync()
        {
            if (_currentDescriptor == null) return;

            if (string.IsNullOrWhiteSpace(_worldName.value))
            {
                EditorUtility.DisplayDialog("Error", "World Name is required.", "OK");
                return;
            }

            List<string> targets = SelectedTargets();
            bool forPc = targets.Contains(ParelPlatformTargets.Pc);
            bool forStandalone = targets.Contains(ParelPlatformTargets.Standalone);
            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("Platforms", "Pick at least one platform under Platforms.", "OK");
                return;
            }
            if (!EnsureEditorOnPc()) return;
            if (!await ParelSession.ValidateAsync(force: true))
            {
                EditorUtility.DisplayDialog("Build & Publish", "Your ParelVR session has ended. Sign in again on the Authentication tab, then publish.", "OK");
                return;
            }

            // ── 1. Validate ──
            _progressContainer.style.display = DisplayStyle.Flex;
            _progressStatus.text = "Validating scene...";
            _progressBar.value = 0;
            _btnBuildPublish.SetEnabled(false);

            bool isValid = await RunValidationAsync();
            if (!isValid)
            {
                _progressContainer.style.display = DisplayStyle.None;
                _btnBuildPublish.SetEnabled(true);
                return;
            }

            _uploadCts = new CancellationTokenSource();
            var publishTimer = System.Diagnostics.Stopwatch.StartNew();
            Scene publishScene = SceneManager.GetActiveScene();
            Debug.Log($"[PublishDiag] start scene='{publishScene.path}' name='{publishScene.name}' " +
                      $"playing={Application.isPlaying} loadedScenes={SceneManager.sceneCount}");

            try
            {
                // Credits and Monetization are checked up front: a rejected credit link or a
                // duplicate product id stops the publish before the long build starts.
                WorldPagePublisher.BuildJson(_currentDescriptor);

                // ── 2. Create or Update world profile ──
                _progressStatus.text = "Saving world profile...";
                _progressBar.value = 10;

                string blueprintId = _currentDescriptor.blueprintId;

                var request = new WorldApiClient.CreateWorldRequest
                {
                    blueprintId = blueprintId, // empty → create new, filled → update
                    name = _worldName.value,
                    description = _worldDesc.value,
                    authorName = ParelSession.Username ?? "Unknown",
                    releaseStatus = "public",
                    tags = new List<string>(_currentTags),
                    recommendedCapacity = LobbyCapacity,
                    maximumCapacity = LobbyCapacity,
                    allowDebugging = _worldAllowDebug.value,
                    portalEnabled = true,
                    contentWarnings = GetSelectedWarnings()
                };

                var worldRecord = await WorldApiClient.CreateWorldAsync(request, _uploadCts.Token);
                if (worldRecord == null)
                    throw new Exception("Failed to create/update world profile.");

                // ── 3. Save blueprintId to descriptor (if new) ──
                if (string.IsNullOrEmpty(blueprintId))
                {
                    _currentDescriptor.blueprintId = worldRecord.id;
                    EditorUtility.SetDirty(_currentDescriptor);
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                        SceneManager.GetActiveScene());
                    UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                    Debug.Log($"[WorldsTab] New world created. Blueprint ID: {worldRecord.id}");
                }

                blueprintId = worldRecord.id;

                // A platform left out stops being offered (the server keeps its last build aside).
                var targetsAnswer = await ParelQuestApiClient.SetTargetsAsync(ParelQuestApiClient.Worlds, blueprintId, targets, _uploadCts.Token);
                if (targetsAnswer?.questConversion != null) _questStatus = targetsAnswer.questConversion;

                // ── 3b. Credits, shop theme and monetization ──
                _progressStatus.text = "Saving credits and store...";
                _progressBar.value = 15;
                try
                {
                    await WorldPagePublisher.PublishAsync(_currentDescriptor, blueprintId, _uploadCts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception pageError)
                {
                    // The world itself still publishes; the page can be sent again with the next publish.
                    Debug.LogWarning($"[WorldsTab] Credits / store were not saved: {pageError.Message}");
                }

                // ── 4. Build AssetBundle ──
                _progressStatus.text = "Building AssetBundle (this may take a while)...";
                _progressBar.value = 20;

                // One PC build. With Meta / Standalone VR on, what it was built from is packed for ParelVR's servers too.
                WorldBuilder.ExportQuestSource = forStandalone;
                string bundlePath = await OutsidePlayerLoop.Run(WorldBuilder.BuildWorldBundle);
                if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
                    throw new Exception("AssetBundle build failed. Check the Unity Console.");
                ParelQuestSourceResult questSource = WorldBuilder.QuestSource;
                string questSourceError = WorldBuilder.QuestSourceError;
                string platform = WorldBuilder.BuiltPlatform;
                string voltPackage = WorldBuilder.VoltPackagePathFor(bundlePath);
                var bundleInfo = new FileInfo(bundlePath);
                Debug.Log($"[PublishDiag] build done ms={publishTimer.ElapsedMilliseconds} " +
                          $"scene='{publishScene.path}' playing={Application.isPlaying} " +
                          $"bundle='{bundlePath}' bytes={bundleInfo.Length} platform={platform} " +
                          $"questSource={questSource != null} volt={File.Exists(voltPackage)}");
                _progressBar.value = 45;

                // ── 5. Upload Thumbnail (if captured) ──
                if (_pendingThumbnailBytes != null)
                {
                    _progressStatus.text = "Uploading thumbnail...";
                    _progressBar.value = 50;
                    await WorldApiClient.UploadImageAsync(
                        blueprintId, _pendingThumbnailBytes, _uploadCts.Token);
                    _pendingThumbnailBytes = null;
                }

                // ── 6. Upload the PC bundle ──
                float pcEnd = forStandalone ? 75f : 95f;
                if (forPc)
                {
                    byte[] bundleBytes = File.ReadAllBytes(bundlePath);
                    Debug.Log($"[PublishDiag] upload start bytes={bundleBytes.Length} " +
                              $"elapsedMs={publishTimer.ElapsedMilliseconds}");
                    _progressStatus.text = "Uploading PC / PCVR bundle...";
                    var progress = new Progress<float>(p =>
                    {
                        _progressBar.value = 55f + p * (pcEnd - 55f);
                        _progressStatus.text = $"Uploading PC / PCVR bundle... {(int)(p * 100)}%";
                    });
                    var response = await WorldApiClient.UploadBundleAsync(blueprintId, platform, bundleBytes, progress, _uploadCts.Token);
                    if (response == null || !response.success)
                        throw new Exception(response?.message ?? "Unknown upload error.");
                    Debug.Log($"[PublishDiag] upload done ms={publishTimer.ElapsedMilliseconds} " +
                              $"world={response.world?.id ?? "?"}");

                    if (WorldBuilder.AfterBundleUploaded != null)
                    {
                        _progressStatus.text = "Uploading scripts...";
                        await WorldBuilder.AfterBundleUploaded(blueprintId, platform, bundlePath, _uploadCts.Token);
                    }
                }

                // ── 7. Send what the Meta / Standalone VR version is built from; the server queues the conversion ──
                string questNote = null;
                if (forStandalone)
                {
                    if (questSource == null)
                    {
                        questNote = "The Meta / Standalone VR version wasn't sent: " + (questSourceError ?? "the package wasn't made") + ". Publish again to retry.";
                    }
                    else
                    {
                        try
                        {
                            var questProgress = new Progress<float>(p =>
                            {
                                _progressBar.value = pcEnd + p * (95f - pcEnd);
                                _progressStatus.text = $"Sending to ParelVR for Meta / Standalone VR... {(int)(p * 100)}%";
                            });
                            var queued = await ParelQuestApiClient.UploadSourceAsync(ParelQuestApiClient.Worlds, blueprintId,
                                questSource.PackagePath, File.Exists(voltPackage) ? voltPackage : null, ParelQuestSource.SdkVersion(),
                                questProgress, _uploadCts.Token);
                            if (queued?.questConversion != null) _questStatus = queued.questConversion;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception questError)
                        {
                            questNote = "The Meta / Standalone VR version wasn't sent: " + questError.Message + " Publish again to retry.";
                            Debug.LogWarning("[WorldsTab] " + questNote);
                        }
                        finally
                        {
                            TryDelete(questSource.PackagePath);
                        }
                    }
                }

                // The bundle cannot be turned back into a project, so the project itself is saved for Import World.
                _progressStatus.text = "Saving the project for Import World...";
                try
                {
                    WorldSourceStore.Info source = WorldSourceStore.Save(blueprintId, worldRecord.name, _currentDescriptor.gameObject.scene.path);
                    if (source != null)
                    {
                        _progressStatus.text = "Uploading the project...";
                        await WorldSourceStore.UploadAsync(source, _uploadCts.Token);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception sourceError)
                {
                    // The world itself is published; only Import World misses this version.
                    Debug.LogWarning($"[WorldsTab] The project was not saved for Import World: {sourceError.Message}");
                }
                Debug.Log($"[PublishDiag] done totalMs={publishTimer.ElapsedMilliseconds} " +
                          $"scene='{publishScene.path}' playing={Application.isPlaying}");
                _progressBar.value = 100;
                _progressStatus.text = "Done!";

                string message = forPc ? $"World '{worldRecord.name}' published for PC / PCVR." : $"World '{worldRecord.name}' saved.";
                if (forStandalone && questNote == null)
                    message += "\n\nParelVR is making the Meta / Standalone VR version now. It usually takes a few minutes; the Builder and the Content Manager show when it's ready.";
                if (questNote != null) message += "\n\n" + questNote;
                EditorUtility.DisplayDialog("Success", message, "OK");
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[WorldsTab] Publish canceled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorldsTab] Publish failed after " +
                               $"{publishTimer.ElapsedMilliseconds} ms " +
                               $"(scene='{publishScene.path}', playing={Application.isPlaying}): {ex.Message}");
                EditorUtility.DisplayDialog("Publish Error", ex.Message, "OK");
            }
            finally
            {
                _btnBuildPublish.SetEnabled(true);
                _progressContainer.style.display = DisplayStyle.None;
                if (_uploadCts != null)
                {
                    _uploadCts.Dispose();
                    _uploadCts = null;
                }
                RefreshState();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
