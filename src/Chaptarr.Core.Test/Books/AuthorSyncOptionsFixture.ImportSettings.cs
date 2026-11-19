using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        private static AuthorImportResource MinimalImport(string media = "both") => new()
        {
            ForeignAuthorId = "hc:123456",
            MediaType = media
        };

        private static object[] SavedSettings(Author author) => new object[]
        {
            author.Name, author.AudiobookRootFolderPath, author.EbookRootFolderPath,
            author.AudiobookQualityProfileId, author.EbookQualityProfileId,
            author.AudiobookMetadataProfileId, author.EbookMetadataProfileId,
            author.AudiobookMonitored, author.EbookMonitored,
            author.AudiobookMonitorNewItems, author.EbookMonitorNewItems,
            string.Join(",", author.AudiobookTags.OrderBy(id => id)),
            string.Join(",", author.EbookTags.OrderBy(id => id)),
            author.SyncMonitoredAcrossFormats,
            author.AudiobookSettingsManuallyOverridden, author.EbookSettingsManuallyOverridden
        };

        private static void ConfigureStoredSettings(Harness h, bool sync)
        {
            h.Import(h.Request("both", false));
            h.Scan();
            var author = h.Author;
            var audio = AuthorLibraryServiceRootFolderInheritanceFixture.BuildAudiobookRoot(AudioRootPath + "-chosen", 13, 12);
            var ebook = AuthorLibraryServiceRootFolderInheritanceFixture.BuildEbookRoot(EbookRootPath + "-chosen", 14, 13);
            h.Roots.All().AddRange(new[] { audio, ebook });
            author.AudiobookRootFolderPath = audio.Path;
            author.EbookRootFolderPath = ebook.Path;
            author.AudiobookQualityProfileId = 23;
            author.EbookQualityProfileId = 24;
            author.AudiobookMetadataProfileId = 22;
            author.EbookMetadataProfileId = 23;
            author.AudiobookMonitored = false;
            author.EbookMonitored = false;
            author.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
            author.EbookMonitorNewItems = NewItemMonitorTypes.None;
            author.AudiobookTags = new HashSet<int> { 7, 8 };
            author.EbookTags = new HashSet<int>();
            author.SyncMonitoredAcrossFormats = sync;
            h.Authors.UpdateAuthor(author);
            h.Commands.Clear();
            h.MetadataFetches = 0;
        }

        [Test]
        public void import_settings_omission_preserves_each_configured_field(
            [Values(false, true)] bool postgres,
            [Values("audiobook", "ebook", "both")] string media,
            [Values(false, true)] bool sync)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                ConfigureStoredSettings(h, sync);
                var before = SavedSettings(h.Author);
                var books = h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()).ToArray();
                h.Import(MinimalImport(media));
                Assert.Multiple(() =>
                {
                    Assert.That(SavedSettings(h.Author), Is.EqualTo(before));
                    Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.IsMonitored()), Is.EqualTo(books));
                    Assert.That(h.MetadataFetches, Is.Zero);
                    Assert.That(h.Commands, Is.Empty);
                });
            });
        }

        [Test]
        public void import_settings_explicit_changes_win_without_redefaulting_omitted_profiles(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook", "both")] string media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                ConfigureStoredSettings(h, true);
                var request = MinimalImport(media);
                request.SyncMonitoredAcrossFormats = false;
                request.Tags = new HashSet<int> { 99 };
                if (media is "audiobook" or "both")
                {
                    request.AudiobookRootFolderPath = h.AudioRoot.Path;
                    request.AudiobookTags = new HashSet<int>();
                    request.AudiobookMonitored = true;
                    request.AudiobookMonitorNewItems = NewItemMonitorTypes.New;
                    request.AudiobookMetadataProfileId = 2;
                }
                if (media is "ebook" or "both")
                {
                    request.EbookRootFolderPath = h.EbookRoot.Path;
                    request.EbookTags = new HashSet<int>();
                    request.EbookMonitored = true;
                    request.EbookMonitorNewItems = NewItemMonitorTypes.New;
                    request.EbookMetadataProfileId = 3;
                }
                h.Import(request);
                var saved = h.Author;
                Assert.Multiple(() =>
                {
                    Assert.That(saved.SyncMonitoredAcrossFormats, Is.False);
                    Assert.That(saved.AudiobookQualityProfileId, Is.EqualTo(23));
                    Assert.That(saved.EbookQualityProfileId, Is.EqualTo(24));
                    Assert.That(saved.AudiobookRootFolderPath, Is.EqualTo(media == "ebook" ? AudioRootPath + "-chosen" : h.AudioRoot.Path));
                    Assert.That(saved.EbookRootFolderPath, Is.EqualTo(media == "audiobook" ? EbookRootPath + "-chosen" : h.EbookRoot.Path));
                    Assert.That(saved.AudiobookMetadataProfileId, Is.EqualTo(media == "ebook" ? 22 : 2));
                    Assert.That(saved.EbookMetadataProfileId, Is.EqualTo(media == "audiobook" ? 23 : 3));
                    Assert.That(saved.AudiobookMonitored, Is.EqualTo(media != "ebook"));
                    Assert.That(saved.EbookMonitored, Is.EqualTo(media != "audiobook"));
                    Assert.That(saved.AudiobookMonitorNewItems, Is.EqualTo(media == "ebook" ? NewItemMonitorTypes.None : NewItemMonitorTypes.New));
                    Assert.That(saved.EbookMonitorNewItems, Is.EqualTo(media == "audiobook" ? NewItemMonitorTypes.None : NewItemMonitorTypes.New));
                    Assert.That(saved.AudiobookTags, Is.EquivalentTo(media == "ebook" ? new[] { 7, 8 } : Array.Empty<int>()));
                    Assert.That(saved.EbookTags, Is.Empty);
                    Assert.That(h.Commands, Is.Empty);
                });
            });
        }

        [Test]
        public void import_settings_pending_request_retains_choices_when_reposted_without_fields([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = false };
                var request = h.Request("both", false);
                request.AudiobookQualityProfileId = 23;
                request.EbookQualityProfileId = 24;
                request.AudiobookTags = new HashSet<int> { 7 };
                request.EbookTags = new HashSet<int>();
                request.AudiobookMonitored = false;
                request.EbookMonitored = false;
                h.Import(request);
                h.Import(MinimalImport());
                h.Available = true;
                h.ProcessPending();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author.AudiobookQualityProfileId, Is.EqualTo(23));
                    Assert.That(h.Author.EbookQualityProfileId, Is.EqualTo(24));
                    Assert.That(h.Author.AudiobookTags, Is.EquivalentTo(new[] { 7 }));
                    Assert.That(h.Author.EbookTags, Is.Empty);
                    Assert.That(h.Author.AudiobookMonitored, Is.False);
                    Assert.That(h.Author.EbookMonitored, Is.False);
                    Assert.That(h.Author.SyncMonitoredAcrossFormats, Is.False);
                });
            });
        }

        [Test]
        public void import_settings_new_side_uses_configured_default_and_leaves_other_side_alone(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string side,
            [Values(false, true)] bool existing)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                if (existing)
                {
                    h.Import(h.Request(side == "ebook" ? "audiobook" : "ebook", false));
                    h.Scan();
                }
                var root = side == "audiobook"
                    ? AuthorLibraryServiceRootFolderInheritanceFixture.BuildAudiobookRoot(AudioRootPath + "-default", 13, 2, MonitorTypes.None, false, NewItemMonitorTypes.None, new[] { 5 })
                    : AuthorLibraryServiceRootFolderInheritanceFixture.BuildEbookRoot(EbookRootPath + "-default", 14, 3, MonitorTypes.None, false, NewItemMonitorTypes.None, new[] { 6 });
                h.Roots.All().Add(root);
                Set(h.Library, "_configService", Proxy<IConfigService>((method, _) => method.Name switch
                {
                    "get_DefaultAudiobookRootFolderPath" => side == "audiobook" ? root.Path : h.AudioRoot.Path,
                    "get_DefaultEbookRootFolderPath" => side == "ebook" ? root.Path : h.EbookRoot.Path,
                    _ => throw new NotSupportedException(method.Name)
                }));
                var request = MinimalImport(side);
                request.SyncMonitoredAcrossFormats = false;
                h.Import(request);
                var author = h.Author;
                Assert.Multiple(() =>
                {
                    Assert.That(side == "audiobook" ? author.AudiobookRootFolderPath : author.EbookRootFolderPath, Is.EqualTo(root.Path));
                    Assert.That(side == "audiobook" ? author.AudiobookQualityProfileId : author.EbookQualityProfileId, Is.EqualTo(side == "audiobook" ? 13 : 14));
                    Assert.That(side == "audiobook" ? author.AudiobookMetadataProfileId : author.EbookMetadataProfileId, Is.EqualTo(side == "audiobook" ? 2 : 3));
                    Assert.That(side == "audiobook" ? author.AudiobookMonitored : author.EbookMonitored, Is.False);
                    Assert.That(side == "audiobook" ? author.AudiobookMonitorNewItems : author.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                    Assert.That(side == "audiobook" ? author.AudiobookTags : author.EbookTags, Is.EquivalentTo(new[] { side == "audiobook" ? 5 : 6 }));
                    Assert.That(side == "audiobook" ? author.EbookRootFolderPath : author.AudiobookRootFolderPath,
                        Is.EqualTo(existing ? (side == "audiobook" ? h.EbookRoot.Path : h.AudioRoot.Path) : null));
                    Assert.That(author.SyncMonitoredAcrossFormats, Is.False);
                });
            });
        }

        [Test]
        public void import_settings_invalid_or_ambiguous_root_fails_before_save_or_provider_lookup(
            [Values(false, true)] bool postgres, [Values("ambiguous", "wrong-side", "missing")] string invalid)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Roots.All().Add(AuthorLibraryServiceRootFolderInheritanceFixture.BuildAudiobookRoot(AudioRootPath + "-other"));
                var request = MinimalImport("audiobook");
                request.AudiobookRootFolderPath = invalid == "wrong-side" ? h.EbookRoot.Path : invalid == "missing" ? AudioRootPath + "-missing" : null;
                Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.Multiple(() =>
                {
                    Assert.That(h.Author, Is.Null);
                    Assert.That(h.MetadataFetches, Is.Zero);
                    Assert.That(h.Commands, Is.Empty);
                    Assert.That(h.Pending.GetByProviderId("hc:123456"), Is.Null);
                });
            });
        }

        [Test]
        public void import_settings_unavailable_side_preserves_saved_choices_until_hydrated(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Import(h.Request(side == "ebook" ? "audiobook" : "ebook", false));
                h.Scan();
                h.Available = false;
                var request = h.Request(side, false);
                request.SearchForMissingBooks = true;
                request.AudiobookMonitored = false;
                request.EbookMonitored = false;
                request.AudiobookQualityProfileId = 23;
                request.EbookQualityProfileId = 24;
                h.Import(request);
                var before = SavedSettings(h.Author);
                h.Import(MinimalImport(side));
                Assert.That(SavedSettings(h.Author), Is.EqualTo(before));
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(side == "audiobook" ? h.Author.AddOptions.AudiobookMonitor : h.Author.AddOptions.EbookMonitor, Is.EqualTo(MonitorTypes.All));
                h.Available = true;
                h.Import(MinimalImport(side));
                Assert.That(SavedSettings(h.Author), Is.EqualTo(before));
                Assert.That(h.Author.AddOptions.AwaitingMetadata, Is.False);
                Assert.That(h.Author.AddOptions.SearchForMissingBooks, Is.True);
                Assert.That(h.BookRows.GetBooksByAuthorId(h.Author.Id).Select(book => book.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
            });
        }
    }
}
