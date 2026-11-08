using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Books
{
    public partial class AddBookServiceAutoEditionSelectionFixture
    {
        private static Edition RoutingEdition(string id) => new()
        {
            ForeignEditionId = id,
            GoodreadsEditionId = id.StartsWith("gr:") ? long.Parse(ProviderIdHelper.StripPrefix(id)) : null,
            HardcoverEditionId = id.StartsWith("hc:") ? id : null,
            Asin = id.StartsWith("az:") ? id : null,
            OpenLibraryEditionId = id.StartsWith("ol:") ? id : null,
            GoogleBooksEditionId = id.StartsWith("gb:") ? id : null,
            Monitored = true
        };

        private class RoutingBookInfoProxy : DispatchProxy
        {
            public Tuple<string, Book, List<Author>> Result { get; set; }
            public int WorkReads { get; private set; }
            public int EditionReads { get; private set; }
            public Exception Error { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IProvideBookInfo.GetWorkInfo)) WorkReads++;
                else if (method.Name == nameof(IProvideBookInfo.GetEditionInfo)) EditionReads++;
                else throw new NotImplementedException(method.Name);
                if (Error != null) throw Error;
                return Result ?? throw new BookNotFoundException((string)args[0]);
            }
        }

        [Test]
        public async Task native_primary_routing_preserves_request_and_reuses_edition_proof(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(null, "gr:8888", "hc:edition:8888", "az:B000ABC123", "ol:OL8888M", "gb:volume8888")] string editionId,
            [Values(MonitorTypes.SpecificBook, MonitorTypes.All)] MonitorTypes insertion)
        {
            var selected = new Author { Id = 43, Name = "Selected Primary", HardcoverAuthorId = "hc:9001" };
            var contributor = new Author { HardcoverAuthorId = "hc:2222", Name = "Reported Contributor",
                AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = 4, AudiobookRootFolderPath = @"C:\Audio",
                EbookQualityProfileId = 5, EbookMetadataProfileId = 6, EbookRootFolderPath = @"C:\Books",
                AudiobookTags = new HashSet<int> { 7 }, EbookTags = new HashSet<int> { 8 },
                AudiobookMonitorNewItems = NewItemMonitorTypes.All, EbookMonitorNewItems = NewItemMonitorTypes.All,
                AddOptions = new AddAuthorOptions { Monitor = insertion } };
            var local = new Book { Id = 101, AuthorId = selected.Id, Author = selected, MediaType = media, HardcoverBookId = "hc:7000" };
            var books = new StubBookService { AuthorBooks = new List<Book> { local } };
            var info = DispatchProxy.Create<IProvideBookInfo, RoutingBookInfoProxy>();
            var metadata = (RoutingBookInfoProxy)info;
            metadata.Result = Tuple.Create("hc:9001", new Book { MediaType = media, HardcoverBookId = "hc:7000", Author = selected,
                Editions = editionId == null ? new List<Edition>() : new List<Edition> { RoutingEdition(editionId) } }, new List<Author> { contributor, selected });
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            var imports = (AuthorLibraryProxy)library;
            imports.AddedAuthor = selected;
            var exclusions = DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>();
            var exclusion = (ImportListExclusionServiceProxy)exclusions;
            exclusion.Exclusions.AddRange(new[] { new ImportListExclusion { Id = 1, ForeignId = "hc:9001", MediaType = media },
                new ImportListExclusion { Id = 2, ForeignId = "hc:2222", MediaType = media } });
            var commandRepository = DispatchProxy.Create<ICommandRepository, CommandRepositoryProxy>();
            var commands = new CommandQueueManager(commandRepository, null, null, NLog.LogManager.GetCurrentClassLogger());
            var service = BuildService(new StubAuthorService(null), books, new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(), library, exclusions, info, commandQueue: commands);
            var request = new Book { Title = "Requested Work", MediaType = media, Author = contributor,
                HardcoverBookId = editionId == null ? "hc:7000" : null,
                Editions = editionId == null ? null : new List<Edition> { RoutingEdition(editionId) },
                AddOptions = new AddBookOptions { SearchForNewBook = true } };

            var result = await service.AddBook(request);

            Assert.Multiple(() =>
            {
                Assert.That(result.Id, Is.EqualTo(local.Id));
                Assert.That(imports.ProviderId, Is.EqualTo("hc:9001"));
                Assert.That(imports.Config.AuthorName, Is.EqualTo(selected.Name));
                Assert.That(media == BookMediaType.Audiobook ? imports.Config.AudiobookBooksToSearch : imports.Config.EbookBooksToSearch, Is.EqualTo(new[] { "hc:7000" }));
                Assert.That(imports.Config.AudiobookQualityProfileId, Is.EqualTo(3));
                Assert.That(imports.Config.EbookQualityProfileId, Is.EqualTo(5));
                Assert.That(imports.Config.AudiobookTags, Is.EquivalentTo(new[] { 7 }));
                Assert.That(imports.Config.EbookTags, Is.EquivalentTo(new[] { 8 }));
                Assert.That(media == BookMediaType.Audiobook ? imports.Config.AudiobookMonitorExistingMode : imports.Config.EbookMonitorExistingMode, Is.EqualTo(insertion));
                Assert.That(media == BookMediaType.Audiobook ? imports.Config.AudiobookMonitorNewItems : imports.Config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(request.Author, Is.SameAs(contributor));
                Assert.That(books.UpdatedBook, Is.Null);
                Assert.That(metadata.WorkReads, Is.EqualTo(editionId == null ? 1 : 0));
                Assert.That(metadata.EditionReads, Is.EqualTo(editionId == null ? 0 : 1));
                Assert.That(exclusion.DeletedIds, Does.Contain(1));
                Assert.That(exclusion.DeletedIds, Does.Not.Contain(2));
                var search = ((CommandRepositoryProxy)commandRepository).Commands.OfType<BookSearchCommand>().Single();
                Assert.That(search.BookIds, Is.EqualTo(new[] { local.Id }));
                Assert.That(search.Trigger, Is.EqualTo(CommandTrigger.Manual));
            });
        }

        [Test]
        public void primary_name_should_preserve_only_a_known_same_persona_label(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool editionLookup,
            [Values("same", "alias", "primary-alias", "different", "named")] string identity)
        {
            var selected = new Author { Name = "Selected display name", HardcoverAuthorId = identity == "different" ? "hc:2222" : "hc:9001" };
            if (identity == "alias")
            {
                selected.HardcoverAuthorId = null;
                selected.GoodreadsAuthorId = "gr:9001";
                selected.RemoteProviderIds = new HashSet<string> { "hc:9001" };
            }
            var primary = new Author { HardcoverAuthorId = "hc:9001", Name = identity == "named" ? "Metadata display name" : null };
            if (identity == "primary-alias")
            {
                selected.HardcoverAuthorId = null;
                selected.GoodreadsAuthorId = "gr:9001";
                primary.GoodreadsAuthorId = "gr:9001";
            }
            var info = DispatchProxy.Create<IProvideBookInfo, RoutingBookInfoProxy>();
            var metadata = (RoutingBookInfoProxy)info;
            metadata.Result = Tuple.Create("hc:9001", new Book { MediaType = media, HardcoverBookId = "hc:7000", Author = primary,
                Editions = new List<Edition> { RoutingEdition("gr:8888") } }, new List<Author> { selected, primary });
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            var imports = (AuthorLibraryProxy)library;
            imports.AddedAuthor = new Author { Id = -77 };
            var books = new StubBookService();
            var service = BuildService(new StubAuthorService(null), books, new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(), library,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), bookInfo: info);
            var request = new Book { MediaType = media, Author = selected,
                HardcoverBookId = editionLookup ? null : "hc:7000",
                Editions = editionLookup ? new List<Edition> { RoutingEdition("gr:8888") } : null };

            Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(request));

            Assert.Multiple(() =>
            {
                Assert.That(imports.ProviderId, Is.EqualTo("hc:9001"));
                Assert.That(imports.Config.AuthorName, Is.EqualTo(identity == "different" ? null : identity == "named" ? primary.Name : selected.Name));
                Assert.That(request.Author, Is.SameAs(selected));
                Assert.That(metadata.WorkReads, Is.EqualTo(editionLookup ? 0 : 1));
                Assert.That(metadata.EditionReads, Is.EqualTo(editionLookup ? 1 : 0));
                Assert.That(books.AddedBook, Is.Null);
            });
        }

        [TestCase("missing-book-author")]
        [TestCase("conflicting-book-author")]
        [TestCase("wrong-work")]
        [TestCase("opposite-media")]
        [TestCase("edition-only-collision")]
        public void selected_primary_requires_exact_work_media_and_selected_identity(string variant)
        {
            var selected = new Author { HardcoverAuthorId = "hc:9001" };
            var contributor = new Author { HardcoverAuthorId = "hc:2222" };
            var book = new Book { MediaType = variant == "opposite-media" ? BookMediaType.Ebook : BookMediaType.Audiobook,
                HardcoverBookId = variant is "wrong-work" or "edition-only-collision" ? "hc:7001" : "hc:7000",
                Author = variant == "missing-book-author" ? null : variant == "conflicting-book-author" ? contributor : selected,
                Editions = new List<Edition> { new() { HardcoverEditionId = "hc:7000" } } };
            var tuple = Tuple.Create("hc:9001", book, new List<Author> { contributor, selected });
            Assert.That(BookIdentity.GetRequestedWorkPrimaryAuthor(tuple, "HC:7000", BookMediaType.Audiobook),
                variant == "missing-book-author" ? Is.SameAs(selected) : Is.Null);
        }

        [Test]
        public void not_ready_native_primary_proof_preserves_existing_pending_request(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media, [Values(false, true)] bool outage)
        {
            var info = DispatchProxy.Create<IProvideBookInfo, RoutingBookInfoProxy>();
            if (outage) ((RoutingBookInfoProxy)info).Error = new BookInfoException("Injected unavailable lookup");
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = new Author { Id = -77 };
            var service = BuildService(new StubAuthorService(null), new StubBookService(), new StubEditionService(Array.Empty<Edition>()),
                new StubMetadataProfileService(), library, importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), bookInfo: info);
            Assert.ThrowsAsync<PendingBookRequestException>(() => service.AddBook(new Book { MediaType = media, HardcoverBookId = "hc:7000",
                Author = new Author { HardcoverAuthorId = "hc:2222" } }));
            Assert.That(((AuthorLibraryProxy)library).ProviderId, Is.EqualTo("hc:2222"));
            Assert.That(((AuthorLibraryProxy)library).Config.QueueIfUnavailable, Is.True);
            Assert.That(((RoutingBookInfoProxy)info).WorkReads, Is.EqualTo(1));
        }

        [TestCase(" GR:8000 ")]
        [TestCase("ol:OL8000W")]
        public void selected_primary_accepts_supported_secondary_work_and_author_aliases(string workId)
        {
            var selected = new Author { HardcoverAuthorId = "hc:9001", RemoteProviderIds = new HashSet<string> { "gr:9001" } };
            var metadata = Tuple.Create(" GR:9001 ", new Book { HardcoverBookId = "hc:7000", MediaType = BookMediaType.Ebook,
                Author = selected, RemoteProviderIds = new HashSet<string> { workId.Trim() } }, new List<Author> { selected });
            Assert.That(BookIdentity.GetRequestedWorkPrimaryAuthor(metadata, workId, BookMediaType.Ebook), Is.SameAs(selected));
        }
    }
}
