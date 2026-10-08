using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using global::ParelVR.SDK.Core.Auth;
using global::ParelVR.SDK.Core.Settings;
using global::ParelVR.SDK.Core.Http;

namespace ParelVR.SDK.Core.ControlPanel
{
    /// <summary>
    /// ParelVR SDK's Control Panel, laid out like the VRChat SDK's: an Authentication tab (sign in /
    /// your account), the module tabs for the current project type (Builder, Content Manager -- found
    /// by reflection from IParelTab implementations), and a Settings tab. Module tabs need you to be
    /// signed in, exactly like VRChat's.
    /// </summary>
    public sealed class ParelControlPanel : EditorWindow
    {
        private static string UiRoot => ParelPackagePaths.Combine("Editor/UI/");
        private const string AuthenticationTab = "Authentication";
        private const string SettingsTab = "Settings";

        /// <summary>Raised after a tab is shown, with its name.</summary>
        public static event Action<string> TabShown;

        private sealed class TabEntry
        {
            public string Name;
            public IParelTab Module;
            public Action<VisualElement> Build;
            public Action Shown;
            public bool RequiresLogin;
            public VisualElement Container;
            public VisualElement Placeholder;
            public Label Label;
            public bool Built;
        }

        private VisualElement _root;
        private VisualElement _dashboard;
        private VisualElement _tabStrip;
        private VisualElement _tabContentHost;
        private Label _signedInLabel;
        private Button _signOutButton;

        private readonly List<TabEntry> _entries = new List<TabEntry>();
        private TabEntry _active;
        private bool _wasLoggedIn;
        private string _pendingTab;

        // ---- Authentication tab --------------------------------------------------------------
        private VisualElement _authHost;
        private VisualElement _loginScreen;
        private VisualElement _accountCard;
        private VisualElement _errorBanner;
        private Label _errorLabel;
        private Label _statusLabel;
        private Button _submitButton;
        private TextField _identifierField;
        private TextField _passwordField;
        private VisualElement _loginForm;
        private VisualElement _totpForm;
        private TextField _totpField;
        private Button _totpCancelButton;
        private string _pendingTotpToken;
        private Label _loginServerLabel;

        // ---- Settings tab ---------------------------------------------------------------------
        private enum SettingsSection { ProjectType, Preferences }
        private SettingsSection _settingsSection = SettingsSection.ProjectType;
        private VisualElement _settingsContent;
        private Label _navProjectType;
        private Label _navPreferences;
        private VisualElement _autoPortReportContainer;

        // =========================================================================================
        // Opening
        // =========================================================================================

        public static bool IsOpen => HasOpenInstances<ParelControlPanel>();

        /// <summary>The panel is one fixed, tall rectangle: it cannot be resized, maximized or docked.</summary>
        private static readonly Vector2 WindowSize = new Vector2(520, 700);

        public static void Open()
        {
            ParelControlPanel window = HasOpenInstances<ParelControlPanel>() ? GetWindow<ParelControlPanel>() : null;
            // A panel left open from before it had a fixed size is still an ordinary resizable window.
            if (window != null && (window.docked || window.maxSize != WindowSize))
            {
                window.Close();
                window = null;
            }
            if (window == null)
            {
                window = CreateInstance<ParelControlPanel>();
                window.titleContent = new GUIContent("ParelVR SDK");
                window.minSize = WindowSize;
                window.maxSize = WindowSize;
                window.ShowUtility();
            }
            window.Focus();
        }

        /// <summary>Opens the Control Panel on the named tab ("Builder", "Content Manager", ...).</summary>
        public static void ShowTab(string tabName)
        {
            Open();
            var window = GetWindow<ParelControlPanel>();
            window.SelectByName(tabName);
        }

        public void CreateGUI()
        {
            _root = rootVisualElement;
            _root.AddToClassList("bk-root");
            _root.AddToClassList(EditorGUIUtility.isProSkin ? "bk-dark" : "bk-light");

            var themeUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UiRoot + "Theme.uss");
            if (themeUss != null) _root.styleSheets.Add(themeUss);
            else Debug.LogWarning("[ParelVR SDK] Theme.uss not found at " + UiRoot);

            BuildShell();
            BuildEntries();
            _wasLoggedIn = ParelSession.IsLoggedIn;
            RefreshSession();
            SelectEntry(_wasLoggedIn ? FirstModuleOr(AuthenticationTab) : Find(AuthenticationTab));

            ParelSession.OnSessionChanged += OnSessionChanged;
            ParelModeManager.OnModeChanged += OnModeChanged;
        }

        private void OnDestroy()
        {
            ParelSession.OnSessionChanged -= OnSessionChanged;
            ParelModeManager.OnModeChanged -= OnModeChanged;
        }

        private void OnModeChanged()
        {
            string current = _active?.Name;
            BuildEntries();
            SelectByName(current == AuthenticationTab || current == SettingsTab ? current : null);
        }

        private Texture2D LoadLogo() => AssetDatabase.LoadAssetAtPath<Texture2D>(UiRoot + "logo.png");

        // =========================================================================================
        // Shell and tabs
        // =========================================================================================

        private void BuildShell()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiRoot + "Dashboard.uxml");
            _dashboard = uxml != null ? uxml.Instantiate() : new VisualElement();
            _dashboard.style.flexGrow = 1;
            _root.Add(_dashboard);

            var logoImage = _dashboard.Q<Image>("dash-logo");
            var logo = LoadLogo();
            if (logoImage != null && logo != null) logoImage.image = logo;

            _tabStrip = _dashboard.Q<VisualElement>("tabs") ?? new VisualElement();
            _tabContentHost = _dashboard.Q<VisualElement>("tab-content") ?? _dashboard;
            _signedInLabel = _dashboard.Q<Label>("signed-in-as");

            // Settings is a tab now, like VRChat's.
            var settingsBtn = _dashboard.Q<Button>("settings-btn");
            if (settingsBtn != null) settingsBtn.style.display = DisplayStyle.None;

            _signOutButton = _dashboard.Q<Button>("logout-btn");
            if (_signOutButton != null) _signOutButton.clicked += () => ParelAuth.Logout();
        }

        private void BuildEntries()
        {
            _tabStrip.Clear();
            _tabContentHost.Clear();
            _entries.Clear();
            _active = null;

            _entries.Add(new TabEntry { Name = AuthenticationTab, Build = BuildAuthenticationTab, Shown = RenderAuthentication });

            var modules = new List<IParelTab>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IParelTab>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                try
                {
                    var tab = (IParelTab)Activator.CreateInstance(type);
                    if (tab.RequiredMode != ParelModeManager.Current) continue;
                    modules.Add(tab);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ParelVR SDK] Failed to instantiate tab '{type.FullName}': {ex.Message}");
                }
            }
            modules.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));
            foreach (IParelTab module in modules)
            {
                IParelTab captured = module;
                _entries.Add(new TabEntry
                {
                    Name = module.TabName,
                    Module = module,
                    Build = container => captured.BuildUI(container),
                    Shown = () => captured.OnShown(),
                    RequiresLogin = true,
                });
            }

            _entries.Add(new TabEntry { Name = SettingsTab, Build = BuildSettingsTab, Shown = RenderSettingsSection });

            foreach (TabEntry entry in _entries)
            {
                var label = new Label(entry.Name);
                label.AddToClassList("bk-tab");
                label.pickingMode = PickingMode.Position;
                TabEntry captured = entry;
                label.RegisterCallback<ClickEvent>(_ => SelectEntry(captured));
                _tabStrip.Add(label);
                entry.Label = label;

                entry.Container = new VisualElement { style = { display = DisplayStyle.None, flexGrow = 1 } };
                _tabContentHost.Add(entry.Container);

                if (entry.RequiresLogin)
                {
                    entry.Placeholder = new VisualElement();
                    entry.Placeholder.AddToClassList("bk-empty-state");
                    var heading = new Label("Sign in first");
                    heading.AddToClassList("bk-heading");
                    entry.Placeholder.Add(heading);
                    var sub = new Label("Sign in with your ParelVR account on the Authentication tab to use the " + entry.Name + ".");
                    sub.AddToClassList("bk-subheading");
                    entry.Placeholder.Add(sub);
                    var go = new Button(() => SelectByName(AuthenticationTab)) { text = "Go to Authentication" };
                    go.AddToClassList("bk-btn");
                    go.AddToClassList("bk-btn-primary");
                    go.style.alignSelf = Align.FlexStart;
                    go.style.marginTop = 10;
                    entry.Placeholder.Add(go);
                    entry.Container.Add(entry.Placeholder);
                }
            }

            if (_pendingTab != null)
            {
                string pending = _pendingTab;
                _pendingTab = null;
                SelectByName(pending);
            }
        }

        private TabEntry Find(string name)
        {
            foreach (TabEntry entry in _entries)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            return null;
        }

        private TabEntry FirstModuleOr(string fallback)
        {
            foreach (TabEntry entry in _entries)
            {
                if (entry.Module != null) return entry;
            }
            return Find(fallback);
        }

        private void SelectByName(string name)
        {
            if (_entries.Count == 0)
            {
                _pendingTab = name;
                return;
            }
            TabEntry entry = string.IsNullOrEmpty(name) ? null : Find(name);
            SelectEntry(entry ?? (ParelSession.IsLoggedIn ? FirstModuleOr(AuthenticationTab) : Find(AuthenticationTab)));
        }

        private void SelectEntry(TabEntry entry)
        {
            if (entry == null) return;
            if (_active != null)
            {
                _active.Container.style.display = DisplayStyle.None;
                _active.Label.RemoveFromClassList("bk-tab-active");
            }

            _active = entry;
            entry.Container.style.display = DisplayStyle.Flex;
            entry.Label.AddToClassList("bk-tab-active");

            bool locked = entry.RequiresLogin && !ParelSession.IsLoggedIn;
            if (entry.Placeholder != null) entry.Placeholder.style.display = locked ? DisplayStyle.Flex : DisplayStyle.None;
            if (locked) return;

            try
            {
                if (!entry.Built)
                {
                    entry.Built = true;
                    var body = new VisualElement { style = { flexGrow = 1 } };
                    entry.Container.Add(body);
                    entry.Build(body);
                }
                entry.Shown?.Invoke();
                TabShown?.Invoke(entry.Name);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ParelVR SDK] The {entry.Name} tab failed: {ex}");
            }
        }

        private void OnSessionChanged()
        {
            bool loggedIn = ParelSession.IsLoggedIn;
            RefreshSession();
            if (loggedIn && !_wasLoggedIn) SelectEntry(FirstModuleOr(AuthenticationTab));
            else if (!loggedIn && _active != null && _active.RequiresLogin) SelectEntry(Find(AuthenticationTab));
            else if (_active != null) SelectEntry(_active);
            _wasLoggedIn = loggedIn;
        }

        private void RefreshSession()
        {
            bool loggedIn = ParelSession.IsLoggedIn;
            if (_signedInLabel != null)
            {
                _signedInLabel.text = loggedIn
                    ? (string.IsNullOrEmpty(ParelSession.DisplayName) ? ParelSession.Username : ParelSession.DisplayName)
                    : "Not signed in";
            }
            if (_signOutButton != null) _signOutButton.style.display = loggedIn ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (TabEntry entry in _entries)
            {
                if (entry.RequiresLogin) entry.Label.style.opacity = loggedIn ? 1f : 0.45f;
            }
            RenderAuthentication();
        }

        // =========================================================================================
        // Authentication tab
        // =========================================================================================

        private void BuildAuthenticationTab(VisualElement container)
        {
            _authHost = container;

            var banner = new VisualElement();
            banner.AddToClassList("bk-banner");
            var bannerLogo = new Image { image = LoadLogo(), scaleMode = ScaleMode.ScaleToFit };
            banner.Add(bannerLogo);
            var bannerTitle = new Label("ParelVR SDK");
            bannerTitle.AddToClassList("bk-banner-title");
            banner.Add(bannerTitle);
            var bannerSub = new Label("Avatars, worlds and Volt world scripts");
            bannerSub.AddToClassList("bk-banner-sub");
            banner.Add(bannerSub);
            container.Add(banner);

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiRoot + "LoginScreen.uxml");
            _loginScreen = uxml != null ? uxml.Instantiate() : new VisualElement();
            container.Add(_loginScreen);

            var logoImage = _loginScreen.Q<Image>("login-logo");
            var logo = LoadLogo();
            if (logoImage != null && logo != null) logoImage.image = logo;

            _errorBanner = _loginScreen.Q<VisualElement>("login-error");
            _errorLabel = _loginScreen.Q<Label>("login-error-text");
            _identifierField = _loginScreen.Q<TextField>("login-identifier");
            _passwordField = _loginScreen.Q<TextField>("login-password");
            _loginForm = _loginScreen.Q<VisualElement>("login-form");
            _totpForm = _loginScreen.Q<VisualElement>("totp-form");
            _totpField = _loginScreen.Q<TextField>("login-totp");
            _totpCancelButton = _loginScreen.Q<Button>("totp-cancel");
            _submitButton = _loginScreen.Q<Button>("login-submit");
            _statusLabel = _loginScreen.Q<Label>("login-status");

            if (_submitButton != null) _submitButton.clicked += OnLoginSubmit;
            if (_totpCancelButton != null) _totpCancelButton.clicked += CancelTotp;
            var signUp = _loginScreen.Q<Button>("login-signup");
            if (signUp != null) signUp.clicked += () => Application.OpenURL("https://parelvr.parelllc.com/Home");
            _loginServerLabel = _loginScreen.Q<Label>("login-server");
            if (_identifierField != null) _identifierField.RegisterCallback<KeyDownEvent>(OnLoginFieldKeyDown);
            if (_passwordField != null) _passwordField.RegisterCallback<KeyDownEvent>(OnLoginFieldKeyDown);
            if (_totpField != null) _totpField.RegisterCallback<KeyDownEvent>(OnLoginFieldKeyDown);

            _accountCard = new VisualElement();
            container.Add(_accountCard);
        }

        private void RenderAuthentication()
        {
            if (_authHost == null) return;
            bool loggedIn = ParelSession.IsLoggedIn;
            _loginScreen.style.display = loggedIn ? DisplayStyle.None : DisplayStyle.Flex;
            _accountCard.style.display = loggedIn ? DisplayStyle.Flex : DisplayStyle.None;
            if (!loggedIn)
            {
                if (_loginServerLabel != null) _loginServerLabel.text = "Server: " + ParelEnvironment.Current + " (" + ParelEnvironment.BaseUrl + ")";
                CancelTotp();
                return;
            }

            _accountCard.Clear();
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            _accountCard.Add(card);

            var heading = new Label("Account");
            heading.AddToClassList("bk-card-title");
            card.Add(heading);

            AddAccountRow(card, "Signed in as", string.IsNullOrEmpty(ParelSession.DisplayName) ? ParelSession.Username : ParelSession.DisplayName);
            if (!string.IsNullOrEmpty(ParelSession.Username)) AddAccountRow(card, "Username", ParelSession.Username);
            if (!string.IsNullOrEmpty(ParelSession.UserId)) AddAccountRow(card, "User ID", ParelSession.UserId);
            if (!string.IsNullOrEmpty(ParelSession.Rank)) AddAccountRow(card, "Rank", ParelSession.Rank);
            AddAccountRow(card, "Server", ParelEnvironment.Current + "  (" + ParelEnvironment.BaseUrl + ")");

            var buttons = new VisualElement();
            buttons.AddToClassList("bk-row");
            buttons.style.marginTop = 6;
            var signOut = new Button(() => ParelAuth.Logout()) { text = "Sign Out" };
            signOut.AddToClassList("bk-btn");
            signOut.style.flexGrow = 1;
            signOut.style.marginLeft = 0;
            buttons.Add(signOut);
            var website = new Button(() => Application.OpenURL("https://parelvr.parelllc.com/Home")) { text = "Open ParelVR Website" };
            website.AddToClassList("bk-btn");
            website.style.flexGrow = 1;
            website.style.marginRight = 0;
            buttons.Add(website);
            card.Add(buttons);
        }

        private static void AddAccountRow(VisualElement card, string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("bk-prop");
            var name = new Label(label);
            name.AddToClassList("bk-prop-label");
            row.Add(name);
            var text = new Label(value ?? string.Empty);
            text.selection.isSelectable = true;
            text.style.paddingTop = 5;
            text.style.whiteSpace = WhiteSpace.Normal;
            text.style.flexShrink = 1;
            row.Add(text);
            card.Add(row);
        }

        private void OnLoginFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                OnLoginSubmit();
        }

        private void CancelTotp()
        {
            _pendingTotpToken = null;
            if (_totpField != null) _totpField.value = string.Empty;
            if (_loginForm != null) _loginForm.style.display = DisplayStyle.Flex;
            if (_totpForm != null) _totpForm.style.display = DisplayStyle.None;
            HideError();
        }

        private async void OnLoginSubmit()
        {
            if (_submitButton == null) return;
            HideError();
            _submitButton.SetEnabled(false);
            _statusLabel.text = "Signing in…";
            try
            {
                if (!string.IsNullOrEmpty(_pendingTotpToken))
                {
                    await ParelAuth.LoginTotpAsync(_pendingTotpToken, _totpField?.value ?? "");
                    CancelTotp(); // Clears state on success
                }
                else
                {
                    var result = await ParelAuth.LoginAsync(_identifierField.value, _passwordField.value);
                    if (result.requiresTotp)
                    {
                        _pendingTotpToken = result.pendingToken;
                        if (_loginForm != null) _loginForm.style.display = DisplayStyle.None;
                        if (_totpForm != null) _totpForm.style.display = DisplayStyle.Flex;
                        _statusLabel.text = string.Empty;
                        _submitButton.SetEnabled(true);
                        return;
                    }
                }

                if (_passwordField != null) _passwordField.value = string.Empty;
            }
            catch (ParelApiException ex)
            {
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                ShowError("Unexpected error: " + ex.Message);
                Debug.LogException(ex);
            }
            finally
            {
                if (_statusLabel != null) _statusLabel.text = string.Empty;
                _submitButton.SetEnabled(true);
            }
        }

        private void ShowError(string message)
        {
            if (_errorLabel == null) return;
            _errorLabel.text = message;
            _errorBanner.AddToClassList("bk-visible");
        }

        private void HideError()
        {
            if (_errorBanner == null) return;
            _errorBanner.RemoveFromClassList("bk-visible");
        }

        // =========================================================================================
        // Settings tab
        // =========================================================================================

        private void BuildSettingsTab(VisualElement container)
        {
            // One column of titled cards, every setting visible at once.
            _settingsContent = new VisualElement();
            _settingsContent.AddToClassList("bk-settings-column");
            container.Add(_settingsContent);
        }

        private void SelectSettingsSection(SettingsSection section)
        {
            _settingsSection = section;
            RenderSettingsSection();
        }

        private void RenderSettingsSection()
        {
            if (_settingsContent == null) return;
            _settingsContent.Clear();
            BuildProjectTypeContent();
            BuildPreferencesContent();
        }

        private void BuildPreferencesContent()
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            _settingsContent.Add(card);

            var heading = new Label("Preferences");
            heading.AddToClassList("bk-card-title");
            card.Add(heading);

            var toggle = new Toggle { value = ParelPreferences.AutoPortContentEnabled };
            toggle.RegisterValueChangedCallback(evt => ParelPreferences.SetAutoPortContentEnabled(evt.newValue));
            VisualElement portValue = Setting(card, "Auto Port Content", toggle,
                "Automatically detects and fixes common setup issues in the open scene.");

            var scanBtn = new Button(RunAutoPortScan) { text = "Scan Now" };
            scanBtn.AddToClassList("bk-btn");
            scanBtn.style.alignSelf = Align.FlexStart;
            scanBtn.style.marginLeft = 0;
            scanBtn.style.marginTop = 4;
            portValue.Add(scanBtn);

            _autoPortReportContainer = new VisualElement();
            portValue.Add(_autoPortReportContainer);

            var addToggle = new Toggle { value = ParelPreferences.AutoAddReferencedScripts };
            addToggle.RegisterValueChangedCallback(evt => ParelPreferences.SetAutoAddReferencedScripts(evt.newValue));
            Setting(card, "Automatically Add Referenced Scripts", addToggle,
                "When you add a script to an object and it needs other scripts to work, the SDK adds those for you. " +
                "For example, adding a Volt Pickup also adds a Rigidbody, a collider and a Volt Object Sync, and a script that " +
                "handles OnInteract gets a collider. Nothing already on the object is changed, and each addition can be undone.");

            var envDropdown = new EnumField(ParelEnvironment.Current);
            envDropdown.RegisterValueChangedCallback(evt =>
            {
                ParelPreferences.SetEnvironment((ParelEnvironmentType)evt.newValue);
                if (ParelSession.IsLoggedIn) ParelAuth.Logout();
            });
            Setting(card, "API Environment", envDropdown,
                "Connects the SDK to different backend instances (Development, Staging, Production). Changing it signs you out.");
        }

        /// <summary>One setting: its name on the left; its control and what it does on the right.</summary>
        private static VisualElement Setting(VisualElement card, string name, VisualElement control, string description)
        {
            var row = new VisualElement();
            row.AddToClassList("bk-prop");
            row.style.marginBottom = 10;
            var label = new Label(name);
            label.AddToClassList("bk-prop-label");
            row.Add(label);

            var value = new VisualElement();
            value.AddToClassList("bk-prop-value");
            value.style.flexDirection = FlexDirection.Column;
            control.style.marginLeft = 0;
            control.style.alignSelf = control is Toggle ? Align.FlexStart : Align.Stretch;
            value.Add(control);
            var hint = new Label(description);
            hint.AddToClassList("bk-hint");
            hint.style.marginTop = 2;
            value.Add(hint);
            row.Add(value);
            card.Add(row);
            return value;
        }

        private async void RunAutoPortScan()
        {
            _autoPortReportContainer.Clear();
            var status = new Label("Scanning scene...");
            status.AddToClassList("bk-hint");
            _autoPortReportContainer.Add(status);

            var mergedReport = new global::ParelVR.SDK.Core.Validation.ValidationReport();

            foreach (var type in TypeCache.GetTypesDerivedFrom<global::ParelVR.SDK.Core.Validation.IValidator>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                try
                {
                    var validator = (global::ParelVR.SDK.Core.Validation.IValidator)Activator.CreateInstance(type);
                    if (validator.RequiredMode != ParelModeManager.Current) continue;

                    var report = await validator.ValidateAsync();
                    mergedReport.Merge(report);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ParelVR SDK] Validator {type.Name} failed: {ex}");
                }
            }

            _autoPortReportContainer.Clear();

            if (mergedReport.Issues.Count == 0)
            {
                var ok = new Label("No issues found! Your scene is ready to build.");
                ok.AddToClassList("bk-label");
                ok.style.color = new Color(0.2f, 0.8f, 0.2f);
                _autoPortReportContainer.Add(ok);
                return;
            }

            foreach (var issue in mergedReport.Issues)
            {
                var row = new VisualElement();
                row.AddToClassList("bk-row");
                row.style.marginTop = 4;

                var msg = new Label($"[{issue.Level}] {issue.Message}");
                msg.AddToClassList("bk-hint");
                msg.style.whiteSpace = WhiteSpace.Normal;
                msg.style.flexShrink = 1;

                if (issue.Level == global::ParelVR.SDK.Core.Validation.ValidationIssueLevel.Error)
                    msg.style.color = new Color(0.9f, 0.3f, 0.3f);
                else if (issue.Level == global::ParelVR.SDK.Core.Validation.ValidationIssueLevel.Warning)
                    msg.style.color = new Color(0.9f, 0.7f, 0.2f);

                row.Add(msg);

                if (issue.HasAutoFix)
                {
                    var fixBtn = new Button(() => { issue.AutoFix(); RunAutoPortScan(); }) { text = "Auto Fix" };
                    fixBtn.AddToClassList("bk-btn");
                    fixBtn.style.paddingLeft = fixBtn.style.paddingRight = 6;
                    row.Add(fixBtn);
                }

                _autoPortReportContainer.Add(row);
            }
        }

        private void BuildProjectTypeContent()
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            _settingsContent.Add(card);

            var heading = new Label("Project Type");
            heading.AddToClassList("bk-card-title");
            card.Add(heading);

            var sub = new Label("Choose what this Unity project is being developed for. ParelVR SDK only enables the tools for the selected type.");
            sub.AddToClassList("bk-hint");
            sub.style.marginTop = 0;
            sub.style.marginBottom = 10;
            card.Add(sub);

            card.Add(BuildModeOption(
                ParelProjectType.World,
                "World Project",
                "SDK will enable World development tools and disable Avatar development systems."));

            card.Add(BuildModeOption(
                ParelProjectType.Avatar,
                "Avatar Project",
                "SDK will enable Avatar development tools and disable World development systems."));
        }

        private VisualElement BuildModeOption(ParelProjectType mode, string title, string description)
        {
            var option = new VisualElement();
            option.AddToClassList("bk-mode-option");
            option.pickingMode = PickingMode.Position;
            bool isCurrent = mode == ParelModeManager.Current;
            if (isCurrent) option.AddToClassList("bk-mode-option-active");

            var titleRow = new VisualElement();
            titleRow.AddToClassList("bk-row");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("bk-mode-option-title");
            titleRow.Add(titleLabel);

            if (isCurrent)
            {
                var pill = new Label("CURRENT");
                pill.AddToClassList("bk-pill");
                pill.AddToClassList("bk-pill-accent");
                pill.style.marginLeft = 8;
                titleRow.Add(pill);
            }

            option.Add(titleRow);

            var descLabel = new Label(description);
            descLabel.AddToClassList("bk-mode-option-desc");
            option.Add(descLabel);

            option.RegisterCallback<ClickEvent>(_ => ParelModeManager.SetMode(mode));
            return option;
        }
    }
}
