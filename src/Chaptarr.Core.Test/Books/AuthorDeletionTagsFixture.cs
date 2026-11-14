using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorDeletionTagsFixture
    {
        [Test]
        public void deleting_one_format_preserves_only_surviving_tags_in_storage_api_and_notifications(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType removedMedia,
            [Values(false, true)] bool retainedTagsEmpty,
            [Values(false, true)] bool bulk,
            [Values(false, true)] bool explicitFormatPaths)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                EditionService editionService = null;
                var edited = new List<Author>();
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
                {
                    if (args[0] is AuthorEditedEvent edit) edited.Add(edit.Author);
                    return null;
                });
                var authors = new AuthorRepository(database, events, logger);
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                editionService = new EditionService(editions, events, logger);
                var bookService = new BookService(books, editionService, events, null, null, null, null, null, logger);
                var service = new AuthorService(authors, events, null, null, null, new CacheManager(), books, null,
                    logger, null, null, new Lazy<IBookService>(() => bookService), database);
                var selected = new List<Author>();
                for (var index = 0; index < (bulk ? 2 : 1); index++)
                {
                    var author = authors.Insert(new Author
                    {
                        Name = "Author " + index, CleanName = "author" + index, TitleSlug = "author-" + index,
                        Path = "fixture", Added = DateTime.UtcNow,
                        AudiobookPath = explicitFormatPaths ? "audio-fixture" : null,
                        EbookPath = explicitFormatPaths ? "ebook-fixture" : null,
                        Tags = new HashSet<int> { 10, 20, 30 },
                        AudiobookTags = new HashSet<int> { 10, 30 },
                        EbookTags = new HashSet<int> { 20, 30 }
                    });
                    if (retainedTagsEmpty)
                    {
                        if (removedMedia == BookMediaType.Audiobook) author.EbookTags.Clear();
                        else author.AudiobookTags.Clear();
                        author.Tags = removedMedia == BookMediaType.Audiobook ? author.AudiobookTags : author.EbookTags;
                        authors.Update(author);
                    }

                    selected.Add(author);
                    foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
                    {
                        books.Insert(new Book
                        {
                            AuthorId = author.Id, Title = "Book", CleanTitle = "book", TitleSlug = "book-" + index + "-" + media,
                            MediaType = media, Added = DateTime.UtcNow
                        });
                    }
                }

                service.GetAllAuthors();
                service.DeleteAuthors(selected.Select(author => author.Id).ToList(), false, mediaType: removedMedia);

                var expected = retainedTagsEmpty ? Array.Empty<int>()
                    : removedMedia == BookMediaType.Audiobook ? new[] { 20, 30 } : new[] { 10, 30 };
                var retainedMedia = removedMedia == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                foreach (var author in selected)
                {
                    var stored = authors.Get(author.Id);
                    var resource = stored.ToResource();
                    var retainedBook = books.GetBooksByAuthorId(author.Id).Single();
                    retainedBook.Author = stored;
                    Assert.Multiple(() =>
                    {
                        Assert.That(stored.Path, Is.EqualTo(explicitFormatPaths
                            ? retainedMedia == BookMediaType.Audiobook ? "audio-fixture" : "ebook-fixture"
                            : "fixture"), "Use the surviving format path, retaining the legacy path when no separate path exists");
                        Assert.That(stored.Tags, Is.EquivalentTo(expected), "persisted aggregate");
                        Assert.That(stored.GetTagsForMediaType(removedMedia), Is.Empty);
                        Assert.That(stored.GetTagsForMediaType(retainedMedia), Is.EquivalentTo(expected));
                        Assert.That(resource.Tags, Is.EquivalentTo(expected), "displayed aggregate");
                        Assert.That(edited.Single(item => item.Id == author.Id).Tags, Is.EquivalentTo(expected));
                        Assert.That(retainedBook.MediaType, Is.EqualTo(retainedMedia));
                        foreach (var tag in new[] { 10, 20, 30 })
                        {
                            Assert.That(service.AllForTag(tag).Any(item => item.Id == author.Id), Is.EqualTo(expected.Contains(tag)), "tag lookup: " + tag);
                        }

                        Assert.That(NotifiedTags(retainedBook), Is.EquivalentTo(expected), "notifications after deletion");
                    });
                }
            });
        }

        private static List<int> NotifiedTags(Book book)
        {
            var sent = new List<int>();
            var notifications = new[] { 10, 20, 30 }.Select(tag => InterfaceProxy.Proxy<INotification>((method, args) =>
            {
                if (method.Name == "get_HasPendingQueue") return false;
                if (method.Name == "get_Definition") return new NotificationDefinition { Id = tag, Tags = new HashSet<int> { tag } };
                if (method.Name == nameof(INotification.OnBookAdded)) { sent.Add(tag); return null; }
                throw new NotImplementedException(method.Name);
            })).ToList();
            var factory = InterfaceProxy.Proxy<INotificationFactory>((method, args) =>
                method.Name == nameof(INotificationFactory.OnBookAddedEnabled) ? notifications : throw new NotImplementedException(method.Name));
            var status = InterfaceProxy.Proxy<INotificationStatusService>((method, args) =>
                method.Name == nameof(INotificationStatusService.RecordSuccess) ? null : throw new NotImplementedException(method.Name));
            new NotificationService(factory, status, null, LogManager.GetCurrentClassLogger()).Handle(new BookAddedEvent(book));
            return sent;
        }
    }
}
