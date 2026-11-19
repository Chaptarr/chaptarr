using System;
using System.Linq;
using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void disabling_sync_does_not_copy_a_legacy_future_choice_to_the_other_format(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var author = h.Author;
                author.AudiobookMonitorNewItems = NewItemMonitorTypes.All;
                author.EbookMonitorNewItems = NewItemMonitorTypes.All;
                h.Authors.UpdateAuthor(author);
                var resource = author.ToResource();
                resource.SyncMonitoredAcrossFormats = false;
                resource.MonitorNewItems = "none";
                h.Controller.UpdateAuthor(resource, mediaType: "ebook");
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.False);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scoped_legacy_monitor_request_aligns_synced_future_choices(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var author = h.Author;
                author.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                author.EbookMonitorNewItems = NewItemMonitorTypes.All;
                h.Authors.UpdateAuthor(author);
                var resource = author.ToResource();
                resource.MonitorNewItems = "none";
                h.Controller.UpdateAuthor(resource, mediaType: "ebook");
                Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                Assert.That(h.Author.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(h.Author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [TestCase(false, "audiobook")]
        [TestCase(true, "audiobook")]
        [TestCase(false, "ebook")]
        [TestCase(true, "ebook")]
        public void unrelated_author_save_keeps_sync_when_its_root_is_no_longer_configured(bool postgres, string mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                h.Scan();
                var author = h.Author;
                h.Roots.All().Remove(mediaType == "ebook" ? h.EbookRoot : h.AudioRoot);
                var resource = author.ToResource();
                resource.AudiobookTags = resource.EbookTags = new HashSet<int> { 19 };
                h.Commands.Clear();
                Assert.DoesNotThrow(() => h.Controller.UpdateAuthor(resource));
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.True);
                    Assert.That(h.Author.AudiobookRootFolderPath, Is.EqualTo(author.AudiobookRootFolderPath));
                    Assert.That(h.Author.EbookRootFolderPath, Is.EqualTo(author.EbookRootFolderPath));
                    Assert.That(h.Author.EbookTags, Is.EquivalentTo(new[] { 19 }));
                    Assert.That(h.Commands, Is.Empty);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void synced_author_missing_root_error_names_the_author_and_format(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request());
                var resource = h.Author.ToResource();
                resource.EbookRootFolderPath = "/no-longer-configured";
                var error = Assert.Throws<ValidationException>(() => h.Controller.UpdateAuthor(resource, mediaType: "ebook"));
                Assert.That(error.Errors.Single().ErrorMessage, Does.Contain(h.Author.Name).And.Contain("ebook root folder"));
                Assert.That(h.Author.EbookRootFolderPath, Is.EqualTo(EbookRootPath), "Rejected save makes no changes");
            });
        }

        [TestCase(false, MonitorTypes.None)]
        [TestCase(true, MonitorTypes.None)]
        [TestCase(false, MonitorTypes.All)]
        [TestCase(true, MonitorTypes.All)]
        public void native_post_author_omitted_monitor_uses_root_default(bool postgres, MonitorTypes mode)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var settings = h.EbookRoot.GetEbookSettings();
                settings.MonitorExistingMode = mode;
                h.EbookRoot.SetEbookSettings(settings);
                h.Controller.AddAuthor(new AuthorResource
                {
                    AuthorName = "Sync Options Writer", ForeignAuthorId = "hc:123456",
                    EbookRootFolderPath = EbookRootPath, EbookQualityProfileId = 4, EbookMetadataProfileId = 3,
                    EbookMonitored = true, AddOptions = new AddAuthorOptions { Monitor = null }
                }).GetAwaiter().GetResult();
                Assert.That(h.Author.AddOptions.EbookMonitor, Is.EqualTo(mode));
                Assert.That(h.Author.ToResource().InitialScanPaths, Is.EqualTo(h.Author.GetScanPaths("ebook")));
                h.Scan();
                Assert.That(h.Author.ToResource().InitialScanPaths, Is.Empty);
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Where(book => book.MediaType == BookMediaType.Ebook).All(book => book.IsMonitored() == (mode == MonitorTypes.All)), Is.True);
            });
        }
    }
}
