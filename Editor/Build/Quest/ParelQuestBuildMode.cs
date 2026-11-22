using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ParelVR.SDK.Core.Settings;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>
    /// How a world's or avatar's Meta / Standalone VR version is made, per project:
    /// Automatic: the SDK builds it on this PC in the background (headset-ready shaders, textures and settings) while
    /// the project stays on Windows. Manual: the creator switches the project to Android and publishes from there; the
    /// SDK builds exactly what the Android project has (no changes made), and the PC version is published from Windows.
    /// </summary>
    public static class ParelQuestBuildMode
    {
        private static string ManualKey => "ParelVR.SDK.QuestBuildManual:" + ParelQuestLocalBuild.ProjectRoot.Replace('\\', '/').ToLowerInvariant();

        public static bool Manual
        {
            get => EditorPrefs.GetBool(ManualKey, false);
            set => EditorPrefs.SetBool(ManualKey, value);
        }

        public static bool EditorOnAndroid => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;

        /// <summary>Manual, with the project switched to Android: this editor builds and uploads the Meta / Standalone VR version itself.</summary>
        public static bool BuildsHere => Manual && EditorOnAndroid;

        /// <summary>Automatic: the background build makes the Meta / Standalone VR version.</summary>
        public static bool Background => !Manual;

        private static readonly List<string> ModeChoices = new List<string>
        {
            "Automatic: built on this PC in the background (recommended)",
            "Manual: switch the project to Android and publish from there",
        };

        /// <summary>The line under the platform toggles in Manual mode, or null in Automatic.</summary>
        public static string ManualStatus(string kind, bool pc, bool standalone)
        {
            if (!Manual) return null;
            if (EditorOnAndroid)
                return standalone
                    ? $"Manual: this project is on Android, so publishing builds and uploads the Meta / Standalone VR version only, exactly as this project has it (the SDK changes nothing). " +
                      (pc ? $"The PC / PCVR version keeps its last upload: switch back to PC to update it." : string.Empty)
                    : $"Manual: this project is on Android. Turn on {ParelPlatformTargets.StandaloneLabel} to publish its version, or switch to PC for the PC / PCVR version.";
            return standalone
                ? $"Manual: publishing uploads the PC / PCVR version only. For Meta / Standalone VR, switch the project to Android and publish this {kind} again."
                : null;
        }

        /// <summary>The note after a Manual publish from PC with Meta / Standalone VR on.</summary>
        public static string ManualPcNote(string kind) =>
            $"Manual Meta / Standalone VR builds are on: switch the project to Android (Platforms > Switch Project to Android) and publish this {kind} again to upload its headset version.";

        /// <summary>
        /// The Automatic / Manual choice, the background build's CPU limit (Automatic) and the switch button (Manual),
        /// for under the platform toggles. <paramref name="changed"/> runs when any of them changes.
        /// </summary>
        public static VisualElement CreateOptions(Action changed)
        {
            var root = new VisualElement();
            root.style.marginTop = 2;
            root.style.marginBottom = 2;

            var mode = new DropdownField("Meta / Standalone VR build", ModeChoices, Manual ? 1 : 0);
            mode.tooltip = "Automatic: the SDK builds the headset version on this PC in the background and makes it headset-ready (shaders, textures, settings); the project stays on Windows.\n" +
                           "Manual: you switch the project to Android yourself and publish from there. What the Android project has is built and uploaded as it is.";
            var speed = new DropdownField("Background build CPU", new List<string>(ParelQuestMachine.SpeedLabels), (int)ParelQuestMachine.Speed);
            speed.tooltip = "How much of this PC's CPU the background build may use. It takes less whenever the rest of the PC is busy, and is held at Light on battery.";
            var switchButton = new Button();
            switchButton.style.alignSelf = Align.FlexStart;
            switchButton.style.marginTop = 2;

            void Refresh()
            {
                bool manual = Manual;
                speed.style.display = manual ? DisplayStyle.None : DisplayStyle.Flex;
                switchButton.style.display = manual ? DisplayStyle.Flex : DisplayStyle.None;
                switchButton.text = EditorOnAndroid ? "Switch Project to PC" : "Switch Project to Android";
            }

            mode.RegisterValueChangedCallback(e =>
            {
                Manual = ModeChoices.IndexOf(e.newValue) == 1;
                Refresh();
                changed?.Invoke();
            });
            speed.RegisterValueChangedCallback(e =>
            {
                int index = Array.IndexOf(ParelQuestMachine.SpeedLabels, e.newValue);
                if (index >= 0) ParelQuestMachine.Speed = (ParelQuestBuildSpeed)index;
                changed?.Invoke();
            });
            switchButton.clicked += () => SwitchProject(EditorOnAndroid ? BuildTarget.StandaloneWindows64 : BuildTarget.Android);
            // The button's label follows a switch made anywhere (Build Profiles too).
            switchButton.schedule.Execute(Refresh).Every(1000);

            root.Add(mode);
            root.Add(speed);
            root.Add(switchButton);
            Refresh();
            return root;
        }

        /// <summary>Asks, then switches the project's platform (Unity reimports its assets for it).</summary>
        public static void SwitchProject(BuildTarget target)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            string name = target == BuildTarget.Android ? "Android (Meta / Standalone VR)" : "Windows (PC / PCVR)";
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                EditorUtility.DisplayDialog("Switch Platform", target == BuildTarget.Android
                    ? ParelQuestLocalBuild.AndroidSupportMissingMessage
                    : "This Unity Editor has no Windows build support. Add it in Unity Hub > Installs > this version > Add Modules.", "OK");
                return;
            }
            if (EditorUtility.DisplayDialog("Switch Platform",
                    $"Switch this project to {name}? Unity reimports the project's assets for it, which can take a while the first time.", "Switch", "Cancel"))
                EditorUserBuildSettings.SwitchActiveBuildTargetAsync(group, target);
        }
    }
}
