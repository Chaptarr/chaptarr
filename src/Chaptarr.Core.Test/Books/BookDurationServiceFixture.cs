using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookDurationServiceFixture
    {
        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }
            public List<Book> AllBooks { get; set; } = new List<Book>();
            public readonly List<Book> UpdatedBooks = new List<Book>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book;
                }

                if (targetMethod?.Name == nameof(IBookService.GetAllBooks))
                {
                    return AllBooks;
                }

                if (targetMethod?.Name == nameof(IBookService.UpdateBook))
                {
                    var book = (Book)args[0];
                    UpdatedBooks.Add(book);
                    return book;
                }

                throw new NotImplementedException($"Unexpected call to IBookService.{targetMethod?.Name}");
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new List<BookFile>();
            public readonly List<int> GetFilesByBookCalls = new List<int>();
            public readonly List<List<int>> GetFilesByBooksCalls = new List<List<int>>();
            public readonly List<BookFile> UpdatedFiles = new List<BookFile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.GetFilesByBook))
                {
                    GetFilesByBookCalls.Add((int)args[0]);
                    return Files;
                }

                if (targetMethod?.Name == nameof(IMediaFileService.GetFilesByBooks))
                {
                    GetFilesByBooksCalls.Add(((List<int>)args[0]).ToList());
                    return Files;
                }

                if (targetMethod?.Name == nameof(IMediaFileService.Update) &&
                    args.Length == 1 &&
                    args[0] is List<BookFile> files)
                {
                    UpdatedFiles.AddRange(files);
                    return null;
                }

                throw new NotImplementedException($"Unexpected call to IMediaFileService.{targetMethod?.Name}");
            }
        }

        private class MediaInfoExtractorProxy : DispatchProxy
        {
            public int ExtractCalls { get; private set; }
            public TimeSpan ExtractedDuration { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaInfoExtractor.ExtractMediaInfo))
                {
                    ExtractCalls++;
                    return new MediaInfoModel { Duration = ExtractedDuration };
                }

                throw new NotImplementedException($"Unexpected call to IMediaInfoExtractor.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_use_persisted_duration_seconds_without_extracting_media_info()
        {
            var book = new Book { Id = 10, Title = "Stored Duration", MediaType = BookMediaType.Audiobook };
            var files = new List<BookFile>
            {
                new BookFile { Path = "/books/a.m4b", DurationSeconds = 3600, MediaInfo = new MediaInfoModel() },
                new BookFile { Path = "/books/b.m4b", DurationSeconds = 5400, MediaInfo = new MediaInfoModel() }
            };

            var (service, bookProxy, mediaProxy, extractorProxy) = CreateService(book, files);

            service.UpdateBookDuration(book);

            Assert.That(extractorProxy.ExtractCalls, Is.EqualTo(0));
            Assert.That(mediaProxy.UpdatedFiles, Is.Empty);
            Assert.That(book.DurationMinutes, Is.EqualTo(150));
            Assert.That(bookProxy.UpdatedBooks, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_backfill_missing_duration_seconds_once()
        {
            var book = new Book { Id = 11, Title = "Legacy Duration", MediaType = BookMediaType.Audiobook };
            var file = new BookFile { Path = "/books/legacy.m4b", MediaInfo = new MediaInfoModel() };

            var (service, _, mediaProxy, extractorProxy) = CreateService(book, new List<BookFile> { file });
            extractorProxy.ExtractedDuration = TimeSpan.FromHours(2);

            service.UpdateBookDuration(book);

            Assert.That(extractorProxy.ExtractCalls, Is.EqualTo(1));
            Assert.That(file.DurationSeconds, Is.EqualTo(7200));
            Assert.That(mediaProxy.UpdatedFiles, Is.EqualTo(new[] { file }));
            Assert.That(book.DurationMinutes, Is.EqualTo(120));
        }

        [Test]
        public void should_bulk_load_files_when_repairing_all_book_durations()
        {
            var firstBook = new Book { Id = 10, Title = "First", MediaType = BookMediaType.Audiobook };
            var secondBook = new Book { Id = 11, Title = "Second", MediaType = BookMediaType.Audiobook };
            var files = new List<BookFile>
            {
                new BookFile { Path = "/books/first.m4b", DurationSeconds = 3600, Edition = new Edition { BookId = firstBook.Id } },
                new BookFile { Path = "/books/second.m4b", DurationSeconds = 7200, Edition = new Edition { BookId = secondBook.Id } }
            };

            var (service, bookProxy, mediaProxy, _) = CreateService(firstBook, files);
            bookProxy.AllBooks = new List<Book> { firstBook, secondBook };

            service.UpdateAllBookDurations();

            Assert.That(mediaProxy.GetFilesByBooksCalls, Has.Count.EqualTo(1));
            Assert.That(mediaProxy.GetFilesByBooksCalls[0], Is.EquivalentTo(new[] { firstBook.Id, secondBook.Id }));
            Assert.That(mediaProxy.GetFilesByBookCalls, Is.Empty);
            Assert.That(firstBook.DurationMinutes, Is.EqualTo(60));
            Assert.That(secondBook.DurationMinutes, Is.EqualTo(120));
            Assert.That(bookProxy.UpdatedBooks.Select(book => book.Id), Is.EquivalentTo(new[] { firstBook.Id, secondBook.Id }));
        }

        [Test]
        public void should_ignore_partial_file_sets_from_event_callers()
        {
            var book = new Book { Id = 12, Title = "Multipart", MediaType = BookMediaType.Audiobook };
            var firstFile = new BookFile { Path = "/books/part01.mp3", DurationSeconds = 1800, MediaInfo = new MediaInfoModel() };
            var secondFile = new BookFile { Path = "/books/part02.mp3", DurationSeconds = 3600, MediaInfo = new MediaInfoModel() };

            var (service, _, mediaProxy, _) = CreateService(book, new List<BookFile> { firstFile, secondFile });

            service.UpdateBookDuration(book, new List<BookFile> { firstFile });

            Assert.That(mediaProxy.GetFilesByBookCalls, Is.EqualTo(new[] { book.Id }));
            Assert.That(book.DurationMinutes, Is.EqualTo(90));
        }

        private static (BookDurationService Service,
                        BookServiceProxy BookProxy,
                        MediaFileServiceProxy MediaProxy,
                        MediaInfoExtractorProxy ExtractorProxy) CreateService(Book book, List<BookFile> files)
        {
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var bookProxy = (BookServiceProxy)(object)bookService;
            bookProxy.Book = book;

            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            var mediaProxy = (MediaFileServiceProxy)(object)mediaFileService;
            mediaProxy.Files = files;

            var extractor = DispatchProxy.Create<IMediaInfoExtractor, MediaInfoExtractorProxy>();
            var extractorProxy = (MediaInfoExtractorProxy)(object)extractor;

            var service = new BookDurationService(
                bookService,
                mediaFileService,
                extractor,
                LogManager.GetLogger("test"));

            return (service, bookProxy, mediaProxy, extractorProxy);
        }
    }
}
