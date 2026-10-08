using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Build;
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
        private TextField _worldName;
        private TextField _worldDesc;
        private IntegerField _worldRecCap;
        private IntegerField _worldMaxCap;
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

        private VisualElement _validationErrorsContainer;
        private ScrollView _validationErrorsList;

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
            _worldName = tree.Q<TextField>("world-name");
            _worldDesc = tree.Q<TextField>("world-desc");
            
            _worldRecCap = tree.Q<IntegerField>("world-rec-cap");
            _worldMaxCap = tree.Q<IntegerField>("world-max-cap");
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

            // Validation
            _validationErrorsContainer = tree.Q<VisualElement>("validation-errors-container");
            _validationErrorsList = tree.Q<ScrollView>("validation-errors-list");

            // Progress state
            _progressContainer = tree.Q<VisualElement>("progress-container");
            _progressStatus = tree.Q<Label>("progress-status");
            _progressBar = tree.Q<ProgressBar>("progress-bar");

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

            ParelSession.OnSessionChanged += RefreshState;
            EditorApplication.hierarchyChanged += RefreshState;
            container.RegisterCallback<DetachFromPanelEvent>(evt =>
            {
                ParelSession.OnSessionChanged -= RefreshState;
                EditorApplication.hierarchyChanged -= RefreshState;
            });

            RefreshState();
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

            if (!ParelSession.IsLoggedIn)
            {
                _setupContainer.style.display = DisplayStyle.None;
                _publishContainer.style.display = DisplayStyle.None;
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

                if (string.IsNullOrEmpty(_currentDescriptor.blueprintId))
                {
                    _publishTitle.text = "Publish New World";
                    _blueprintIdLabel.text = "Blueprint ID: None (will be assigned on publish)";
                    _btnBuildPublish.text = "Build & Publish New World";
                    
                    if (isNewDescriptor)
                    {
                        _worldName.value = "";
                        _worldDesc.value = "";
                        _worldRecCap.value = 16;
                        _worldMaxCap.value = 32;
                        _worldAllowDebug.value = false;
                        _currentTags.Clear();
                        RenderTags();
                        ClearWarnings();
                        _pendingThumbnailBytes = null;
                        if (_thumbnailPreview != null) _thumbnailPreview.image = null;
                    }
                }
                else
                {
                    _publishTitle.text = "Update Existing World";
                    _blueprintIdLabel.text = $"Blueprint ID: {_currentDescriptor.blueprintId}";
                    _btnBuildPublish.text = "Build & Update World";
                    
                    if (isNewDescriptor)
                    {
                        _worldName.value = "";
                        _worldDesc.value = "";
                        _worldRecCap.value = 16;
                        _worldMaxCap.value = 32;
                        _worldAllowDebug.value = false;
                        _currentTags.Clear();
                        RenderTags();
                        ClearWarnings();
                        _pendingThumbnailBytes = null;
                        if (_thumbnailPreview != null) _thumbnailPreview.image = null;
                        _ = FetchExistingWorldDataAsync(_currentDescriptor.blueprintId);
                    }
                }
            }
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
                pill.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
                pill.style.borderTopLeftRadius = 4;
                pill.style.borderTopRightRadius = 4;
                pill.style.borderBottomLeftRadius = 4;
                pill.style.borderBottomRightRadius = 4;
                pill.style.paddingLeft = 8;
                pill.style.paddingRight = 4;
                pill.style.paddingTop = 2;
                pill.style.paddingBottom = 2;
                pill.style.marginRight = 4;
                pill.style.marginBottom = 4;

                var label = new Label(tag);
                label.style.color = Color.white;
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
                    
                    _worldRecCap.value = world.recommendedCapacity > 0 ? world.recommendedCapacity : 16;
                    _worldMaxCap.value = world.maximumCapacity > 0 ? world.maximumCapacity : 32;
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

            var validator = new WorldValidator();
            var report = await validator.ValidateAsync();

            if (report.CanBuild)
                return true;

            _validationErrorsContainer.style.display = DisplayStyle.Flex;
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

            return false;
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

        private async Task BuildAndPublishAsync()
        {
            if (_currentDescriptor == null) return;

            if (string.IsNullOrWhiteSpace(_worldName.value))
            {
                EditorUtility.DisplayDialog("Error", "World Name is required.", "OK");
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
                    recommendedCapacity = _worldRecCap.value,
                    maximumCapacity = _worldMaxCap.value,
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
                await Task.Yield();

                string bundlePath = WorldBuilder.BuildWorldBundle();
                if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
                    throw new Exception("AssetBundle build failed. Check the Unity Console.");

                byte[] bundleBytes = File.ReadAllBytes(bundlePath);
                _progressBar.value = 50;

                // ── 5. Upload Thumbnail (if captured) ──
                if (_pendingThumbnailBytes != null)
                {
                    _progressStatus.text = "Uploading thumbnail...";
                    _progressBar.value = 55;
                    await WorldApiClient.UploadImageAsync(
                        blueprintId, _pendingThumbnailBytes, _uploadCts.Token);
                    _pendingThumbnailBytes = null;
                }

                // ── 6. Upload Bundle ──
                _progressStatus.text = "Uploading bundle...";
                var progress = new Progress<float>(p =>
                {
                    float overall = 60f + (p * 40f); // 60% to 100%
                    _progressBar.value = overall;
                    _progressStatus.text = $"Uploading bundle... {(int)(p * 100)}%";
                });

                var response = await WorldApiClient.UploadBundleAsync(
                    blueprintId, "windows", bundleBytes, progress, _uploadCts.Token);

                if (response != null && response.success)
                {
                    if (WorldBuilder.AfterBundleUploaded != null)
                    {
                        _progressStatus.text = "Uploading scripts...";
                        await WorldBuilder.AfterBundleUploaded(blueprintId, "windows", bundlePath, _uploadCts.Token);
                    }
                    _progressBar.value = 100;
                    _progressStatus.text = "Done!";
                    EditorUtility.DisplayDialog("Success",
                        $"World '{worldRecord.name}' published successfully!", "OK");
                }
                else
                {
                    throw new Exception(response?.message ?? "Unknown upload error.");
                }
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[WorldsTab] Publish canceled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WorldsTab] Publish failed: {ex.Message}");
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
    }
}
