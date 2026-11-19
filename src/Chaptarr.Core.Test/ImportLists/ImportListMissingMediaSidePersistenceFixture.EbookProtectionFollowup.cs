using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        private static AuthorLibraryService FollowupLibrary(StoredListLibrary h, Func<Author> catalog, MetadataProfileService profiles)
        {
            return new AuthorLibraryService(h.AuthorService, Proxy<IProvideAuthorInfo>((_, _) => catalog()), h.Books,
                null, h.Editions, null, profiles, new TestQualityProfileService(), h.Paths, h.Roots, h.Queue, h.Events,
                h.Pending, h.Database, new ImportListExclusionService(new ImportListExclusionRepository(h.Database, h.Events), h.Logger),
                new EditionMetadataProfileFilter(new TestTermMatcherService()), null, h.Logger, new EditionSelector(h.Logger),
                PinProtectionRefresh(h, new Author(), profiles));
        }

        private static void ProcessFollowupPin(StoredListLibrary h, PendingAuthorImport request, Func<Author> catalog,
            MetadataProfileService profiles, Book requested)
        {
            request.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            h.Requests.Update(request);
            var metadata = Proxy<IProvideBookInfo>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IProvideBookInfo.GetWorkInfo)));
                var owner = catalog();
                requested.Author = owner;
                return Tuple.Create(h.Author.GoodreadsAuthorId, requested, new List<Author> { owner });
            });
            new ProcessPendingImportsCommandHandler(h.Pending, FollowupLibrary(h, catalog, profiles), h.Books,
                metadata, h.Queue, h.Events, h.Logger, h.Aliases).Execute(new ProcessPendingImportsCommand());
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void opposite_side_author_exclusion_preserves_requested_pin_profile_protection(bool postgres, BookMediaType requestedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var profile = PinProtectionProfile(h);
                var profiles = PinProtectionProfiles(h);
                var source = PinProtectionCatalog(h, requestedSide).Books.First();
                var requestedId = source.Editions.Last().ForeignEditionId;
                EbookPinSync(h, EbookPinSource(h, new[] { source }, requestedSide == BookMediaType.Ebook ? "ebook" : "audiobook"),
                    new[] { source }, search: false, audiobookOnly: requestedSide == BookMediaType.Audiobook, metadataProfileId: profile.Id)
                    .Execute(new ImportListSyncCommand(1));
                var excludedSide = requestedSide == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                new ImportListExclusionRepository(h.Database, h.Events).Insert(new ImportListExclusion
                {
                    ForeignId = h.Author.GoodreadsAuthorId, Name = h.Author.Name, MediaType = excludedSide
                });
                ProcessFollowupPin(h, h.Requests.All().Single(), () => PinProtectionCatalog(h, requestedSide), profiles, source);
                var stored = h.Books.GetBooksByAuthor(h.Author.Id);
                Assert.That(stored, Has.Count.EqualTo(1), "An exclusion on the other side must not prune the requested protected pin.");
                var pinned = stored.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(pinned.MediaType, Is.EqualTo(requestedSide));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(pinned).ForeignEditionId, Is.EqualTo(requestedId));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(pinned).ManualAdd, Is.True);
                    Assert.That(pinned.AnyEditionOk, Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                });
                TestContext.WriteLine($"{database.DatabaseType}: {excludedSide} exclusion leaves {requestedSide} profile-protected pin {requestedId} fulfilled.");
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void late_author_exclusion_removes_only_its_media_protection_and_requests(bool postgres, BookMediaType excludedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                PinProtectionProfile(h);
                var remainingSide = excludedSide == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var config = new MonitoringConfig
                {
                    CreateAudiobook = true, CreateEbook = true, RequestedBy = "ImportList:Explicit source",
                    AudiobookNarrations = new List<RequestedNarration>
                    {
                        new() { MediaType = BookMediaType.Audiobook, WorkProviderId = "hc:123", EditionProviderId = "hc:edition:123202",
                            NarratorNames = new List<string> { "Requested Narrator" }, RequiresEdition = true },
                        new() { MediaType = BookMediaType.Ebook, WorkProviderId = "hc:123", EditionProviderId = "hc:edition:123202", RequiresEdition = true }
                    }
                };
                var inserted = false;
                Author Catalog()
                {
                    if (!inserted)
                    {
                        new ImportListExclusionRepository(h.Database, h.Events).Insert(new ImportListExclusion
                        {
                            ForeignId = h.Author.GoodreadsAuthorId, Name = h.Author.Name, MediaType = excludedSide
                        });
                        inserted = true;
                    }
                    var remote = PinProtectionCatalog(h, BookMediaType.Audiobook);
                    remote.Books.RemoveAll(book => book.GoodreadsWorkId != "gr:123");
                    return remote;
                }
                FollowupLibrary(h, Catalog, PinProtectionProfiles(h)).AddAuthorAsync(h.Author.GoodreadsAuthorId, config).GetAwaiter().GetResult();
                Assert.Multiple(() =>
                {
                    Assert.That(config.ProtectedNarrationIds.Keys, Is.EqualTo(new[] { remainingSide }));
                    Assert.That(config.ProtectedNarrationWorkIds.Keys, Is.EqualTo(new[] { remainingSide }));
                    Assert.That(config.AudiobookNarrations.Select(request => request.MediaType), Is.EqualTo(new[] { remainingSide }));
                    Assert.That(config.KnownParentEditionRequests.Any(request => request.MediaType == excludedSide), Is.False);
                    Assert.That(excludedSide == BookMediaType.Audiobook ? config.CreateAudiobook : config.CreateEbook, Is.False);
                    Assert.That(remainingSide == BookMediaType.Audiobook ? config.CreateAudiobook : config.CreateEbook, Is.True);
                    Assert.That(h.Books.GetBooksByAuthor(h.Author.Id).Select(book => book.MediaType), Is.EqualTo(new[] { remainingSide }));
                });
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 0, BookMediaType.Audiobook)]
        [TestCase(true, 0, BookMediaType.Audiobook)]
        public void matched_then_pruned_pin_reports_unavailable_without_catalog_resurrection(bool postgres, int requestedFormat, BookMediaType side = BookMediaType.Ebook)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var profile = PinProtectionProfile(h);
                var profiles = PinProtectionProfiles(h);
                var source = EbookPinCatalog(side, requestedFormat: requestedFormat == 0 ? 3 : requestedFormat);
                var requestedId = source.Editions.Last().ForeignEditionId;
                EbookPinSync(h, EbookPinSource(h, new[] { source }, side == BookMediaType.Ebook ? "ebook" : "audiobook"), new[] { source },
                    search: false, audiobookOnly: side == BookMediaType.Audiobook, metadataProfileId: profile.Id)
                    .Execute(new ImportListSyncCommand(1));
                Author Catalog()
                {
                    var book = EbookPinCatalog(side, requestedFormat: requestedFormat == 0 ? 3 : requestedFormat);
                    if (requestedFormat == 0)
                    {
                        book.Editions.Last().ForeignEditionId = "az:B000000002";
                        book.Editions.Last().Language = "fra";
                        book.Editions.Add(new Edition
                        {
                            ForeignEditionId = "az:B000000003", HardcoverEditionId = requestedId,
                            Title = "Ambiguous provider alias", ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3, Language = "fra",
                            Narrator = side == BookMediaType.Audiobook ? "Requested Narrator" : null
                        });
                    }
                    return new Author { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                        Books = new List<Book> { book }, Series = new List<Series>() };
                }
                ProcessFollowupPin(h, h.Requests.All().Single(), Catalog, profiles, source);
                var failed = h.Requests.GetByProviderId(h.Author.GoodreadsAuthorId);
                var retained = h.Books.GetBooksByAuthor(h.Author.Id).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(side == BookMediaType.Ebook ? failed.EbookStatus : failed.AudiobookStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.LastError, Does.Contain(requestedId).And.Contain("profile-filtered catalog"));
                    Assert.That(failed.AudiobookNarrations, Does.Contain(requestedId));
                    Assert.That(h.Requests.GetActiveByProviderId(h.Author.GoodreadsAuthorId), Is.Null);
                    Assert.That(retained.AnyEditionOk, Is.True);
                    Assert.That(retained.Editions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { "hc:edition:123201" }));
                    Assert.That(retained.Editions.Any(edition => edition.ManualAdd), Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                });
                TestContext.WriteLine($"{database.DatabaseType}: requested {requestedId}, {(requestedFormat == 0 ? "ambiguous profile protection" : "native retention")} prunes exact edition; request reports unavailable and preserves automatic catalog.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_pin_without_known_parent_remains_retryable(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var profile = PinProtectionProfile(h);
                var source = EbookPinCatalog(BookMediaType.Ebook, "777");
                EbookPinSync(h, EbookPinSource(h, new[] { source }, "ebook"), new[] { source }, search: false, metadataProfileId: profile.Id)
                    .Execute(new ImportListSyncCommand(1));
                Author Catalog() => new() { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                    Books = new List<Book> { EbookPinCatalog(BookMediaType.Ebook, "124") }, Series = new List<Series>() };
                ProcessFollowupPin(h, h.Requests.All().Single(), Catalog, PinProtectionProfiles(h), source);
                var pending = h.Requests.GetActiveByProviderId(h.Author.GoodreadsAuthorId);
                Assert.That(pending, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(pending.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(pending.LastError, Does.Not.Contain("profile-filtered catalog"));
                    Assert.That(pending.AudiobookNarrations, Does.Contain(source.Editions.Last().ForeignEditionId));
                    Assert.That(h.Books.GetBooksByAuthor(h.Author.Id).Select(book => book.GoodreadsWorkId), Does.Not.Contain(source.GoodreadsWorkId));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                });
            });
        }
    }
}
