using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    // BookService.Handle(AuthorDeletedEvent) used to always publish BookDeletedEvent with
    // deleteFiles hardcoded to false, regardless of what the author-level delete actually
    // requested - so MediaFileService never deleted BookFile rows for an author's books, only
    // unlinked them, leaving them behind as "unmapped files" even when the physical files really
    // were deleted (see MediaFileDeletionServiceFixture's dual-path test for the other half of
    // this bug).
    [TestFixture]
    public class BookServiceAuthorDeletedFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class EmptyLinksSeriesBookLinkRepositoryProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, nameof(ISeriesBookLinkRepository.GetLinksByBook), StringComparison.Ordinal))
                {
                    return new List<SeriesBookLink>();
                }

                throw new NotImplementedException($"Test proxy does not implement ISeriesBookLinkRepository.{targetMethod?.Name}");
            }
        }

        private class RecordingBookRepositoryProxy : DispatchProxy
        {
            public List<Book> BooksByAuthor { get; set; } = new();
            public List<Book> DeletedBooks { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, nameof(IBookRepository.GetBooksByAuthorId), StringComparison.Ordinal))
                {
                    return BooksByAuthor;
                }

                if (string.Equals(targetMethod?.Name, "DeleteMany", StringComparison.Ordinal) &&
                    args?.Length == 1 &&
                    args[0] is IEnumerable<Book> books)
                {
                    DeletedBooks.AddRange(books);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookRepository.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_pass_the_authors_delete_files_flag_through_to_each_books_delete_event()
        {
            var eventAggregator = new RecordingEventAggregator();
            var bookRepository = DispatchProxy.Create<IBookRepository, RecordingBookRepositoryProxy>();
            var bookRepoProxy = (RecordingBookRepositoryProxy)(object)bookRepository;

            var author = new Author { Id = 7, Name = "Jim Butcher" };
            var book = new Book { Id = 42, AuthorId = author.Id, Author = author, Title = "Storm Front" };
            bookRepoProxy.BooksByAuthor = new List<Book> { book };

            var service = new BookService(
                bookRepository,
                null, // editionService
                eventAggregator,
                null, // authorService
                null, // mediaFileService
                null, // rootFolderService
                DispatchProxy.Create<ISeriesBookLinkRepository, EmptyLinksSeriesBookLinkRepositoryProxy>(),
                null, // multiCopySeriesService
                LogManager.GetCurrentClassLogger());

            service.Handle(new AuthorDeletedEvent(author, deleteFiles: true, addImportListExclusion: false));

            var published = eventAggregator.Events.OfType<BookDeletedEvent>().Single();
            Assert.That(published.DeleteFiles, Is.True);
            Assert.That(bookRepoProxy.DeletedBooks.Select(b => b.Id), Does.Contain(book.Id));

            // MediaFileDeletionService's own AuthorDeletedEvent handler already recursively deletes
            // the author's whole folder(s) - a per-book disk delete here would just race it.
            Assert.That(published.SkipDiskCleanup, Is.True);
        }

        [Test]
        public void should_not_delete_files_when_the_author_delete_did_not_request_it()
        {
            var eventAggregator = new RecordingEventAggregator();
            var bookRepository = DispatchProxy.Create<IBookRepository, RecordingBookRepositoryProxy>();
            var bookRepoProxy = (RecordingBookRepositoryProxy)(object)bookRepository;

            var author = new Author { Id = 7, Name = "Jim Butcher" };
            var book = new Book { Id = 42, AuthorId = author.Id, Author = author, Title = "Storm Front" };
            bookRepoProxy.BooksByAuthor = new List<Book> { book };

            var service = new BookService(
                bookRepository,
                null, // editionService
                eventAggregator,
                null, // authorService
                null, // mediaFileService
                null, // rootFolderService
                DispatchProxy.Create<ISeriesBookLinkRepository, EmptyLinksSeriesBookLinkRepositoryProxy>(),
                null, // multiCopySeriesService
                LogManager.GetCurrentClassLogger());

            service.Handle(new AuthorDeletedEvent(author, deleteFiles: false, addImportListExclusion: false));

            var published = eventAggregator.Events.OfType<BookDeletedEvent>().Single();
            Assert.That(published.DeleteFiles, Is.False);
        }
    }
}
