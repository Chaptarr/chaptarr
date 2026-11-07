using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Chaptarr.Api.V1.Localization;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Localization;

namespace Chaptarr.Core.Test.Localization
{
    [TestFixture]
    public class BookshelfLocalizationCompatibilityFixture
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Directory.CreateTempSubdirectory("chaptarr-localization-compat-").FullName;
            Directory.CreateDirectory(Path.Combine(_root, "Localization", "Core"));
            Directory.CreateDirectory(Path.Combine(_root, "Localization", "BookshelfCompatibility"));
            Write("Core", "en", "{\"Shared\":\"Native English\",\"NativeOnly\":\"New capability\"}");
            Write("Core", "fr", "{\"Shared\":\"Nouveau\"}");
            Write("BookshelfCompatibility", "en", "{\"Shared\":\"Legacy English\",\"OldKey\":\"Retained legacy field\"}");
            Write("BookshelfCompatibility", "fr", "{\"Shared\":\"Ancien\"}");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, true);
        }

        private void Write(string catalog, string locale, string json)
        {
            File.WriteAllText(Path.Combine(_root, "Localization", catalog, locale + ".json"), json);
        }

        [Test]
        public void packaged_predecessor_catalogs_should_match_the_pinned_source_manifest()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "Localization", "BookshelfCompatibility");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "source-manifest.json")));
            Assert.That(manifest.RootElement.GetProperty("commit").GetString(), Is.EqualTo("f512a17f866d1fa8e5d92ae792d044de1fb45535"));
            var count = 0;
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                Assert.That(Path.GetFileName(file.Name), Is.EqualTo(file.Name));
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file.Name)))).ToLowerInvariant();
                Assert.That(hash, Is.EqualTo(file.Value.GetString()), file.Name);
                count++;
            }

            Assert.That(count, Is.EqualTo(45));
        }

        [Test]
        public void language_changes_should_invalidate_native_and_legacy_caches()
        {
            var language = 1;
            var config = new Mock<IConfigService>();
            config.SetupGet(service => service.UILanguage).Returns(() => language);
            var folders = new Mock<IAppFolderInfo>();
            folders.SetupGet(info => info.StartUpFolder).Returns(_root);
            var service = new LocalizationService(config.Object, folders.Object, new CacheManager(), LogManager.GetCurrentClassLogger());
            Assert.That(service.GetBookshelfLocalizationDictionary()["Shared"], Is.EqualTo("Legacy English"));
            language = 2;
            service.HandleAsync(new ConfigSavedEvent());
            Assert.That(service.GetBookshelfLocalizationDictionary()["Shared"], Is.EqualTo("Ancien"));
            Assert.That(service.GetLocalizationDictionary()["Shared"], Is.EqualTo("Nouveau"));
        }

        [Test]
        public void legacy_regional_fallback_should_not_inherit_new_native_script_fallbacks()
        {
            Write("Core", "ui-locales", "{\"locales\":[{\"id\":10,\"tag\":\"zh-Hans\",\"file\":\"zh_CN\",\"fallback\":\"zh_Hans\"}]}");
            Write("Core", "zh_Hans", "{\"OldKey\":\"Native script fallback\"}");
            Write("Core", "zh_CN", "{\"Shared\":\"Native Chinese\"}");
            Write("BookshelfCompatibility", "zh_Hans", "{\"OldKey\":\"Different fallback\"}");
            Write("BookshelfCompatibility", "zh_CN", "{\"Shared\":\"Legacy Chinese\"}");
            var config = new Mock<IConfigService>();
            config.SetupGet(service => service.UILanguage).Returns(10);
            var folders = new Mock<IAppFolderInfo>();
            folders.SetupGet(info => info.StartUpFolder).Returns(_root);
            var service = new LocalizationService(config.Object, folders.Object, new CacheManager(), LogManager.GetCurrentClassLogger());

            Assert.That(service.GetBookshelfLocalizationDictionary()["OldKey"], Is.EqualTo("Retained legacy field"));
            Assert.That(service.GetBookshelfLocalizationDictionary()["Shared"], Is.EqualTo("Legacy Chinese"));
            Assert.That(service.GetLocalizationDictionary()["OldKey"], Is.EqualTo("Native script fallback"));
        }

        [Test]
        public void missing_legacy_base_catalog_should_fail_without_corrupting_native_translations()
        {
            File.Delete(Path.Combine(_root, "Localization", "BookshelfCompatibility", "en.json"));
            var config = new Mock<IConfigService>();
            config.SetupGet(service => service.UILanguage).Returns(1);
            var folders = new Mock<IAppFolderInfo>();
            folders.SetupGet(info => info.StartUpFolder).Returns(_root);
            var service = new LocalizationService(config.Object, folders.Object, new CacheManager(), LogManager.GetCurrentClassLogger());

            Assert.Throws<FileNotFoundException>(() => service.GetBookshelfLocalizationDictionary());
            Assert.That(service.GetLocalizationDictionary()["Shared"], Is.EqualTo("Native English"));
        }

        [TestCase(true, 1, "Legacy English")]
        [TestCase(true, 2, "Ancien")]
        [TestCase(false, 1, "Native English")]
        [TestCase(false, 2, "Nouveau")]
        public void compatibility_route_should_retain_source_keys_and_values_without_changing_native_cache(bool legacy, int language, string expected)
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(service => service.UILanguage).Returns(language);
            var folders = new Mock<IAppFolderInfo>();
            folders.SetupGet(info => info.StartUpFolder).Returns(_root);
            var service = new LocalizationService(config.Object, folders.Object, new CacheManager(), LogManager.GetCurrentClassLogger());
            var nativeBefore = service.GetLocalizationDictionary();
            var controller = new LocalizationController(service)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("hc", null, string.Empty, isBookshelfCompatibility: legacy);

            using var response = JsonDocument.Parse(controller.GetLocalizationDictionary());
            var strings = response.RootElement.GetProperty("Strings");
            Assert.That(strings.GetProperty("Shared").GetString(), Is.EqualTo(expected));
            Assert.That(strings.GetProperty("NativeOnly").GetString(), Is.EqualTo("New capability"));
            Assert.That(strings.TryGetProperty("OldKey", out var oldKey), Is.EqualTo(legacy));
            if (legacy)
            {
                Assert.That(oldKey.GetString(), Is.EqualTo("Retained legacy field"));
            }

            Assert.That(service.GetLocalizationDictionary(), Is.SameAs(nativeBefore));
            Assert.That(nativeBefore["Shared"], Is.EqualTo(language == 1 ? "Native English" : "Nouveau"));
            Assert.That(nativeBefore.ContainsKey("OldKey"), Is.False);
        }
    }
}
