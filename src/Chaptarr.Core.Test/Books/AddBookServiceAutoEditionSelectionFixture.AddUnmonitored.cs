using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AddBookServiceAutoEditionSelectionFixture
    {
        [Test]
        public async Task explicit_add_without_monitoring_keeps_the_book_unmonitored(
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool alreadyInCatalog,
            [Values(false, true)] bool search)
        {
            var author = new Author { Id = 7, Name = "Frank Herbert", HardcoverAuthorId = "hc:1095145",
                AudiobookQualityProfileId = 1, AudiobookRootFolderPath = "/audio",
                EbookQualityProfileId = 2, EbookRootFolderPath = "/ebooks", AudiobookMonitored = false, EbookMonitored = false };
            var local = new Book { Id = 21, AuthorId = author.Id, Author = author,
                HardcoverBookId = "hc:7000", MediaType = media, Title = "Dune" };
            local.SetMonitored(true);
            var books = new StubBookService { AuthorBooks = new List<Book> { local }, FindExistingBook = alreadyInCatalog };
            var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
            ((AuthorLibraryProxy)library).AddedAuthor = author;
            var commands = new List<Command>();
            var queue = Proxy<IManageCommandQueue>((_, args) => { commands.Add((Command)args[0]); return new CommandModel(); });
            var service = BuildService(new StubAuthorService(author, (_, _) => author), books,
                new StubEditionService(Array.Empty<Edition>()), new StubMetadataProfileService(), library,
                importListExclusionService: DispatchProxy.Create<IImportListExclusionService, ImportListExclusionServiceProxy>(), commandQueue: queue);
            var request = new Book { Title = local.Title, HardcoverBookId = local.HardcoverBookId, MediaType = media,
                Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId, AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.None } },
                AddOptions = new AddBookOptions { SearchForNewBook = search, MonitorBook = false } };

            var result = await service.AddBook(request);

            Assert.Multiple(() =>
            {
                Assert.That(result.Id, Is.EqualTo(local.Id));
                Assert.That(result.IsMonitored(), Is.False);
                Assert.That(result.AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                Assert.That(commands.FindAll(command => command is BookSearchCommand).Count, Is.EqualTo(search ? 1 : 0));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
            });
        }
    }
}
