using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Extras
{
    [TestFixture]
    public class ExtraFilePathHelperFixture
    {
        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_select_base_path_by_actual_file_location()
        {
            var author = new Author
            {
                Id = 1,
                Path = "/media/Audiobooks/Author",
                AudiobookPath = "/media/Audiobooks/Author",
                EbookPath = "/media/Ebooks/Author"
            };

            var fullPath = "/media/Ebooks/Author/Book/file.nfo";

            ExtraFilePathHelper.TryGetRelativePath(author, fullPath, out var relativePath, out var basePath);

            Assert.That(basePath, Is.EqualTo("/media/Ebooks/Author"));
            Assert.That(relativePath, Is.EqualTo("Book/file.nfo"));
        }

        [Test]
        public void should_normalize_stored_relative_paths_to_forward_slashes()
        {
            var relativePath = ExtraFilePathHelper.NormalizePathSeparators(@"Book\cover.webp");

            Assert.That(relativePath, Is.EqualTo("Book/cover.webp"));
        }

        [Test]
        public void should_prefer_matching_parent_path_even_when_media_type_differs()
        {
            var author = new Author
            {
                Id = 1,
                Path = "/media/Audiobooks/Author",
                AudiobookPath = "/media/Audiobooks/Author",
                EbookPath = "/media/Ebooks/Author"
            };

            var bookFile = new BookFile
            {
                Path = "/media/Audiobooks/Author/Book/book.epub",
                MediaType = "ebook"
            };

            var preferred = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);

            Assert.That(preferred, Is.EqualTo("/media/Audiobooks/Author"));
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_resolve_existing_full_path_across_multiple_author_bases()
        {
            var author = new Author
            {
                Id = 1,
                Path = "/media/Audiobooks/Author",
                AudiobookPath = "/media/Audiobooks/Author",
                EbookPath = "/media/Ebooks/Author"
            };

            var relative = "Book/file.srt";
            var expected = "/media/Ebooks/Author/Book/file.srt";

            var resolved = ExtraFilePathHelper.ResolveFullPath(author, relative, p => string.Equals(p, expected, StringComparison.Ordinal));

            Assert.That(resolved, Is.EqualTo(expected));
        }

        [Test]
        public void filter_and_clean_should_match_previously_imported_files_across_bases()
        {
            var author = new Author
            {
                Id = 1,
                Path = "/media/Audiobooks/Author",
                AudiobookPath = "/media/Audiobooks/Author",
                EbookPath = "/media/Ebooks/Author"
            };

            var authorFiles = new List<OtherExtraFile>
            {
                new OtherExtraFile
                {
                    Id = 10,
                    AuthorId = 1,
                    BookId = 1,
                    BookFileId = 1,
                    RelativePath = "Book/file.srt",
                    Extension = ".srt"
                }
            };

            var service = new StubExtraFileService(authorFiles);
            var importer = new TestImporter(service);

            var filesOnDisk = new List<string> { "/media/Ebooks/Author/Book/file.srt" };
            var importedFiles = new List<string>();

            var result = importer.FilterAndClean(author, filesOnDisk, importedFiles);

            Assert.That(result.PreviouslyImported, Has.Count.EqualTo(1));
            Assert.That(result.FilesOnDisk, Is.Empty);
            Assert.That(service.DeletedIds, Is.Empty);
        }

        private sealed class TestImporter : ImportExistingExtraFilesBase<OtherExtraFile>
        {
            public TestImporter(IExtraFileService<OtherExtraFile> extraFileService)
                : base(extraFileService)
            {
            }

            public override int Order => 0;

            public override IEnumerable<ExtraFile> ProcessFiles(Author author, List<string> filesOnDisk, List<string> importedFiles)
            {
                return Enumerable.Empty<ExtraFile>();
            }
        }

        private sealed class StubExtraFileService : IExtraFileService<OtherExtraFile>
        {
            private readonly List<OtherExtraFile> _files;

            public StubExtraFileService(List<OtherExtraFile> files)
            {
                _files = files;
            }

            public List<int> DeletedIds { get; } = new List<int>();

            public List<OtherExtraFile> GetFilesByAuthor(int authorId)
            {
                return _files.Where(f => f.AuthorId == authorId).ToList();
            }

            public List<OtherExtraFile> GetFilesByBookFile(int bookFileId)
            {
                throw new NotImplementedException();
            }

            public OtherExtraFile FindByPath(int authorId, string path)
            {
                throw new NotImplementedException();
            }

            public void Upsert(OtherExtraFile extraFile)
            {
                throw new NotImplementedException();
            }

            public void Upsert(List<OtherExtraFile> extraFiles)
            {
                throw new NotImplementedException();
            }

            public void Delete(int id)
            {
                throw new NotImplementedException();
            }

            public void DeleteMany(IEnumerable<int> ids)
            {
                DeletedIds.AddRange(ids);
            }

            public List<OtherExtraFile> GetFilesByBook(int authorId, int bookId)
            {
                throw new NotImplementedException();
            }
        }
    }
}
