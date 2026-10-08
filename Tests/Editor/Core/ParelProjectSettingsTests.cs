using System;
using System.IO;
using NUnit.Framework;
using ParelVR.SDK.Core.Settings;
using UnityEngine;

namespace ParelVR.SDK.Tests
{
    public class ParelProjectSettingsTests
    {
        private static string SettingsFilePath =>
            Path.Combine(Application.dataPath, "..", "Library", "ParelVR", "project-settings.json");

        private ParelProjectType _originalType;
        private string _originalFileContents;
        private bool _originalFileExisted;

        [SetUp]
        public void SetUp()
        {
            _originalType = ParelProjectSettings.ProjectType;
            _originalFileExisted = File.Exists(SettingsFilePath);
            if (_originalFileExisted) _originalFileContents = File.ReadAllText(SettingsFilePath);
        }

        [TearDown]
        public void TearDown()
        {
            if (_originalFileExisted) File.WriteAllText(SettingsFilePath, _originalFileContents);
            else if (File.Exists(SettingsFilePath)) File.Delete(SettingsFilePath);
            ParelProjectSettings.ReloadForTests();
            Assert.AreEqual(_originalType, ParelProjectSettings.ProjectType);
        }

        [Test]
        public void Save_UpdatesCurrentProjectType()
        {
            ParelProjectSettings.Save(ParelProjectType.Avatar);
            Assert.AreEqual(ParelProjectType.Avatar, ParelProjectSettings.ProjectType);

            ParelProjectSettings.Save(ParelProjectType.World);
            Assert.AreEqual(ParelProjectType.World, ParelProjectSettings.ProjectType);
        }

        [Test]
        public void Save_PersistsAcrossReload()
        {
            ParelProjectSettings.Save(ParelProjectType.Avatar);

            ParelProjectSettings.ReloadForTests();

            Assert.AreEqual(ParelProjectType.Avatar, ParelProjectSettings.ProjectType);
        }

        [Test]
        public void Reload_MissingFile_DefaultsToWorld()
        {
            ParelProjectSettings.Save(ParelProjectType.Avatar);
            if (File.Exists(SettingsFilePath)) File.Delete(SettingsFilePath);

            ParelProjectSettings.ReloadForTests();

            Assert.AreEqual(ParelProjectType.World, ParelProjectSettings.ProjectType);
        }

        [Test]
        public void Reload_CorruptFile_DefaultsToWorldWithoutThrowing()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
            File.WriteAllText(SettingsFilePath, "{ not valid json ][");

            Assert.DoesNotThrow(() => ParelProjectSettings.ReloadForTests());
            Assert.AreEqual(ParelProjectType.World, ParelProjectSettings.ProjectType);
        }

        [Test]
        public void Reload_UnrecognizedProjectTypeValue_DefaultsToWorld()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
            File.WriteAllText(SettingsFilePath, "{\"projectType\":\"Vehicle\"}");

            ParelProjectSettings.ReloadForTests();

            Assert.AreEqual(ParelProjectType.World, ParelProjectSettings.ProjectType);
        }
    }
}
