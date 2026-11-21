using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public partial class RefreshEditionServiceNullProviderIdFixture
    {
        private sealed class Harness
        {
            private readonly BookRepository _books;
            private readonly Author _author;
            private readonly Book _book;
            public readonly EditionService Editions;
            public readonly MediaFileRepository Files;
            public readonly RefreshEditionService RefreshEditions;
            public readonly RefreshBookService RefreshBooks;
            public readonly List<List<Edition>> TagUpdates = new();
            public readonly List<BookUpdatedEvent> BookUpdates = new();

            public Harness(IMainDatabase database, Logger logger = null)
            {
                logger ??= LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (args[0] is BookUpdatedEvent updated) BookUpdates.Add(updated);
                    return null;
                });
                _books = new BookRepository(database, events);
                Editions = new EditionService(new EditionRepository(database, events), events, logger);
                Files = new MediaFileRepository(database, events);
                _author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Refresh fixture author", CleanName = "refreshfixtureauthor", SortName = "refreshfixtureauthor",
                    TitleSlug = "refresh-fixture-author", GoodreadsAuthorId = "gr:9001",
                    Path = @"C:\Books\Refresh fixture author".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                _book = _books.Insert(new Book
                {
                    Title = "Protected edition fixture", CleanTitle = "protectededitionfixture", TitleSlug = "protected-edition-fixture",
                    GoodreadsWorkId = "gr:9002", HardcoverBookId = "hc:9002", ForeignEditionId = "gr:9003",
                    MediaType = BookMediaType.Audiobook, AnyEditionOk = true, Added = DateTime.UtcNow,
                    AuthorId = _author.Id, Author = _author
                });
                var books = Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.GetBook)) return Load();
                    if (method.Name == nameof(IBookService.UpdateMany))
                    {
                        _books.UpdateMany((List<Book>)args[0]);
                        return null;
                    }
                    throw new AssertionException("Unexpected book operation " + method.Name);
                });
                var media = Proxy<IMediaFileService>((method, args) => method.Name switch
                {
                    nameof(IMediaFileService.GetFilesByBook) => Files.GetFilesByBook((int)args[0]),
                    nameof(IMediaFileService.GetFilesByEdition) => Files.GetFilesByEdition((int)args[0]),
                    _ => throw new AssertionException("Unexpected file operation " + method.Name)
                });
                var tags = Proxy<IMetadataTagService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IMetadataTagService.SyncTags)));
                    TagUpdates.Add(((IEnumerable<Edition>)args[0]).ToList());
                    return null;
                });
                RefreshEditions = new RefreshEditionService(Editions, tags, null, database, logger);
                RefreshBooks = new RefreshBookService(books, null, null, Editions, null, null,
                    RefreshEditions, media, null, events, null, new EditionSelector(logger),
                    new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, mainDatabase: null);
            }

            public Edition Seed(string foreignId)
            {
                var edition = new Edition
                {
                    BookId = _book.Id, ForeignEditionId = foreignId, Title = _book.Title,
                    ReadingFormatId = 2, Language = "eng", Monitored = true,
                    Ratings = new Ratings { Votes = 10 }
                };
                Editions.InsertMany(new List<Edition> { edition });
                return edition;
            }

            public BookFile AddFile(Edition edition, string path = null) => Files.Insert(new BookFile
            {
                EditionId = edition.Id, Path = path ?? @"C:\Books\Protected edition fixture.m4b".AsOsAgnostic(),
                Size = 100, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
            });

            public Book Load()
            {
                var book = _books.Get(_book.Id);
                book.Author = _author;
                book.Editions = Editions.GetEditionRowsByBook(new[] { _book.Id });
                return book;
            }

            public void RefreshProtected(bool forceTags)
            {
                var remote = RefreshEntityCopy.CloneBook(_book, includeEditions: false);
                remote.Author = null;
                remote.Editions = new List<Edition>
                {
                    new() { BookId = _book.Id, ForeignEditionId = "gr:9004", Title = _book.Title,
                        ReadingFormatId = 2, Language = "eng", Ratings = new Ratings { Votes = 100 } }
                };
                RefreshBooks.RefreshBookInfo(Load(), new List<Book> { remote }, new Author(), forceTags);
            }
        }

        [Test]
        public void missing_local_provider_id_preserves_file_and_completes_refresh(
            [Values(false, true)] bool postgres, [Values(false, true)] bool updatedBucket,
            [Values(false, true)] bool forceTags, [Values(null, "", " ")] string foreignId)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var local = h.Seed(foreignId);
                var file = h.AddFile(local);
                var stored = h.Editions.GetEdition(local.Id);
                if (updatedBucket)
                {
                    stored.Title = null;
                    stored.ManualAdd = true;
                    Assert.That(h.RefreshEditions.RefreshEditionInfo(new(), new() { stored }, new(), new(), new(), new(), forceTags, new Dictionary<int, Edition>()), Is.True);
                }
                else
                {
                    h.RefreshProtected(forceTags);
                    Assert.That(h.BookUpdates, Has.Count.EqualTo(1));
                }

                var retained = h.Editions.GetEdition(local.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(retained.ForeignEditionId, Is.EqualTo(foreignId));
                    Assert.That(retained.BookId, Is.EqualTo(local.BookId));
                    Assert.That(retained.Monitored, Is.True);
                    Assert.That(retained.ManualAdd, Is.EqualTo(updatedBucket));
                    Assert.That(retained.Title, Is.EqualTo(updatedBucket ? "Unknown" : local.Title));
                    Assert.That(h.Files.Get(file.Id).EditionId, Is.EqualTo(local.Id));
                    Assert.That(h.Files.Get(file.Id).Path, Is.EqualTo(file.Path));
                    Assert.That(h.TagUpdates, Has.Count.EqualTo(1));
                    Assert.That(h.TagUpdates.Single().Any(edition => edition.Id == local.Id), Is.EqualTo(updatedBucket || forceTags));
                });
            });
        }
    }
}
