using NUnit.Framework;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Notifications
{
    [TestFixture]
    public class NotificationFactoryFixture
    {
        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public void should_respect_audiobookshelf_release_import_toggle(bool enabled)
        {
            var definition = new NotificationDefinition
            {
                Implementation = "AudioBookShelf",
                OnReleaseImport = enabled
            };

            Assert.That(NotificationFactory.ShouldTriggerOnReleaseImport(definition), Is.EqualTo(enabled));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_respect_audiobookshelf_rename_toggle(bool enabled)
        {
            var definition = new NotificationDefinition
            {
                Implementation = "AudioBookShelf",
                OnRename = enabled
            };

            Assert.That(NotificationFactory.ShouldTriggerOnRename(definition), Is.EqualTo(enabled));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_respect_audiobookshelf_file_delete_toggle(bool enabled)
        {
            var definition = new NotificationDefinition
            {
                Implementation = "AudioBookShelf",
                OnBookFileDelete = enabled
            };

            Assert.That(NotificationFactory.ShouldTriggerOnBookFileDelete(definition), Is.EqualTo(enabled));
        }

        [Test]
        public void should_still_require_event_toggle_for_other_notifications()
        {
            var definition = new NotificationDefinition
            {
                Implementation = "Webhook",
                OnReleaseImport = false,
                OnRename = false,
                OnBookFileDelete = false
            };

            Assert.That(NotificationFactory.ShouldTriggerOnReleaseImport(definition), Is.False);
            Assert.That(NotificationFactory.ShouldTriggerOnRename(definition), Is.False);
            Assert.That(NotificationFactory.ShouldTriggerOnBookFileDelete(definition), Is.False);
        }
    }
}
