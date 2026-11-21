using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Books;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.MetadataSource.BookInfo;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.ImportLists
{
    public partial class ImportListMissingMediaSidePersistenceFixture
    {
        private static Book EbookPinCatalog(BookMediaType side, string work = "123", int requestedFormat = 3)
        {
            var book = CatalogBook(side, "Pin Diagnostic Work " + work, work);
            book.Editions = new List<Edition>
            {
                new() { ForeignEditionId = "hc:edition:" + work + "201", HardcoverEditionId = "hc:edition:" + work + "201",
                    Title = "Automatic edition", ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3,
                    Narrator = side == BookMediaType.Audiobook ? "Original Narrator" : null,
                    Language = "eng", Monitored = true, ReleaseDate = new DateTime(2020, 1, 1) },
                new() { ForeignEditionId = "hc:edition:" + work + "202", HardcoverEditionId = "hc:edition:" + work + "202",
                    Title = "Requested edition", ReadingFormatId = side == BookMediaType.Audiobook ? 2 : requestedFormat,
                    Narrator = side == BookMediaType.Audiobook ? "Requested Narrator" : null,
                    Language = "eng", ReleaseDate = new DateTime(2020, 1, 1) }
            };
            return book;
        }

        private static List<ImportListItemInfo> EbookPinSource(StoredListLibrary h, IEnumerable<Book> books, string sourceMedia, bool pinned = true, bool perBookMedia = false, bool omitSourceWork = false)
        {
            var remoteAuthor = new ChaptarrAuthor { Id = 50, AuthorName = h.Author.Name, ForeignAuthorId = h.Author.GoodreadsAuthorId,
                Monitored = true, AudiobookMonitored = true, EbookMonitored = true };
            var remoteBooks = books.Select(book => new ChaptarrBook
            {
                AuthorId = remoteAuthor.Id, Title = book.Title, ForeignBookId = omitSourceWork ? null : book.GoodreadsWorkId,
                ForeignEditionId = book.Editions.Last().ForeignEditionId,
                MediaType = perBookMedia ? (book.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook") : sourceMedia, AnyEditionOk = !pinned,
                Monitored = true, AudiobookMonitored = true, EbookMonitored = true,
                Editions = new List<ChaptarrEdition> { new() { ForeignEditionId = book.Editions.Last().ForeignEditionId,
                    ReadingFormatId = book.Editions.Last().ReadingFormatId, Narrator = book.Editions.Last().Narrator, Monitored = true } }
            }).ToList();
            return new ChaptarrImport(Proxy<IChaptarrV1Proxy>((method, _) => method.Name switch
            {
                nameof(IChaptarrV1Proxy.GetAuthors) => new List<ChaptarrAuthor> { remoteAuthor },
                nameof(IChaptarrV1Proxy.GetBooks) => remoteBooks,
                _ => throw new AssertionException(method.Name)
            }), Proxy<IImportListStatusService>((_, _) => null), null, null, null, null, null, null, null, h.Logger)
            { Definition = new ImportListDefinition { Id = 1, Name = "Explicit source", Settings = new ChaptarrSettings() } }.Fetch().ToList();
        }

        private static HttpResponse EbookPinLookupResponse(HttpRequest request, Author author, Book ebook)
        {
            var editions = ebook.Editions.Select(edition => new
            {
                id = edition.ForeignEditionId, hardcoverEditionId = edition.HardcoverEditionId,
                title = edition.Title, format = "ebook", readingFormatId = edition.ReadingFormatId,
                language = edition.Language, publicationDate = edition.ReleaseDate
            }).ToList();
            var payload = JsonConvert.SerializeObject(new
            {
                work = new { id = ebook.HardcoverBookId, title = ebook.Title, hardcoverBookId = ebook.HardcoverBookId,
                    goodreadsWorkId = ebook.GoodreadsWorkId, editions },
                editions,
                authors = new[] { new { name = author.Name, providerIds = new Dictionary<string, string> { { "gr", author.GoodreadsAuthorId } } } },
                series = Array.Empty<object>()
            });
            return new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, payload, HttpStatusCode.OK);
        }

        [Test]
        public void sender_cleanup_preserves_both_explicit_media_requests_when_other_report_fields_are_identical()
        {
            DualDatabaseFixture.Run(false, database =>
            {
                var h = new StoredListLibrary(database);
                var audio = EbookPinCatalog(BookMediaType.Audiobook);
                var ebook = EbookPinCatalog(BookMediaType.Ebook, requestedFormat: 2);
                ebook.Editions[1].Narrator = audio.Editions[1].Narrator;
                var reports = EbookPinSource(h, new[] { audio, ebook }, null, perBookMedia: true);
                Assert.Multiple(() =>
                {
                    Assert.That(reports, Has.Count.EqualTo(2));
                    Assert.That(reports.Select(report => JObject.FromObject(report)["EditionMediaType"]?.Value<int>()), Is.EquivalentTo(new int?[] { 0, 1 }));
                    Assert.That(reports.Select(report => report.EditionProviderId), Is.All.EqualTo(audio.Editions[1].ForeignEditionId));
                    Assert.That(reports.Select(report => report.HardcoverReadingFormatId), Is.All.EqualTo(2));
                });
            });
        }

        private static ImportListSyncService EbookPinSync(StoredListLibrary h, List<ImportListItemInfo> reports,
            IReadOnlyList<Book> catalog, bool both = false, bool search = true, IProvideBookInfo metadata = null, bool audiobookOnly = false, int? metadataProfileId = null)
        {
            var list = new ImportListDefinition
            {
                Id = 1, Name = "Explicit source", EnableAutomaticAdd = true, ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true, ShouldSearch = search, RootFolderPath = @"C:\Ebooks".AsOsAgnostic(),
                MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = true, AudiobookMonitored = false,
                    EbookMonitored = false, AudiobookMonitorNewItems = NewItemMonitorTypes.None, EbookMonitorNewItems = NewItemMonitorTypes.None },
                Settings = new ChaptarrSettings { MonitorAudiobooks = both || audiobookOnly, MonitorEbooks = !audiobookOnly,
                    AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic(),
                    AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = metadataProfileId ?? 4, EbookQualityProfileId = 5, EbookMetadataProfileId = metadataProfileId ?? 6 }
            };
            metadata ??= Proxy<IProvideBookInfo>((method, args) =>
            {
                h.MetadataReads++;
                var side = (BookMediaType)args[1];
                var id = (string)args[0];
                var found = catalog.FirstOrDefault(book => book.MediaType == side &&
                    (method.Name == nameof(IProvideBookInfo.GetEditionInfo)
                        ? book.Editions.Any(edition => BookEditionIdentity.EditionMatchesProviderId(edition, id))
                        : BookIdentity.GetStableWorkProviderIdentityTokens(book).Contains(id)));
                if (found == null) throw new BookNotFoundException(id);
                return Tuple.Create(h.Author.GoodreadsAuthorId, found, new List<Author> { h.Author });
            });
            var instance = new ChaptarrImport(null, null, null, null, null, null, null, null, null, h.Logger) { Definition = list };
            return new ImportListSyncService(
                Proxy<IImportListFactory>((method, _) => method.Name switch
                {
                    nameof(IImportListFactory.Get) => list,
                    nameof(IImportListFactory.GetInstance) => instance,
                    _ => throw new AssertionException(method.Name)
                }),
                new ImportListExclusionService(new ImportListExclusionRepository(h.Database, h.Events), h.Logger),
                Proxy<IFetchAndParseImportList>((method, _) => method.Name == nameof(IFetchAndParseImportList.FetchSingleList) ? reports : throw new AssertionException(method.Name)),
                metadata, h.AuthorService, h.Books, h.Editions, h.Pending, h.Roots, new RootFolderSettingsResolver(h.Roots),
                h.Events, h.Queue, h.Logger, new ImportListBookIdentityCacheRepository(h.Database, h.Events));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_source_pin_through_audio_only_lookup_remains_generic_audio(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var audio = EbookPinCatalog(BookMediaType.Audiobook);
                audio.Editions.RemoveAt(1); h.Save(audio);
                var ebook = EbookPinCatalog(BookMediaType.Ebook);
                var remote = new Author { Name = h.Author.Name, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                    Books = new List<Book> { audio, ebook } };
                foreach (var book in remote.Books) book.Author = remote;
                var httpRequests = 0;
                var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(Proxy<IHttpClient>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Get)));
                    httpRequests++;
                    var request = (HttpRequest)args[0];
                    Assert.That(request.Url.ToString(), Does.EndWith("/api/v5/book/" + BookEditionIdentity.NormalizeEditionLookupId(ebook.Editions[1].ForeignEditionId)));
                    return EbookPinLookupResponse(request, h.Author, ebook);
                }));
                for (var pass = 0; pass < 3; pass++)
                {
                    var reports = EbookPinSource(h, new[] { ebook }, "ebook", omitSourceWork: true);
                    EbookPinSync(h, reports, remote.Books, metadata: metadata, audiobookOnly: true).Execute(new ImportListSyncCommand(1));
                }
                var stored = h.Books.GetBook(audio.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(h.BookRows.All(), Has.Count.EqualTo(1));
                    Assert.That(stored.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).Id, Is.EqualTo(audio.Editions[0].Id));
                    Assert.That(stored.AnyEditionOk, Is.True);
                    Assert.That(stored.Editions.Any(edition => edition.ManualAdd), Is.False);
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(httpRequests, Is.EqualTo(1));
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook, false)]
        [TestCase(false, BookMediaType.Audiobook, true)]
        [TestCase(false, BookMediaType.Ebook, false)]
        [TestCase(false, BookMediaType.Ebook, true)]
        [TestCase(true, BookMediaType.Audiobook, false)]
        [TestCase(true, BookMediaType.Audiobook, true)]
        [TestCase(true, BookMediaType.Ebook, false)]
        [TestCase(true, BookMediaType.Ebook, true)]
        public void both_destination_sides_preserve_explicit_source_enum_and_generic_sibling(bool postgres, BookMediaType sourceSide, bool pinned)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var catalog = new[] { EbookPinCatalog(BookMediaType.Audiobook), EbookPinCatalog(BookMediaType.Ebook) };
                foreach (var book in catalog) h.Save(book);
                var source = catalog.Single(book => book.MediaType == sourceSide);
                var reports = EbookPinSource(h, new[] { source }, sourceSide == BookMediaType.Ebook ? "ebook" : "audiobook", pinned);
                var sync = EbookPinSync(h, reports, catalog, both: true);
                var originalIds = catalog.ToDictionary(book => book.MediaType, book => book.Id);
                for (var pass = 0; pass < 3; pass++)
                {
                    sync.Execute(new ImportListSyncCommand(1));
                    var rows = h.Books.GetBooksByAuthor(h.Author.Id);
                    Assert.That(rows, Has.Count.EqualTo(2));
                    foreach (var book in rows)
                    {
                        var explicitSide = pinned && book.MediaType == sourceSide;
                        var selection = BookEditionIdentity.GetMonitoredEdition(book);
                        Assert.Multiple(() =>
                        {
                            Assert.That(book.Id, Is.EqualTo(originalIds[book.MediaType]));
                            Assert.That(selection.ForeignEditionId, Is.EqualTo(source.Editions[explicitSide ? 1 : 0].ForeignEditionId));
                            Assert.That(book.AnyEditionOk, Is.EqualTo(!explicitSide));
                            Assert.That(selection.ManualAdd, Is.EqualTo(explicitSide));
                            Assert.That(book.Editions.Count(edition => edition.Monitored), Is.EqualTo(1));
                        });
                    }
                    Assert.That(h.Requests.All(), Is.Empty, "An available source pin must not enqueue its generic sibling.");
                }
                TestContext.WriteLine($"{database.DatabaseType}: source={sourceSide}, pinned={pinned}; 3 syncs, 2 stable rows; exact and generic selections isolated.");
            });
        }

        [TestCase(false, "audiobook")]
        [TestCase(true, "audiobook")]
        [TestCase(false, null)]
        [TestCase(true, null)]
        [TestCase(false, "unknown")]
        [TestCase(true, "unknown")]
        public void non_ebook_source_pin_cannot_invent_ebook_intent(bool postgres, string sourceMedia)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var book = EbookPinCatalog(BookMediaType.Ebook);
                h.Save(book);
                var source = EbookPinCatalog(BookMediaType.Audiobook);
                EbookPinSync(h, EbookPinSource(h, new[] { source }, sourceMedia), new[] { book, source }).Execute(new ImportListSyncCommand(1));
                var stored = h.Books.GetBook(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).Id, Is.EqualTo(book.Editions[0].Id));
                    Assert.That(stored.AnyEditionOk, Is.True);
                    Assert.That(stored.Editions.Any(edition => edition.ManualAdd), Is.False);
                    Assert.That(h.Requests.All(), Is.Empty);
                });
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        public void retained_ebook_source_pin_uses_book_media_enum_across_reading_formats(bool postgres, int readingFormat)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var ebook = EbookPinCatalog(BookMediaType.Ebook, requestedFormat: readingFormat);
                var audio = EbookPinCatalog(BookMediaType.Audiobook);
                audio.Editions.RemoveAt(1);
                h.Save(audio);
                h.Save(ebook);
                var requested = ebook.Editions[1];
                for (var pass = 0; pass < 3; pass++)
                {
                    var reports = EbookPinSource(h, new[] { ebook }, "ebook");
                    var sourceMediaType = JObject.FromObject(reports.Single())["EditionMediaType"]?.Value<int>();
                    EbookPinSync(h, reports, new[] { audio, ebook }, both: true).Execute(new ImportListSyncCommand(1));
                    var selected = h.Books.GetBook(ebook.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(sourceMediaType, Is.EqualTo((int)BookMediaType.Ebook));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).Id, Is.EqualTo(requested.Id));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ForeignEditionId, Is.EqualTo(requested.ForeignEditionId));
                        Assert.That(selected.AnyEditionOk, Is.False);
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ManualAdd, Is.True);
                        Assert.That(h.Books.GetBook(audio.Id).AnyEditionOk, Is.True);
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(h.Books.GetBook(audio.Id)).Id, Is.EqualTo(audio.Editions[0].Id));
                        Assert.That(h.Requests.All(), Is.Empty);
                    });
                }
                Assert.That(h.MetadataReads, Is.Zero, "Retained local work and edition identity must not require a provider lookup.");
                TestContext.WriteLine($"{database.DatabaseType}: retained Ebook format={readingFormat}, exact local edition={requested.Id}, no metadata lookups, stable pin over 3 fresh source syncs.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cold_lookup_mapping_uses_source_ebook_side_and_preserves_cached_repeat(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var ebook = EbookPinCatalog(BookMediaType.Ebook);
                var audio = EbookPinCatalog(BookMediaType.Audiobook);
                audio.Editions.RemoveAt(1);
                h.Save(audio);
                var remote = new Author { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                    Books = new List<Book> { audio, ebook }, Series = new List<Series>() };
                foreach (var book in remote.Books) book.Author = remote;
                var httpRequests = new List<string>();
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IHttpClient.Get)));
                    var request = (HttpRequest)args[0];
                    httpRequests.Add(request.Url.ToString());
                    Assert.That(request.Url.ToString(), Does.EndWith("/api/v5/book/" + BookEditionIdentity.NormalizeEditionLookupId(ebook.Editions[1].ForeignEditionId)));
                    return EbookPinLookupResponse(request, h.Author, ebook);
                });
                var metadata = BookInfoProxyCanonicalLookupFixture.CreateProxy(http);
                int? selectedEditionId = null;
                var requestedProviderId = ebook.Editions[1].ForeignEditionId;
                for (var pass = 0; pass < 3; pass++)
                {
                    var reports = EbookPinSource(h, new[] { ebook }, "ebook", omitSourceWork: true);
                    var sourceMediaType = JObject.FromObject(reports.Single())["EditionMediaType"]?.Value<int>();
                    EbookPinSync(h, reports, remote.Books, both: true, metadata: metadata).Execute(new ImportListSyncCommand(1));
                    if (pass == 0)
                    {
                        var pending = h.Requests.GetByProviderId(h.Author.GoodreadsAuthorId);
                        Assert.That(pending, Is.Not.Null, "A cold exact source edition must survive until the missing side is materialized.");
                        h.Process(pending, remote);
                    }
                    var selected = h.Books.GetBooksByAuthor(h.Author.Id).Single(book => book.MediaType == BookMediaType.Ebook);
                    selectedEditionId ??= BookEditionIdentity.GetMonitoredEdition(selected).Id;
                    Assert.Multiple(() =>
                    {
                        Assert.That(sourceMediaType, Is.EqualTo((int)BookMediaType.Ebook));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).Id, Is.EqualTo(selectedEditionId));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ForeignEditionId, Is.EqualTo(requestedProviderId));
                        Assert.That(selected.AnyEditionOk, Is.False);
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ManualAdd, Is.True);
                        Assert.That(h.Books.GetBook(audio.Id).AnyEditionOk, Is.True);
                        Assert.That(h.Requests.All(), Is.Empty);
                    });
                }
                Assert.That(httpRequests, Has.Count.EqualTo(1), "Durable list identity mapping must avoid another provider lookup on repeats.");
                Assert.That(new ImportListBookIdentityCacheRepository(h.Database, h.Events).All(), Has.Count.EqualTo(1));
                TestContext.WriteLine($"{database.DatabaseType}: V5 edition HTTP lookups={httpRequests.Count}, source Ebook format={ebook.Editions[1].ReadingFormatId}, stored manual pin retained over 3 syncs.");
            });
        }

        [TestCase(false, "pin")]
        [TestCase(true, "pin")]
        [TestCase(false, "file")]
        [TestCase(true, "file")]
        [TestCase(false, "profile")]
        [TestCase(true, "profile")]
        public void explicit_ebook_pin_preserves_conflicts_and_honors_profile_pruned_source(bool postgres, string refusal)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var fileRows = new MediaFileRepository(database, events);
                var files = Proxy<IMediaFileService>((method, args) => method.Name switch
                {
                    nameof(IMediaFileService.GetFilesByBooks) => fileRows.GetFilesByBooks((List<int>)args[0]),
                    nameof(IMediaFileService.GetFilesByAuthor) => fileRows.GetFilesByAuthor((int)args[0]),
                    nameof(IMediaFileService.GetFilesByBook) => fileRows.GetFilesByBook((int)args[0]),
                    nameof(IMediaFileService.GetFilesByEdition) => fileRows.GetFilesByEdition((int)args[0]),
                    _ => throw new AssertionException("A refused request must not mutate file ownership: " + method.Name)
                });
                var h = new StoredListLibrary(database, files: files);
                var local = EbookPinCatalog(BookMediaType.Ebook);
                if (refusal == "profile") local.Editions.RemoveAt(1);
                if (refusal == "pin") { local.AnyEditionOk = false; local.Editions[0].ManualAdd = true; }
                h.Save(local);
                BookFile storedFile = null;
                if (refusal == "file") storedFile = fileRows.Insert(new BookFile
                {
                    EditionId = local.Editions[0].Id, Edition = local.Editions[0], MediaType = "ebook",
                    Path = @"C:\ExistingEbooks\Existing Author\book.epub".AsOsAgnostic(), Size = 100, Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow,
                    Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
                });
                var remote = EbookPinCatalog(BookMediaType.Ebook);
                if (refusal == "profile") remote.Editions[1].Language = "fra";
                var requestedProviderId = remote.Editions[1].ForeignEditionId;
                var reports = EbookPinSource(h, new[] { remote }, "ebook");
                EbookPinSync(h, reports, new[] { remote }).Execute(new ImportListSyncCommand(1));
                var pending = h.Requests.GetByProviderId(h.Author.GoodreadsAuthorId);
                Assert.That(pending, Is.Not.Null, "A refused explicit Ebook request must be durable and must not report generic fulfillment.");
                Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                IMetadataProfileService profiles = null;
                if (refusal == "profile")
                {
                    var repository = new MetadataProfileRepository(h.Database, h.Events);
                    for (var index = 0; index < 8; index++) repository.Insert(new MetadataProfile
                    {
                        Name = "English catalog " + index, AllowedLanguages = "eng", SkipMissingDate = false,
                        MinPopularity = 0, Ignored = new List<string>()
                    });
                    profiles = new MetadataProfileService(repository, h.AuthorService, h.Books, h.Editions, h.Files, null, h.Roots,
                        new TestTermMatcherService(), h.Events, h.Logger);
                }
                h.Process(pending, new Author { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                    Books = new List<Book> { remote }, Series = new List<Series>() }, profiles);
                var failed = refusal == "profile" ? null : h.Requests.Get(pending.Id);
                var after = h.Books.GetBook(local.Id);
                var owner = h.Authors.Get(local.AuthorId);
                var added = new BookAddedService(new CacheManager(), h.Queue, h.Books, h.Logger);
                var scanned = new AuthorScannedHandler(new BookMonitoredService(h.AuthorService, h.Books, h.Logger),
                    h.AuthorService, h.Queue, added, h.Events, h.Logger, h.Books);
                for (var pass = 0; pass < 2; pass++)
                {
                    added.Handle(new BookInfoRefreshedEvent(owner, new List<Book>(), new List<Book> { after }, new List<Book>()));
                    scanned.Handle(new AuthorScannedEvent(owner));
                }
                if (refusal == "profile")
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(h.Requests.All(), Is.Empty);
                        Assert.That(h.BookRows.All().Select(book => book.Id), Is.EqualTo(new[] { local.Id }));
                        Assert.That(after.AnyEditionOk, Is.False);
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(after).ForeignEditionId, Is.EqualTo(requestedProviderId));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(after).ManualAdd, Is.True);
                        Assert.That(fileRows.All(), Is.Empty);
                        Assert.That(h.Commands.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { local.Id }));
                    });
                    TestContext.WriteLine($"{database.DatabaseType}: source Ebook pin {requestedProviderId} honors explicit profile protection on existing local book {local.Id}; one acknowledged search, no copy.");
                    return;
                }
                Assert.Multiple(() =>
                {
                    Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.EbookStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.LastError, Does.Contain(requestedProviderId));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.BookRows.All(), Has.Count.EqualTo(1));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(after).Id, Is.EqualTo(local.Editions[0].Id));
                    Assert.That(after.AnyEditionOk, Is.EqualTo(local.AnyEditionOk));
                    Assert.That(after.Editions.Single(edition => edition.Id == local.Editions[0].Id).ManualAdd, Is.EqualTo(local.Editions[0].ManualAdd));
                    Assert.That(after.Editions.Any(edition => edition.ManualAdd && edition.Id != local.Editions[0].Id), Is.False);
                    Assert.That(fileRows.All().Select(file => (file.Id, file.EditionId)),
                        Is.EqualTo(storedFile == null ? Array.Empty<(int, int)>() : new[] { (storedFile.Id, local.Editions[0].Id) }));
                });
                TestContext.WriteLine($"{database.DatabaseType}: source Ebook pin refused for {refusal}; durable Failed, original local book {local.Id}/edition {local.Editions[0].Id} and files unchanged, zero searches/copies.");
            });
        }

        [TestCase(false, "existing")]
        [TestCase(true, "existing")]
        [TestCase(false, "new")]
        [TestCase(true, "new")]
        [TestCase(false, "missing-side")]
        [TestCase(true, "missing-side")]
        public void explicit_ebook_request_survives_author_materialization_retry_and_refresh(bool postgres, string setup)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new StoredListLibrary(database);
                var ebook = EbookPinCatalog(BookMediaType.Ebook);
                var audio = EbookPinCatalog(BookMediaType.Audiobook);
                if (setup != "new") h.Save(audio);
                if (setup == "missing-side")
                {
                    h.Author.EbookQualityProfileId = null; h.Author.EbookMetadataProfileId = null; h.Author.EbookRootFolderPath = null;
                    h.Authors.Update(h.Author);
                }
                if (setup == "new") h.Authors.Delete(h.Author.Id);
                var remote = new Author { Name = h.Author.Name, Overview = string.Empty, GoodreadsAuthorId = h.Author.GoodreadsAuthorId,
                    Books = new List<Book> { ebook, audio }, Series = new List<Series>() };
                var reports = EbookPinSource(h, new[] { ebook }, "ebook");
                EbookPinSync(h, reports, remote.Books).Execute(new ImportListSyncCommand(1));
                var item = h.Requests.GetByProviderId(h.Author.GoodreadsAuthorId);
                Assert.That(item, Is.Not.Null);
                Assert.That(item.AudiobookNarrations, Is.Not.Null.And.Not.Empty, "The explicit source edition must survive in durable typed request JSON.");
                var intent = JArray.Parse(item.AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(intent["MediaType"]?.Value<int>(), Is.EqualTo((int)BookMediaType.Ebook));
                    Assert.That(intent["EditionProviderId"]?.Value<string>(), Is.EqualTo(ebook.Editions[1].ForeignEditionId));
                    Assert.That(item.HasEbook(), Is.True);
                    Assert.That(item.HasAudiobook(), Is.False);
                });
                h.Process(item, remote);
                Assert.That(h.Requests.GetByProviderId(h.Author.GoodreadsAuthorId), Is.Null);
                var owner = h.Authors.All().Single();
                var selected = h.Books.GetBooksByAuthor(owner.Id).Single(book => book.MediaType == BookMediaType.Ebook);
                var selectedId = BookEditionIdentity.GetMonitoredEdition(selected).Id;
                Assert.Multiple(() =>
                {
                    Assert.That(selected.AnyEditionOk, Is.False);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ForeignEditionId, Is.EqualTo(ebook.Editions[1].ForeignEditionId));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(selected).ManualAdd, Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { selected.Id }));
                });
                var refreshRequestId = h.Pending.EnqueueAsync(owner.GoodreadsAuthorId, new MonitoringConfig
                {
                    CreateEbook = true, EbookRootFolderPath = owner.EbookRootFolderPath,
                    EbookQualityProfileId = owner.EbookQualityProfileId, EbookMetadataProfileId = owner.EbookMetadataProfileId,
                    EbookBooksToMonitor = new List<string> { ebook.GoodreadsWorkId }, RequestedBy = "ImportList:Explicit source"
                }, "ImportList:Explicit source").GetAwaiter().GetResult();
                var refreshed = new Author { Name = owner.Name, Overview = string.Empty, GoodreadsAuthorId = owner.GoodreadsAuthorId,
                    Books = new List<Book> { EbookPinCatalog(BookMediaType.Ebook), EbookPinCatalog(BookMediaType.Audiobook) }, Series = new List<Series>() };
                h.Process(h.Requests.Get(refreshRequestId), refreshed);
                var after = h.Books.GetBook(selected.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(after).Id, Is.EqualTo(selectedId));
                    Assert.That(after.AnyEditionOk, Is.False);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(after).ManualAdd, Is.True);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { selected.Id }));
                });
                TestContext.WriteLine($"{database.DatabaseType}/{setup}: source selection -> durable request -> local Ebook book {selected.Id}/edition {selectedId}; repeat refresh preserved pin.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_writer_pins_already_selected_edition_with_existing_file_in_place(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = Proxy<IEventAggregator>((_, _) => null);
                var fileRows = new MediaFileRepository(database, events);
                var files = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBooks)
                    ? fileRows.GetFilesByBooks((List<int>)args[0])
                    : throw new AssertionException("Pinning the selected edition must not mutate files: " + method.Name));
                var h = new StoredListLibrary(database, files: files);
                var book = EbookPinCatalog(BookMediaType.Ebook);
                book.Editions[0].Monitored = false;
                book.Editions[1].Monitored = true;
                h.Save(book);
                var file = fileRows.Insert(new BookFile
                {
                    EditionId = book.Editions[1].Id, Edition = book.Editions[1], MediaType = "ebook",
                    Path = @"C:\ExistingEbooks\Existing Author\selected.epub".AsOsAgnostic(), Size = 100,
                    Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow, Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
                });
                for (var repeat = 0; repeat < 3; repeat++)
                {
                    var request = h.Books.GetBook(book.Id).Editions.Single(edition => edition.Id == file.EditionId);
                    var failures = new List<Exception>();
                    var result = h.Books.AddWantedEditions(new List<Edition> { request }, (_, error) => failures.Add(error));
                    var stored = h.Books.GetBook(book.Id);
                    TestContext.WriteLine($"{database.DatabaseType}: selected local edition {file.EditionId} owns file {file.Id}; pin repeat {repeat}, failures [{string.Join("; ", failures.Select(error => error.Message))}].");
                    Assert.Multiple(() =>
                    {
                        Assert.That(failures, Is.Empty);
                        Assert.That(result.ContainsKey(request.Id), Is.True);
                        Assert.That(result.GetValueOrDefault(request.Id)?.Id, Is.EqualTo(book.Id));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).Id, Is.EqualTo(file.EditionId));
                        Assert.That(BookEditionIdentity.GetMonitoredEdition(stored).ManualAdd, Is.True);
                        Assert.That(stored.AnyEditionOk, Is.False);
                        Assert.That(h.BookRows.All(), Has.Count.EqualTo(1));
                        Assert.That(new EditionRepository(h.Database, h.Events).All(), Has.Count.EqualTo(2));
                        Assert.That(fileRows.All().Select(row => (row.Id, row.EditionId, row.Path, row.Size)),
                            Is.EqualTo(new[] { (file.Id, file.EditionId, file.Path, file.Size) }));
                    });
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_ebook_list_batch_loads_catalog_and_writes_selection_once(bool postgres)
        {
            var measurements = new List<int>();
            foreach (var count in new[] { 1, 25 })
            {
                DualDatabaseFixture.Run(postgres, database =>
                {
                    var h = new StoredListLibrary(database);
                    var catalog = Enumerable.Range(1000, count).Select(work => EbookPinCatalog(BookMediaType.Ebook, work.ToString())).ToList();
                    foreach (var book in catalog) h.Save(book);
                    var reports = EbookPinSource(h, catalog, "ebook");
                    var sync = EbookPinSync(h, reports, catalog, search: false);
                    h.Calls.Clear(); h.ReaderStatements.Clear();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    sync.Execute(new ImportListSyncCommand(1));
                    var reads = h.ReaderStatements.ToList();
                    var calls = h.Calls.ToDictionary(pair => pair.Key, pair => pair.Value);
                    var rows = h.Books.GetBooksByAuthor(h.Author.Id);
                    Assert.Multiple(() =>
                    {
                        Assert.That(rows, Has.Count.EqualTo(count));
                        Assert.That(rows.All(book => !book.AnyEditionOk && BookEditionIdentity.GetMonitoredEdition(book).ManualAdd), Is.True);
                        Assert.That(calls.GetValueOrDefault(nameof(IBookService.AddWantedEditions)), Is.EqualTo(1));
                        Assert.That(calls.GetValueOrDefault(nameof(IBookService.GetImportListBookIdentities)), Is.EqualTo(1));
                        Assert.That(calls.GetValueOrDefault(nameof(IBookService.FindByProviderId)), Is.Zero);
                    });
                    measurements.Add(reads.Count);
                    TestContext.WriteLine($"{database.DatabaseType}: {count} explicit Ebook reports; {reads.Count} SELECTs, writer batches={calls.GetValueOrDefault(nameof(IBookService.AddWantedEditions))}, catalog reads={calls.GetValueOrDefault(nameof(IBookService.GetImportListBookIdentities))}, {watch.ElapsedMilliseconds}ms.");
                });
            }
            Assert.That(measurements[1], Is.EqualTo(measurements[0]), "Increasing source items from 1 to 25 must not add catalog, owner or edition SELECTs per item.");
        }
    }
}
