using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AddBookServiceAutoEditionSelectionFixture
    {
        [Test]
        public void every_manual_add_branch_reconciles_an_already_monitored_request(
            [Values(false, true)] bool postgres,
            [Values("existing", "scoped", "catalog")] string branch,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool sync)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((_, _) => null);
                var author = new Author
                {
                    Name = "Frank Herbert", CleanName = "frankherbert", TitleSlug = "frank-herbert",
                    HardcoverAuthorId = "hc:123456", Overview = string.Empty, Path = "/audio/Frank Herbert", Added = DateTime.UtcNow,
                    AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, AudiobookRootFolderPath = "/audio",
                    EbookQualityProfileId = 2, EbookMetadataProfileId = 2, EbookRootFolderPath = "/ebooks",
                    AudiobookMonitored = true, EbookMonitored = true, SyncMonitoredAcrossFormats = sync
                };
                new AuthorRepository(database, events, logger).Insert(author);
                var authors = new StubAuthorService(author, (_, _) => author);
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var editions = new EditionService(editionRows, events, logger);
                var books = new BookService(bookRows, editions, events, authors,
                    Proxy<IMediaFileService>((_, _) => new List<BookFile>()), null, new SeriesBookLinkRepository(database, events), null, logger);
                void AddCatalog()
                {
                    foreach (var side in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                    {
                        var book = new Book
                        {
                            AuthorId = author.Id, Author = author, Title = "Dune", CleanTitle = "dune",
                            TitleSlug = "dune-" + side, Overview = string.Empty, HardcoverBookId = "hc:456789",
                            BaseBookId = "hc:456789", MediaType = side, AnyEditionOk = true, Added = DateTime.UtcNow
                        };
                        book.SetMonitored(side == media);
                        bookRows.Insert(book);
                        editionRows.Insert(new Edition
                        {
                            BookId = book.Id, Title = book.Title, Language = "eng", Monitored = true,
                            ForeignEditionId = side == BookMediaType.Audiobook ? "hc-ed:111" : "hc-ed:222",
                            ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3
                        });
                    }
                }

                var library = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryProxy>();
                var libraryCalls = (AuthorLibraryProxy)library;
                libraryCalls.AddedAuthor = author;
                if (branch == "catalog") libraryCalls.BeforeAdd = AddCatalog;
                else AddCatalog();
                var service = BuildService(authors, books, editions, new StubMetadataProfileService(
                    new MetadataProfile { Id = 1, ProfileType = MetadataProfileType.Audiobook },
                    new MetadataProfile { Id = 2, ProfileType = MetadataProfileType.Ebook }), library,
                    importListExclusionService: new ImportListExclusionService(new ImportListExclusionRepository(database, events), logger),
                    bookInfo: Proxy<IProvideBookInfo>((_, _) => null));
                var request = new Book
                {
                    Title = "Dune", CleanTitle = "dune", Overview = string.Empty, HardcoverBookId = "hc:456789", MediaType = media,
                    Author = new Author { HardcoverAuthorId = author.HardcoverAuthorId,
                        AddOptions = branch == "scoped" ? new AddAuthorOptions { Monitor = MonitorTypes.None } : null },
                    AddOptions = new AddBookOptions { MonitorBook = true }
                };

                var result = service.AddBook(request, false).GetAwaiter().GetResult();
                var saved = bookRows.GetBooksByAuthorId(author.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved, Has.Count.EqualTo(2), "a request must reuse its existing rows");
                    Assert.That(saved.Single(book => book.MediaType == media).Id, Is.EqualTo(result.Id));
                    Assert.That(saved.Single(book => book.MediaType == media).IsMonitored(), Is.True);
                    Assert.That(saved.Single(book => book.MediaType != media).IsMonitored(), Is.EqualTo(sync));
                    Assert.That(libraryCalls.ProviderId != null, Is.EqualTo(branch == "catalog"));
                });
            });
        }
    }
}
