using System.Collections.Generic;
using NUnit.Framework;
using ParelVR.SDK.Core.Settings;
using ParelVR.SDK.Core.ControlPanel;

namespace ParelVR.SDK.Tests
{
    internal sealed class RecordingModeHandler : IParelModeAware
    {
        public static readonly List<string> Calls = new List<string>();

        public void OnModeActivated(ParelProjectType mode) => Calls.Add($"Activated:{mode}");
        public void OnModeDeactivated(ParelProjectType mode) => Calls.Add($"Deactivated:{mode}");
    }

    public class ParelModeManagerTests
    {
        private ParelProjectType _originalMode;

        [SetUp]
        public void SetUp()
        {
            _originalMode = ParelModeManager.Current;
            RecordingModeHandler.Calls.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ParelModeManager.SetMode(_originalMode);
        }

        [Test]
        public void SetMode_SameAsCurrent_IsNoOpAndDoesNotFireEvent()
        {
            bool fired = false;
            System.Action handler = () => fired = true;
            ParelModeManager.OnModeChanged += handler;
            try
            {
                ParelModeManager.SetMode(ParelModeManager.Current);
                Assert.IsFalse(fired);
            }
            finally
            {
                ParelModeManager.OnModeChanged -= handler;
            }
        }

        [Test]
        public void SetMode_DifferentMode_UpdatesCurrentAndPersists()
        {
            var target = _originalMode == ParelProjectType.World
                ? ParelProjectType.Avatar
                : ParelProjectType.World;

            ParelModeManager.SetMode(target);

            Assert.AreEqual(target, ParelModeManager.Current);
            Assert.AreEqual(target, ParelProjectSettings.ProjectType);
        }

        [Test]
        public void SetMode_DifferentMode_FiresOnModeChangedExactlyOnce()
        {
            var target = _originalMode == ParelProjectType.World
                ? ParelProjectType.Avatar
                : ParelProjectType.World;

            int fireCount = 0;
            System.Action handler = () => fireCount++;
            ParelModeManager.OnModeChanged += handler;
            try
            {
                ParelModeManager.SetMode(target);
                Assert.AreEqual(1, fireCount);
            }
            finally
            {
                ParelModeManager.OnModeChanged -= handler;
            }
        }

        [Test]
        public void SetMode_DeactivatesOldModeBeforeActivatingNewMode()
        {
            var oldMode = _originalMode;
            var newMode = oldMode == ParelProjectType.World
                ? ParelProjectType.Avatar
                : ParelProjectType.World;

            ParelModeManager.SetMode(newMode);

            int deactivateIndex = RecordingModeHandler.Calls.IndexOf($"Deactivated:{oldMode}");
            int activateIndex = RecordingModeHandler.Calls.IndexOf($"Activated:{newMode}");

            Assert.GreaterOrEqual(deactivateIndex, 0, "Expected OnModeDeactivated for the old mode to have been recorded.");
            Assert.GreaterOrEqual(activateIndex, 0, "Expected OnModeActivated for the new mode to have been recorded.");
            Assert.Less(deactivateIndex, activateIndex, "OnModeDeactivated(old) should run before OnModeActivated(new).");
        }
    }
}
