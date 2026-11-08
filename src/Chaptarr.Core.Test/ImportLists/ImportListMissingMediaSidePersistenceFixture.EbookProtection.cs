using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Profiles.Metadata;
using static Chaptarr.Core.Test.Common.InterfaceProxy;
using static Chaptarr.Core.Test.Books.AuthorLibraryServiceSpecificBookMonitoringFixture;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        private static MetadataProfileService PinProtectionProfiles(StoredListLibrary h)
        {
            return new MetadataProfileService(new MetadataProfileRepository(h.Database, h.Events), h.AuthorService,
                h.Books, h.Editions, h.Files, null, h.Roots, new TestTermMatcherService(), h.Events, h.Logger);
        }

        private static Author PinProtectionCatalog(StoredListLibrary h, BookMediaType side)
        {
            var opposite = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            Book Pocket(BookMediaType media, string work)
            {
                var book = EbookPinCatalog(media, work);
                book.ReleaseDate = null;
                book.Editions.ForEach(edition => edition.Language = "fra");
                return book;
            }
            return new Author
            {
                Name = h.Author.Name, Overview = string.Empty, CleanName = h.Author.CleanName,
                TitleSlug = h.Author.TitleSlug, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                Books = new List<Book> { Pocket(side, "123"), Pocket(side, "124"), Pocket(opposite, "123") },
                Series = new List<Series>()
            };
        }

        private static RefreshAuthorService PinProtectionRefresh(StoredListLibrary h, Author remote, MetadataProfileService profiles)
        {
            var authorInfo = new StubAuthorInfo(remote);
            var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(h.Database, h.Events), h.Logger);
            var selector = new EditionSelector(h.Logger);
            var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
            var refreshEditions = new RefreshEditionService(h.Editions, Proxy<IMetadataTagService>((_, _) => null), null, h.Database, h.Logger);
            var refreshBooks = new RefreshBookService(h.Books, h.AuthorService, h.Roots, h.Editions, authorInfo, null,
                refreshEditions, h.Files, null, h.Events, null, selector, filter, Proxy<IMapCoversToLocal>((_, _) => null), h.Logger, mainDatabase: null);
            return new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, h.AuthorService, h.Books, h.Editions, profiles, refreshBooks, null,
                h.Events, h.Queue, h.Files, new global::NzbDrone.Core.History.HistoryRepository(h.Database, h.Events), h.Roots, null, new MonitorNewBookService(h.Logger),
                Proxy<IConfigService>((method, _) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null),
                exclusions, null, null, null, h.Logger, editionSelector: selector, mainDatabase: h.Database, bookRepository: new BookRepository(h.Database, h.Events), authorRepository: new AuthorRepository(h.Database, h.Events, h.Logger));
        }

        private static MetadataProfile PinProtectionProfile(StoredListLibrary h)
        {
            var profile = new MetadataProfileRepository(h.Database, h.Events).Insert(new MetadataProfile
            {
                Name = "English dated catalog", AllowedLanguages = "eng", SkipMissingDate = true,
                MinPopularity = 0, Ignored = new List<string>()
            });
            h.Author.AudiobookMetadataProfileId = profile.Id;
            h.Author.EbookMetadataProfileId = profile.Id;
            h.Authors.Update(h.Author);
            return profile;
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void explicit_source_pin_survives_profile_import_and_ordinary_refresh(bool postgres, BookMediaType side, bool existingBook, bool newAuthor = false)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var profile = PinProtectionProfile(h);
                var profiles = PinProtectionProfiles(h);
                if (newAuthor)
                {
                    h.Authors.Delete(h.Author.Id);
                    Assert.That(h.Authors.All(), Is.Empty);
                }
                var source = PinProtectionCatalog(h, side).Books.First();
                if (existingBook)
                {
                    var local = EbookPinCatalog(side);
                    local.Editions = new List<Edition> { local.Editions.First() };
                    local.ReleaseDate = null;
                    h.Save(local);
                }

                var reports = EbookPinSource(h, new[] { source }, side == BookMediaType.Audiobook ? "audiobook" : "ebook");
                Assert.That(reports, Has.Count.EqualTo(1));
                EbookPinSync(h, reports, new[] { source }, search: false, audiobookOnly: side == BookMediaType.Audiobook,
                    metadataProfileId: profile.Id).Execute(new ImportListSyncCommand(1));
                var request = h.Requests.All().Single();
                Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(request.AudiobookNarrations).Single().MediaType, Is.EqualTo(side));
                var requestedId = source.Editions.Last().ForeignEditionId;
                h.Process(request, PinProtectionCatalog(h, side), profiles);

                Assert.That(h.Requests.All(), Is.Empty);
                var storedAuthor = h.Authors.All().Single();
                Assert.That(side == BookMediaType.Audiobook ? storedAuthor.AudiobookMetadataProfileId : storedAuthor.EbookMetadataProfileId, Is.EqualTo(profile.Id));
                var pinned = h.Books.GetBooksByAuthor(storedAuthor.Id).Single();
                var pin = BookEditionIdentity.GetMonitoredEdition(pinned);
                Assert.Multiple(() =>
                {
                    Assert.That(pinned.MediaType, Is.EqualTo(side));
                    Assert.That(pinned.AnyEditionOk, Is.False);
                    Assert.That(pin.ForeignEditionId, Is.EqualTo(requestedId));
                    Assert.That(pin.ManualAdd, Is.True);
                    Assert.That(pin.Language, Is.EqualTo("fra"));
                    Assert.That(pinned.ReleaseDate, Is.Null);
                });
                var bookId = pinned.Id;
                var editionId = pin.Id;

                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var remote = PinProtectionCatalog(h, side);
                    PinProtectionRefresh(h, remote, profiles).ReconcileAuthorBlob(h.Authors.Get(storedAuthor.Id), remote);
                    var books = h.Books.GetBooksByAuthor(storedAuthor.Id);
                    Assert.That(books.Select(book => book.Id), Is.EqualTo(new[] { bookId }));
                    var selected = BookEditionIdentity.GetMonitoredEdition(books.Single());
                    Assert.Multiple(() =>
                    {
                        Assert.That(selected.Id, Is.EqualTo(editionId));
                        Assert.That(selected.ForeignEditionId, Is.EqualTo(requestedId));
                        Assert.That(selected.ManualAdd, Is.True);
                        Assert.That(books.Single().AnyEditionOk, Is.False);
                        Assert.That(books.Single().MediaType, Is.EqualTo(side));
                    });
                }
                TestContext.WriteLine($"{database.DatabaseType}/{side}: explicit {requestedId} survives English/date pruning, import pin and two ordinary refreshes; unrequested/opposite books removed.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void new_author_explicit_ebook_pin_survives_profile_import_and_ordinary_refresh(bool postgres)
        {
            explicit_source_pin_survives_profile_import_and_ordinary_refresh(postgres, BookMediaType.Ebook, false, newAuthor: true);
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void requested_pin_protection_does_not_cross_identical_work_or_edition_ids(bool postgres, BookMediaType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var profile = PinProtectionProfile(h);
                var profiles = PinProtectionProfiles(h);
                var remote = PinProtectionCatalog(h, side);
                var requested = remote.Books.First();
                var request = new RequestedNarration
                {
                    MediaType = side, WorkProviderId = requested.HardcoverBookId,
                    EditionProviderId = requested.Editions.Last().ForeignEditionId, RequiresEdition = true,
                    NarratorNames = side == BookMediaType.Audiobook ? new List<string> { "Requested Narrator" } : null
                };
                var config = new MonitoringConfig { CreateAudiobook = true, CreateEbook = true, AudiobookNarrations = new() { request } };
                AuthorLibraryService.PrepareRequestedNarrations(remote, config);
                var json = JObject.FromObject(config);
                Assert.That(json.Property("protectedNarrationIds", StringComparison.OrdinalIgnoreCase), Is.Null);
                Assert.That(json.Property("protectedNarrationWorkIds", StringComparison.OrdinalIgnoreCase), Is.Null);
                var result = RefreshAuthorService.NormalizeRemoteBooks(h.Author, remote.Books, remote.Series, profiles,
                    null, h.Logger, profile.Id, profile.Id, new EditionSelector(h.Logger), true, "requested pin media scope",
                    requestedNarrationIds: config.ProtectedNarrationIds, requestedNarrationWorkIds: config.ProtectedNarrationWorkIds);
                Assert.Multiple(() =>
                {
                    Assert.That(result, Has.Count.EqualTo(1));
                    Assert.That(result.Single(), Is.SameAs(requested));
                    Assert.That(result.Single().MediaType, Is.EqualTo(side));
                    Assert.That(result.Single().Editions.Select(edition => edition.ForeignEditionId), Is.EqualTo(new[] { request.EditionProviderId }));
                    Assert.That(config.KnownParentEditionRequests.Count, Is.EqualTo(1));
                    Assert.That(config.KnownParentEditionRequests.Contains((side, request.EditionProviderId)), Is.True);
                });
            });
        }
    }
}
