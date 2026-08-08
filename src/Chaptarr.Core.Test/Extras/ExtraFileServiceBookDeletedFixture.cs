using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.Extras
{
    /// <summary>
    /// A cover belongs to the book, not to any one of its files, so nothing reachable from a book
    /// file can clean it up. Without a book-level delete it outlives the book and keeps the folder
    /// from ever being empty.
    /// </summary>
    [TestFixture]
    public class ExtraFileServiceBookDeletedFixture
    {
        // Authored Windows-style and adapted per-OS. Production joins the author path to the
        // extra's relative path with Path.Combine, so a forward-slash literal would not match the
        // backslash-joined result on Windows.
        private static readonly string AudiobookAuthorFolder = @"C:\audiobooks\Jim Butcher".AsOsAgnostic();
        private static readonly string EbookAuthorFolder = @"C:\ebooks\Jim Butcher".AsOsAgnostic();
        private static readonly string CoverRelativePath = @"Captains Fury\cover.jpg".AsOsAgnostic();
        private static readonly string AudiobookFolder = Path.Combine(AudiobookAuthorFolder, "Captains Fury");
        private static readonly string EbookFolder = Path.Combine(EbookAuthorFolder, "Captains Fury");

        private ExtraFileRepositoryProxy _repository;
        private RecordingRecycleBinProvider _recycleBin;
        private MetadataFileService _subject;
        private Author _author;
        private Book _book;

        [SetUp]
        public void SetUp()
        {
            var repository = DispatchProxy.Create<IExtraFileRepository<MetadataFile>, ExtraFileRepositoryProxy>();
            _repository = (ExtraFileRepositoryProxy)(object)repository;
            _recycleBin = new RecordingRecycleBinProvider();

            _author = new Author
            {
                Id = 38,
                Name = "Jim Butcher",
                Path = AudiobookAuthorFolder,
                AudiobookPath = AudiobookAuthorFolder,
                EbookPath = EbookAuthorFolder
            };

            _book = new Book
            {
                Id = 5792,
                AuthorId = _author.Id,
                Author = _author,
                Title = "Captain's Fury",
                MediaType = BookMediaType.Audiobook
            };

            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = _author;

            _subject = new MetadataFileService(
                repository,
                authorService,
                DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                _recycleBin,
                LogManager.GetLogger("ExtraFileServiceBookDeletedFixture"));
        }

        [Test]
        public void should_recycle_book_scoped_extras_when_the_book_and_its_files_are_deleted()
        {
            _repository.Files.Add(BookCover());

            _subject.Handle(new BookDeletedEvent(_book, deleteFiles: true, addImportListExclusion: false));

            Assert.That(_recycleBin.DeletedFiles, Is.EqualTo(new[] { Path.Combine(AudiobookFolder, "cover.jpg") }));
            Assert.That(_repository.DeletedForBook, Is.EqualTo(new[] { (_author.Id, _book.Id) }));
        }

        [Test]
        public void should_leave_the_shared_cover_alone_when_only_one_file_of_the_book_is_removed()
        {
            // A multi-part audiobook shares one cover across every part. Removing one part must not
            // take the cover with it.
            _repository.Files.Add(BookCover());

            _subject.Handle(new BookFileDeletedEvent(
                new BookFile
                {
                    Id = 4001,
                    Path = Path.Combine(AudiobookFolder, "Captains Fury - Part 01.m4b"),
                    Author = _author
                },
                DeleteMediaFileReason.Manual));

            Assert.That(_recycleBin.DeletedFiles, Is.Empty);
            Assert.That(_repository.DeletedForBook, Is.Empty);
        }

        [Test]
        public void should_clear_rows_without_touching_disk_when_files_are_kept()
        {
            _repository.Files.Add(BookCover());

            _subject.Handle(new BookDeletedEvent(_book, deleteFiles: false, addImportListExclusion: false));

            Assert.That(_recycleBin.DeletedFiles, Is.Empty, "the user kept the files");
            Assert.That(_repository.DeletedForBook, Is.EqualTo(new[] { (_author.Id, _book.Id) }),
                "the rows point at a book that no longer exists");
        }

        [Test]
        public void should_resolve_the_extra_under_the_root_matching_the_books_media_type()
        {
            _book.MediaType = BookMediaType.Ebook;
            _repository.Files.Add(new MetadataFile
            {
                Id = 11,
                AuthorId = _author.Id,
                BookId = _book.Id,
                Type = MetadataType.BookImage,
                RelativePath = CoverRelativePath,
                Extension = ".jpg"
            });

            _subject.Handle(new BookDeletedEvent(_book, deleteFiles: true, addImportListExclusion: false));

            Assert.That(_recycleBin.DeletedFiles, Is.EqualTo(new[] { Path.Combine(EbookFolder, "cover.jpg") }));
        }

        [Test]
        public void should_do_nothing_for_a_book_that_was_never_persisted()
        {
            _subject.Handle(new BookDeletedEvent(new Book { Id = 0 }, deleteFiles: true, addImportListExclusion: false));

            Assert.That(_recycleBin.DeletedFiles, Is.Empty);
            Assert.That(_repository.DeletedForBook, Is.Empty);
        }

        private MetadataFile BookCover()
        {
            return new MetadataFile
            {
                Id = 10,
                AuthorId = _author.Id,
                BookId = _book.Id,
                Type = MetadataType.BookImage,
                RelativePath = CoverRelativePath,
                Extension = ".jpg"
            };
        }

        /// <summary>
        /// Proxy rather than a hand-written stub: IBasicRepository is wide and unrelated to what is
        /// under test here, and a proxy will not rot as it grows.
        /// </summary>
        private class ExtraFileRepositoryProxy : DispatchProxy
        {
            public List<MetadataFile> Files { get; } = new();
            public List<(int AuthorId, int BookId)> DeletedForBook { get; } = new();
            public List<int> DeletedForBookFile { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IExtraFileRepository<MetadataFile>.GetFilesByBook):
                        return Files.Where(f => f.AuthorId == (int)args[0] && f.BookId == (int)args[1]).ToList();

                    case nameof(IExtraFileRepository<MetadataFile>.GetFilesByBookFile):
                        return Files.Where(f => f.BookFileId == (int)args[0]).ToList();

                    case nameof(IExtraFileRepository<MetadataFile>.DeleteForBook):
                        DeletedForBook.Add(((int)args[0], (int)args[1]));
                        return null;

                    case nameof(IExtraFileRepository<MetadataFile>.DeleteForBookFile):
                        DeletedForBookFile.Add((int)args[0]);
                        return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IExtraFileRepository.{targetMethod?.Name}");
            }
        }

        private sealed class RecordingRecycleBinProvider : IRecycleBinProvider
        {
            public List<string> DeletedFiles { get; } = new();

            public void DeleteFile(string path, string subfolder = "") => DeletedFiles.Add(path);
            public void DeleteFolder(string path) => throw new NotImplementedException();
            public void Empty() => throw new NotImplementedException();
            public void Cleanup() => throw new NotImplementedException();
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    return true;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetParentFolder))
                {
                    // Both separators: Windows joins with '\' via Path.Combine while relative
                    // paths stored in the DB may still carry '/'.
                    var path = (string)args[0];
                    var index = path.LastIndexOfAny(new[] { '/', '\\' });
                    return index > 0 ? path.Substring(0, index) : path;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }
    }
}
