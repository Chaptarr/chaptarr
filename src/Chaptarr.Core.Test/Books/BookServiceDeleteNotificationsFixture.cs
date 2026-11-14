using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;
using Chaptarr.Core.Test.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Synology;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceDeleteNotificationsFixture
    {
        private sealed class Events : IEventAggregator
        {
            public Action<IEvent> OnPublished { get; set; }
            public void PublishEvent<T>(T message) where T : class, IEvent => OnPublished?.Invoke(message);
        }

        [Test]
        public void completed_file_result_survives_bulk_teardown_without_deleting_the_other_format(
            [Values(false, true)] bool postgres,
            [Values(false, true)] bool filesAlreadyDeleted,
            [Values(false, true)] bool authorEvent)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new Events();
                var logger = LogManager.GetCurrentClassLogger();
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Author", CleanName = "author", TitleSlug = "author",
                    HardcoverAuthorId = "501", Path = "fixture", Added = DateTime.UtcNow
                });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var selected = books.Insert(new Book
                {
                    AuthorId = author.Id, Author = author, Title = "Book", CleanTitle = "book", TitleSlug = "ebook",
                    HardcoverBookId = "601", MediaType = BookMediaType.Ebook, Added = DateTime.UtcNow
                });
                var retainedAuthor = authorEvent ? new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Other", CleanName = "other", TitleSlug = "other", Path = "other", Added = DateTime.UtcNow
                }) : author;
                var retained = books.Insert(new Book
                {
                    AuthorId = retainedAuthor.Id, Author = retainedAuthor, Title = "Book", CleanTitle = "book", TitleSlug = "audio",
                    HardcoverBookId = "601", MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                });
                var selectedEdition = editions.Insert(new Edition { BookId = selected.Id, Title = "Book", ForeignEditionId = "hc:edition:701" });
                var retainedEdition = editions.Insert(new Edition { BookId = retained.Id, Title = "Book", ForeignEditionId = "hc:edition:702" });
                var editionService = new EditionService(editions, events, logger);
                var files = new MediaFileRepository(database, events);
                var file = files.Insert(new BookFile
                {
                    EditionId = selectedEdition.Id, Path = @"C:\fixture\Author\Book.epub".AsOsAgnostic(), Quality = new QualityModel(),
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, MediaInfo = new MediaInfoModel()
                });
                var snapshot = filesAlreadyDeleted ? files.GetFilesByBook(selected.Id) : null;
                if (filesAlreadyDeleted) files.Delete(file.Id);
                var fileQueries = 0;
                var media = AuthorDeletionSafetyFixture.Proxy<IMediaFileService>((method, args) =>
                {
                    if (method.Name != nameof(IMediaFileService.GetFilesByBooks)) throw new NotImplementedException(method.Name);
                    fileQueries++;
                    return files.GetFilesByBooks((List<int>)args[0]);
                });
                var deleted = new List<BookDeletedEvent>();
                var indexedDeletions = new List<string>();
                var indexer = new SynologyIndexer(AuthorDeletionSafetyFixture.Proxy<ISynologyIndexerProxy>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(ISynologyIndexerProxy.DeleteFile)));
                    indexedDeletions.Add((string)args[0]);
                    return null;
                }), AuthorDeletionSafetyFixture.Proxy<IDiskProvider>((_, _) => false), new CacheManager(), new Chaptarr.Core.Test.Common.MemoryPendingNotifications())
                {
                    Definition = new NotificationDefinition { Settings = new SynologyIndexerSettings { UpdateLibrary = true } }
                };
                events.OnPublished = message =>
                {
                    if (message is BookDeletedEvent bookDeleted)
                    {
                        Assert.That(books.Find(bookDeleted.Book.Id), Is.Null);
                        deleted.Add(bookDeleted);
                        indexer.OnBookDelete(new BookDeleteMessage(bookDeleted.Book, bookDeleted.FilesAlreadyDeleted));
                    }
                };
                var service = new BookService(books, editionService, events, null, media, null, new SeriesBookLinkRepository(database, events), null, logger);

                if (authorEvent)
                {
                    service.Handle(new AuthorDeletedEvent(author, filesAlreadyDeleted, false, deletedBookFiles: snapshot));
                }
                else
                {
                    service.DeleteMany(new List<Book> { selected, selected }, filesAlreadyDeleted, snapshot);
                }

                Assert.That(indexedDeletions, Is.Empty);
                indexer.ProcessQueue();

                Assert.Multiple(() =>
                {
                    Assert.That(deleted, Has.Count.EqualTo(1));
                    Assert.That(indexedDeletions, Is.EqualTo(filesAlreadyDeleted ? new[] { file.Path } : Array.Empty<string>()));
                    Assert.That(deleted.Single().Book.BookFiles.Select(f => f.Path), Is.EqualTo(new[] { file.Path }));
                    Assert.That(deleted.Single().Book.Editions.Single().BookFiles.Select(f => f.Path), Is.EqualTo(new[] { file.Path }));
                    Assert.That(fileQueries, Is.EqualTo(filesAlreadyDeleted ? 0 : 1));
                    Assert.That(deleted.Single().FilesAlreadyDeleted, Is.EqualTo(filesAlreadyDeleted));
                    Assert.That(deleted.Single().DeleteFiles, Is.False);
                    Assert.That(books.Find(selected.Id), Is.Null);
                    Assert.That(editions.Find(selectedEdition.Id), Is.Null);
                    Assert.That(books.Find(retained.Id), Is.Not.Null);
                    Assert.That(editions.Find(retainedEdition.Id), Is.Not.Null);
                });
            });
        }
    }
}
