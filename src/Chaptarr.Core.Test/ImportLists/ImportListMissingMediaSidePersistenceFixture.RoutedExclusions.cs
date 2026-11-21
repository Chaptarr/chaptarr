using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Books;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
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
        [Test]
        public void routed_real_author_exclusion_fails_only_its_media_side_without_selecting_existing_rows(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType excludedSide,
            [Values(false, true)] bool excludedRowExists,
            [Values(false, true)] bool editionRequest,
            [Values(false, true)] bool originalOwner)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var stored = new StoredListLibrary(database);
                var owner = originalOwner ? stored.Author : stored.Authors.Insert(new Author
                {
                    Name = "Selected Primary", CleanName = "selectedprimary", SortName = "selectedprimary",
                    SortNameLastFirst = "Primary, Selected", TitleSlug = "selected-primary",
                    Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selected-primary"), Added = DateTime.UtcNow,
                    GoodreadsAuthorId = "gr:58", AudiobookQualityProfileId = 9, EbookQualityProfileId = 9,
                    AudiobookMetadataProfileId = 8, EbookMetadataProfileId = 8,
                    AudiobookRootFolderPath = stored.Author.AudiobookRootFolderPath,
                    EbookRootFolderPath = stored.Author.EbookRootFolderPath,
                    AudiobookMonitored = false, EbookMonitored = false
                });
                var healthySide = excludedSide == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                var sources = new[] { BookMediaType.Audiobook, BookMediaType.Ebook }
                    .Select(side => CatalogBook(side, "Routed work", "7000")).ToList();
                foreach (var book in sources.Where(book => book.MediaType == healthySide || excludedRowExists))
                {
                    book.AuthorId = owner.Id;
                    book.Author = owner;
                    stored.BookRows.Insert(book);
                    foreach (var edition in book.Editions) edition.BookId = book.Id;
                    stored.Editions.InsertMany(book.Editions);
                }
                var exclusions = new ImportListExclusionService(new ImportListExclusionRepository(stored.Database, stored.Events), stored.Logger);
                exclusions.Add(new ImportListExclusion { ForeignId = owner.GoodreadsAuthorId, MediaType = excludedSide, Name = owner.Name });
                var requested = sources.Select(book => new RequestedNarration
                {
                    MediaType = book.MediaType, WorkProviderId = book.GoodreadsWorkId,
                    EditionProviderId = book.Editions.Single().ForeignEditionId,
                    NarratorNames = book.MediaType == BookMediaType.Audiobook ? new() { "Fixture Narrator" } : null,
                    RequiresEdition = true, Search = true
                }).ToList();
                var pending = stored.Requests.Insert(new PendingAuthorImport
                {
                    ProviderId = stored.Author.GoodreadsAuthorId, ProviderPrefix = "gr", AuthorName = stored.Author.Name,
                    RequestedBy = "ImportList:Routed work", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow.AddMinutes(-1), Version = 1,
                    OverallStatus = PendingImportStatus.Pending, AudiobookStatus = PendingImportStatus.Pending, EbookStatus = PendingImportStatus.Pending,
                    AudiobookBooksToMonitor = "[\"gr:7000\"]", AudiobookBooksToSearch = "[\"gr:7000\"]",
                    EbookBooksToMonitor = "[\"gr:7000\"]", EbookBooksToSearch = "[\"gr:7000\"]",
                    AudiobookNarrations = editionRequest ? JsonConvert.SerializeObject(requested) : null,
                    AudiobookQualityProfileId = 9, EbookQualityProfileId = 9, AudiobookMetadataProfileId = 8, EbookMetadataProfileId = 8,
                    AudiobookRootFolderPath = stored.Author.AudiobookRootFolderPath, EbookRootFolderPath = stored.Author.EbookRootFolderPath
                });
                var authorLookups = new List<string>();
                var authorInfo = Proxy<IProvideAuthorInfo>((_, args) =>
                {
                    authorLookups.Add((string)args[0]);
                    var source = (string)args[0] == owner.GoodreadsAuthorId ? owner : stored.Author;
                    return new Author
                    {
                        Name = source.Name, GoodreadsAuthorId = source.GoodreadsAuthorId,
                        Books = (source == owner ? sources.Where(book => book.MediaType == healthySide || excludedRowExists)
                            .Select(book => CatalogBook(book.MediaType, book.Title, "7000")) : Enumerable.Empty<Book>()).ToList(),
                        Series = new List<Series>()
                    };
                });
                var profiles = new StubMetadataProfileService();
                var filter = new EditionMetadataProfileFilter(new TestTermMatcherService());
                var selector = new EditionSelector(stored.Logger);
                var library = new AuthorLibraryService(stored.AuthorService, authorInfo, stored.Books, null, stored.Editions, null,
                    profiles, new TestQualityProfileService(), stored.Paths, stored.Roots, stored.Queue, stored.Events,
                    stored.Pending, stored.Database, exclusions, filter, null, stored.Logger, selector);
                var workLookups = new List<BookMediaType>();
                var metadata = Proxy<IProvideBookInfo>((_, args) =>
                {
                    var side = (BookMediaType)args[1];
                    workLookups.Add(side);
                    var book = CatalogBook(side, "Routed work", "7000");
                    book.Author = owner;
                    return Tuple.Create(owner.GoodreadsAuthorId, book, new List<Author> { owner });
                });
                var handler = new ProcessPendingImportsCommandHandler(stored.Pending, library, stored.Books, metadata,
                    stored.Queue, stored.Events, stored.Logger, stored.Aliases);
                handler.Execute(new ProcessPendingImportsCommand());
                var receivingId = originalOwner ? pending.Id : stored.Pending.GetByProviderId(owner.GoodreadsAuthorId).Id;
                if (!originalOwner)
                {
                    Assert.That(stored.Requests.Find(pending.Id), Is.Null);
                    handler.Execute(new ProcessPendingImportsCommand());
                }
                var saved = stored.Requests.Get(receivingId);
                var catalog = stored.Books.GetBooksByAuthor(owner.Id);
                var healthy = catalog.Single(book => book.MediaType == healthySide);
                Assert.Multiple(() =>
                {
                    Assert.That(authorLookups, Is.EqualTo(originalOwner ? new[] { owner.GoodreadsAuthorId } : new[] { stored.Author.GoodreadsAuthorId, owner.GoodreadsAuthorId }));
                    Assert.That(workLookups, Is.EquivalentTo(originalOwner ? new[] { excludedSide } : new[] { BookMediaType.Audiobook, BookMediaType.Ebook, excludedSide }));
                    Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.PartialSuccess));
                    Assert.That(excludedSide == BookMediaType.Audiobook ? saved.AudiobookStatus : saved.EbookStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(healthySide == BookMediaType.Audiobook ? saved.AudiobookStatus : saved.EbookStatus, Is.EqualTo(PendingImportStatus.Succeeded));
                    Assert.That(saved.LastError, Does.Contain("excluded").And.Contain(excludedSide.ToString()));
                    Assert.That(saved.IsActive(), Is.False);
                    Assert.That(healthy.IsMonitored(), Is.True);
                    Assert.That(stored.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { healthy.Id }));
                    Assert.That(catalog.Where(book => book.MediaType == excludedSide).All(book => !book.IsMonitored() && book.AnyEditionOk &&
                        book.Editions.All(edition => !edition.ManualAdd)), Is.True);
                    Assert.That(catalog.Count(book => book.MediaType == excludedSide), Is.EqualTo(excludedRowExists ? 1 : 0));
                });
            });
        }
    }
}
