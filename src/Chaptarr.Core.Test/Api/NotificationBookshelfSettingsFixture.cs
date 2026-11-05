using System.Collections.Generic;
using System.Text.Json;
using Chaptarr.Api.V1.Notifications;
using Chaptarr.Http.ClientSchema;
using Chaptarr.Http.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Plex.Server;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class NotificationBookshelfSettingsFixture
    {
        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void should_save_migrated_master_switch_from_native_ui_and_legacy_api(bool migrated, bool legacy, bool expected)
        {
            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.BookshelfNGDatabaseMigrated).Returns(migrated);
            config.SetupGet(c => c.BookshelfNGMigrationProfile).Returns(new BookshelfNGCompatibilityProfile { ProviderDialect = "hc" });
            var controller = new TestNotificationController(config.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            if (legacy)
            {
                controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "", true);
            }

            var settings = new PlexServerSettings { UpdateLibrary = true };
            controller.Apply(new NotificationResource
            {
                Fields = new List<Field> { new() { Name = "updateLibrary", Value = JsonSerializer.SerializeToElement(false) } }
            }, new NotificationDefinition { Settings = settings });

            Assert.That(settings.UpdateLibrary, Is.EqualTo(expected));
        }

        private sealed class TestNotificationController : NotificationController
        {
            public TestNotificationController(IConfigService config) : base(null, config)
            {
            }

            public void Apply(NotificationResource resource, NotificationDefinition definition) => ApplyResourceSettings(resource, definition);
        }
    }
}
