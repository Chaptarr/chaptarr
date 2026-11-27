using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfEditionPinServiceFixture
    {
        private const string Folder = "/audiobooks/Blaise Corvin/Nora Hazard/1 Mitigating Risk";

        private class Library
        {
            public List<Book> Books { get; } = new();
            public List<Author> Authors { get; } = new();
            public List<Edition> Editions { get; } = new();
            public List<BookFile> Files { get; } = new();
            public List<(Edition Edition, bool Manual)> SetMonitoredCalls { get; } = new();
            public List<Book> UpdatedBooks { get; } = new();
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public Library Library { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMediaFileService.GetFilesWithBasePath) && args.Length == 2)
                {
                    var basePath = ((string)args[0]).TrimEnd('/') + "/";
                    var mediaType = (string)args[1];
                    return Library.Files
                        .Where(f => f.Path.StartsWith(basePath, StringComparison.Ordinal))
                        .Where(f => string.IsNullOrEmpty(mediaType) || f.MediaType == mediaType)
                        .ToList();
                }

                if (targetMethod.Name == nameof(IMediaFileService.GetFilesByBook))
                {
                    var bookId = (int)args[0];
                    return Library.Files
                        .Where(f => Library.Editions.Any(e => e.Id == f.EditionId && e.BookId == bookId))
                        .ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Library Library { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case nameof(IEditionService.GetEdition):
                        return Library.Editions.FirstOrDefault(e => e.Id == (int)args[0]);
                    case nameof(IEditionService.GetEditionsByBook) when args[0] is int bookId:
                        return Library.Editions.Where(e => e.BookId == bookId).ToList();
                    case nameof(IEditionService.GetEditionsByAuthor):
                        var bookIds = Library.Books.Where(b => b.AuthorId == (int)args[0]).Select(b => b.Id).ToHashSet();
                        return Library.Editions.Where(e => bookIds.Contains(e.BookId)).ToList();
                    case nameof(IEditionService.SetMonitored):
                        var edition = (Edition)args[0];
                        var manual = (bool)args[1];
                        Library.SetMonitoredCalls.Add((edition, manual));

                        foreach (var other in Library.Editions.Where(e => e.BookId == edition.BookId))
                        {
                            other.Monitored = other.Id == edition.Id;
                            other.ManualAdd = manual && other.Id == edition.Id;
                        }

                        return Library.Editions.Where(e => e.BookId == edition.BookId).ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Library Library { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod.Name)
                {
                    case nameof(IBookService.GetBook):
                        return Library.Books.First(b => b.Id == (int)args[0]);
                    case nameof(IBookService.UpdateBook):
                        var book = (Book)args[0];
                        Library.UpdatedBooks.Add(book);

                        // Mirror BookService: a pinned book relinks its files to the monitored edition.
                        var pinned = book.Editions.Single(e => e.Monitored);
                        foreach (var file in Library.Files.Where(f => Library.Editions.Any(e => e.Id == f.EditionId && e.BookId == book.Id)))
                        {
                            file.EditionId = pinned.Id;
                        }

                        return book;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Library Library { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IAuthorService.GetAuthor) && args.Length == 1 && args[0] is int id)
                {
                    return Library.Authors.FirstOrDefault(a => a.Id == id);
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod.Name}");
            }
        }

        private Library _library;
        private AudioBookShelfEditionPinService _subject;

        [SetUp]
        public void SetUp()
        {
            _library = new Library();

            var mediaFiles = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            ((MediaFileServiceProxy)(object)mediaFiles).Library = _library;
            var editions = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editions).Library = _library;
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)books).Library = _library;
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authors).Library = _library;

            _subject = new AudioBookShelfEditionPinService(mediaFiles, editions, books, authors, LogManager.GetCurrentClassLogger());
            _library.Authors.Add(new Author { Id = 7, Tags = new HashSet<int> { 3 } });

            // Book 1 has two editions; its file is linked to the first one.
            AddBook(1, authorId: 7, "Mitigating Risk");
            AddEdition(11, bookId: 1, asin: "B000000001", monitored: true);
            AddEdition(12, bookId: 1, asin: "B07CN8FDM8");
            AddFile(101, editionId: 11, $"{Folder}/1 Mitigating Risk.m4b");

            // Book 2, same author, a different book.
            AddBook(2, authorId: 7, "Competitive Advantage");
            AddEdition(21, bookId: 2, asin: "B0COMPETE1", monitored: true);
        }

        [Test]
        public void should_pin_the_edition_with_the_audiobookshelf_asin_and_relink_the_file()
        {
            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.Pinned));
            Assert.That(_library.SetMonitoredCalls.Single().Edition.Id, Is.EqualTo(12));
            Assert.That(_library.SetMonitoredCalls.Single().Manual, Is.True);
            Assert.That(_library.UpdatedBooks.Single().AnyEditionOk, Is.False);
            Assert.That(_library.Files.Single().EditionId, Is.EqualTo(12));
        }

        [Test]
        public void should_match_an_asin_held_only_in_the_foreign_edition_id()
        {
            var edition = _library.Editions.Single(e => e.Id == 12);
            edition.Asin = null;
            edition.ForeignEditionId = "az:B07CN8FDM8-audiobook";

            var outcome = _subject.PinFolder(Folder, "audiobook", "b07cn8fdm8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.Pinned));
            Assert.That(_library.SetMonitoredCalls.Single().Edition.Id, Is.EqualTo(12));
        }

        [Test]
        public void should_keep_the_linked_edition_when_several_editions_share_the_asin()
        {
            // The linked edition already carries the ASIN; a sibling record of the same release does too.
            _library.Editions.Single(e => e.Id == 11).Asin = "B07CN8FDM8";

            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.Pinned));
            Assert.That(_library.SetMonitoredCalls.Single().Edition.Id, Is.EqualTo(11));
            Assert.That(_library.Files.Single().EditionId, Is.EqualTo(11));
        }

        [Test]
        public void should_prefer_the_audible_record_among_unlinked_editions_sharing_the_asin()
        {
            AddEdition(13, bookId: 1, asin: "B07CN8FDM8");
            _library.Editions.Single(e => e.Id == 13).ForeignEditionId = "az:B07CN8FDM8-audiobook";

            _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(_library.SetMonitoredCalls.Single().Edition.Id, Is.EqualTo(13));
        }

        [Test]
        public void should_do_nothing_when_the_edition_is_already_pinned()
        {
            _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");
            _library.SetMonitoredCalls.Clear();
            _library.UpdatedBooks.Clear();

            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.AlreadyPinned));
            Assert.That(_library.SetMonitoredCalls, Is.Empty);
            Assert.That(_library.UpdatedBooks, Is.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void should_do_nothing_without_an_asin(string asin)
        {
            var outcome = _subject.PinFolder(Folder, "audiobook", asin);

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.NoAsin));
            Assert.That(_library.SetMonitoredCalls, Is.Empty);
        }

        [Test]
        public void should_leave_the_book_alone_when_no_edition_has_the_asin()
        {
            var outcome = _subject.PinFolder(Folder, "audiobook", "B0UNKNOWN0");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.NoMatchingEdition));
            Assert.That(_library.SetMonitoredCalls, Is.Empty);
            Assert.That(_library.UpdatedBooks, Is.Empty);
        }

        [Test]
        public void should_report_but_not_move_files_when_the_asin_belongs_to_another_book()
        {
            var outcome = _subject.PinFolder(Folder, "audiobook", "B0COMPETE1");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.MatchesOtherBook));
            Assert.That(_library.SetMonitoredCalls, Is.Empty);
            Assert.That(_library.UpdatedBooks, Is.Empty);
            Assert.That(_library.Files.Single().EditionId, Is.EqualTo(11));
        }

        [Test]
        public void should_not_pin_a_folder_holding_files_of_two_books()
        {
            AddFile(102, editionId: 21, $"{Folder}/extra.m4b");

            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.AmbiguousFolder));
            Assert.That(_library.SetMonitoredCalls, Is.Empty);
        }

        [Test]
        public void should_ignore_files_of_the_other_media_type_in_the_folder()
        {
            AddFile(103, editionId: 21, $"{Folder}/Competitive Advantage.epub", mediaType: "ebook");

            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.Pinned));
        }

        [Test]
        public void should_not_pin_a_book_that_also_has_files_in_another_folder()
        {
            AddFile(104, editionId: 11, "/audiobooks/Blaise Corvin/Mitigating Risk (other copy)/part.m4b");

            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.AmbiguousFolder));
            Assert.That(_library.SetMonitoredCalls, Is.Empty);
        }

        [Test]
        public void should_not_treat_the_other_format_of_the_same_title_as_another_book()
        {
            // The ebook instance of the book carries the same ASIN.
            _library.Books.Add(new Book { Id = 3, AuthorId = 7, Title = "Mitigating Risk", MediaType = BookMediaType.Ebook, AnyEditionOk = true });
            AddEdition(31, bookId: 3, asin: "B0KINDLE01");
            _library.Books.Single(b => b.Id == 1).MediaType = BookMediaType.Audiobook;

            var outcome = _subject.PinFolder(Folder, "audiobook", "B0KINDLE01");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.NoMatchingEdition));
        }

        [TestCase(3, AudioBookShelfEditionPinOutcome.Pinned)]
        [TestCase(9, AudioBookShelfEditionPinOutcome.NotTagged)]
        public void should_honour_the_connection_tags(int connectionTag, AudioBookShelfEditionPinOutcome expected)
        {
            var outcome = _subject.PinFolder(Folder, "audiobook", "B07CN8FDM8", new HashSet<int> { connectionTag });

            Assert.That(outcome, Is.EqualTo(expected));
        }

        [Test]
        public void should_report_no_files_for_a_folder_chaptarr_does_not_track()
        {
            var outcome = _subject.PinFolder("/audiobooks/Someone Else/Unknown", "audiobook", "B07CN8FDM8");

            Assert.That(outcome, Is.EqualTo(AudioBookShelfEditionPinOutcome.NoFiles));
        }

        private void AddBook(int id, int authorId, string title)
        {
            _library.Books.Add(new Book { Id = id, AuthorId = authorId, Title = title, AnyEditionOk = true });
        }

        private void AddEdition(int id, int bookId, string asin, bool monitored = false)
        {
            _library.Editions.Add(new Edition
            {
                Id = id,
                BookId = bookId,
                Title = $"Edition {id}",
                ForeignEditionId = $"gr:{id}-audiobook",
                Asin = asin,
                Monitored = monitored
            });
        }

        private void AddFile(int id, int editionId, string path, string mediaType = "audiobook")
        {
            _library.Files.Add(new BookFile { Id = id, EditionId = editionId, Path = path, MediaType = mediaType });
        }
    }
}
