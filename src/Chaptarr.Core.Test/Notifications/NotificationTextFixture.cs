using System;
using System.Collections.Generic;
using FluentValidation.Results;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Email;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class NotificationTextFixture
    {
        [Test]
        public void should_substitute_dynamic_values_once_without_translating_or_reinterpreting_them()
        {
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetLocalizedString("notification.body.test"))
                .Returns("{book} : {path} ({count})");
            var text = new NotificationText(localization.Object);

            Assert.That(text.Get("notification.body.test", "unused", new Dictionary<string, object>
            {
                ["book"] = "كتاب {path}",
                ["path"] = "/books/{count}/<name>.epub",
                ["count"] = 3
            }), Is.EqualTo("كتاب {path} : /books/{count}/<name>.epub (3)"));
            localization.Verify(service => service.GetLocalizedString(It.IsAny<string>()), Times.Once);
        }

        [Test]
        public void should_keep_an_english_fallback_when_the_localizer_has_no_key()
        {
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetLocalizedString("missing.id")).Returns("missing.id");
            Assert.That(new NotificationText(localization.Object).Get("missing.id", "Chaptarr: {value}",
                new Dictionary<string, object> { ["value"] = "Book {value}" }), Is.EqualTo("Chaptarr: Book {value}"));
            Assert.That(new NotificationText(null).Get("missing.id", "Fallback"), Is.EqualTo("Fallback"));
        }

        [Test]
        public void should_configure_each_notification_instance_through_the_factory_and_resolve_the_current_locale()
        {
            var heading = "كتاب مضاف";
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetLocalizedString("notification.heading.bookAdded"))
                .Returns(() => heading);
            var provider = new TestNotification();
            var container = new Mock<IServiceProvider>();
            container.Setup(service => service.GetService(typeof(TestNotification))).Returns(provider);
            var factory = new NotificationFactory(
                Mock.Of<INotificationStatusService>(), Mock.Of<INotificationRepository>(),
                new INotification[] { provider }, container.Object, Mock.Of<IEventAggregator>(),
                localization.Object, LogManager.GetCurrentClassLogger());

            var instance = (TestNotification)factory.GetInstance(new NotificationDefinition
            {
                Implementation = nameof(TestNotification), Settings = new EmailSettings()
            });
            Assert.That(instance.Heading, Is.EqualTo("Chaptarr - كتاب مضاف"));
            heading = "Livre ajouté";
            Assert.That(instance.Heading, Is.EqualTo("Chaptarr - Livre ajouté"));
        }

        [Test]
        public void should_localize_delete_copy_without_changing_the_book_or_author_name()
        {
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetLocalizedString("notification.body.bookRemovedWithFiles"))
                .Returns("الكتاب وملفاته حُذفت");
            var message = new BookDeleteMessage(new NzbDrone.Core.Books.Book { Title = "Book {source} / كتاب" }, true,
                new NotificationText(localization.Object));
            Assert.That(message.Message, Is.EqualTo("Book {source} / كتاب - الكتاب وملفاته حُذفت"));
        }

        [Test]
        public void should_refresh_human_sensor_labels_after_ui_language_changes_without_changing_protocol_settings()
        {
            var locale = "ar";
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetCurrentLocale()).Returns(() => locale);
            var text = new NotificationText(localization.Object);
            var first = text.LocaleFingerprint("settings-sha256");
            locale = "de";
            Assert.That(first, Is.EqualTo("settings-sha256|uiLocale:ar"));
            Assert.That(text.LocaleFingerprint("settings-sha256"), Is.EqualTo("settings-sha256|uiLocale:de"));
        }

        public class TestNotification : NotificationBase<EmailSettings>
        {
            public override string Name => "Test";
            public override string Link => string.Empty;
            public string Heading => BOOK_ADDED_TITLE_BRANDED;
            public override ValidationResult Test() => new ValidationResult();
        }
    }
}
