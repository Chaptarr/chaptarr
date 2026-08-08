using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorSyncMonitoredAcrossFormatsFieldFixture
    {
        [Test]
        public void use_db_fields_from_should_copy_sync_monitored_across_formats()
        {
            var local = new Author
            {
                SyncMonitoredAcrossFormats = true
            };

            var remote = new Author();

            remote.UseDbFieldsFrom(local);

            Assert.That(remote.SyncMonitoredAcrossFormats, Is.True);
        }

        [Test]
        public void apply_changes_should_copy_sync_monitored_across_formats_when_provided()
        {
            var existing = new Author
            {
                SyncMonitoredAcrossFormats = null
            };

            var changes = new Author
            {
                SyncMonitoredAcrossFormats = false
            };

            existing.ApplyChanges(changes);

            Assert.That(existing.SyncMonitoredAcrossFormats, Is.False);
        }

        [Test]
        public void apply_changes_should_not_wipe_sync_monitored_across_formats_when_not_provided()
        {
            var existing = new Author
            {
                SyncMonitoredAcrossFormats = true
            };

            var changes = new Author
            {
                SyncMonitoredAcrossFormats = null
            };

            existing.ApplyChanges(changes);

            Assert.That(existing.SyncMonitoredAcrossFormats, Is.True);
        }
    }
}
