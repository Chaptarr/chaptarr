using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Diagnostics
{
    [TestFixture]
    public class DiagnosticsPluginPathResolverFixture
    {
        private string _root;
        private string _appDataFolder;
        private string _startupFolder;
        private List<string> _warnings;

        [SetUp]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "chaptarr-diagnostics-paths-" + Guid.NewGuid().ToString("N"));
            _appDataFolder = Path.Combine(_root, "appdata");
            _startupFolder = Path.Combine(_root, "app");
            Directory.CreateDirectory(_appDataFolder);
            Directory.CreateDirectory(_startupFolder);
            _warnings = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public void should_remain_disabled_by_default_even_when_the_current_plugin_is_installed()
        {
            CreateCurrentPlugin();

            var paths = Resolve(currentSetting: null, legacySetting: null);

            Assert.That(paths, Is.Empty);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void should_load_current_plugin_when_explicitly_enabled()
        {
            var pluginPath = CreateCurrentPlugin();

            var paths = Resolve(currentSetting: "true", legacySetting: null);

            Assert.That(paths, Is.EqualTo(new[] { pluginPath }));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void should_load_current_plugin_for_a_legacy_opt_in_only_when_legacy_marker_exists()
        {
            var pluginPath = CreateCurrentPlugin();
            CreateLegacyPluginMarker();

            var paths = Resolve(currentSetting: null, legacySetting: "true");

            Assert.That(paths, Is.EqualTo(new[] { pluginPath }));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void should_not_inherit_a_legacy_opt_in_without_its_plugin_marker()
        {
            CreateCurrentPlugin();

            var paths = Resolve(currentSetting: null, legacySetting: "true");

            Assert.That(paths, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
            Assert.That(_warnings[0], Does.Contain("no legacy diagnostics plugin"));
        }

        [Test]
        public void should_let_an_explicit_current_disable_override_the_legacy_opt_in()
        {
            CreateCurrentPlugin();
            CreateLegacyPluginMarker();

            var paths = Resolve(currentSetting: "false", legacySetting: "true");

            Assert.That(paths, Is.Empty);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void should_warn_and_send_nothing_when_enabled_plugin_is_missing()
        {
            var paths = Resolve(currentSetting: "true", legacySetting: null);

            Assert.That(paths, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
            Assert.That(_warnings[0], Does.Contain("Chaptarr.Diagnostics.dll is not installed"));
        }

        private IReadOnlyList<string> Resolve(string currentSetting, string legacySetting)
        {
            return DiagnosticsPluginPathResolver.Resolve(
                _appDataFolder,
                _startupFolder,
                currentSetting,
                legacySetting,
                message => _warnings.Add(message));
        }

        private string CreateCurrentPlugin()
        {
            var path = Path.Combine(_startupFolder, "Chaptarr.Diagnostics.dll");
            File.WriteAllText(path, "test marker");
            return path;
        }

        private void CreateLegacyPluginMarker()
        {
            var path = Path.Combine(_appDataFolder, "plugins", "Bookshelf.Diagnostics.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "legacy test marker");
        }
    }
}
