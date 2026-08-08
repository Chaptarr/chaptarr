using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class ManualImportServiceCompletionFixture
    {
        [Test]
        public void should_complete_when_imported_book_matches_grabbed_release_media_type()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook },
                new Book { Id = 11, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook });

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(10, BookMediaType.Audiobook)
                });

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_complete_manual_import_without_expected_target_when_all_selected_items_imported()
        {
            var trackedDownload = CreateTrackedDownload(new QualityModel(Quality.UnknownAudio));

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(10, BookMediaType.Audiobook)
                });

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_not_complete_manual_import_without_expected_target_when_any_selected_item_failed()
        {
            var trackedDownload = CreateTrackedDownload(new QualityModel(Quality.UnknownAudio));

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(10, BookMediaType.Audiobook),
                    CreateImportResult(11, BookMediaType.Audiobook, "Import failed")
                });

            Assert.That(result, Is.False);
        }

        [Test]
        public void should_not_complete_when_imported_book_does_not_match_expected_target()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResult(99, BookMediaType.Audiobook)
                });

            Assert.That(result, Is.False);
        }

        [Test]
        public void should_not_complete_when_preview_has_unimported_file_for_expected_book()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResultForPath(10, BookMediaType.Audiobook, "/downloads/Babbitt 01.m4b")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Babbitt 01.m4b"),
                    CreatePreviewFile(10, "/downloads/Babbitt 02.m4b")
                });

            Assert.That(result, Is.False);
        }

        [Test]
        public void should_complete_when_preview_extras_are_not_expected_books()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResultForPath(10, BookMediaType.Audiobook, "/downloads/Book A.m4b")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Book A.m4b"),
                    CreatePreviewFile(20, "/downloads/Book B.m4b"),
                    CreatePreviewFile(30, "/downloads/Book C.m4b")
                });

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_complete_ebook_when_preview_has_unimported_alternate_format_for_same_book()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.EPUB),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Ebook", MediaType = BookMediaType.Ebook });

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateImportResultForPath(10, BookMediaType.Ebook, "/downloads/Book A.epub")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Book A.epub"),
                    CreatePreviewFile(10, "/downloads/Book A.azw3")
                });

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_complete_audiobook_conversion_when_all_preview_sources_were_imported()
        {
            var trackedDownload = CreateTrackedDownload(
                new QualityModel(Quality.MP3),
                new Book { Id = 10, AuthorId = 7, Title = "Expected Audiobook", MediaType = BookMediaType.Audiobook });

            var result = ManualImportService.AreAllTrackedDownloadItemsImported(
                trackedDownload,
                new List<ImportResult>
                {
                    CreateGeneratedConversionResult(10, "/downloads/Babbitt 01.mp3", "/downloads/Babbitt 02.mp3")
                },
                new List<ManualImportFile>
                {
                    CreatePreviewFile(10, "/downloads/Babbitt 01.mp3"),
                    CreatePreviewFile(10, "/downloads/Babbitt 02.mp3")
                });

            Assert.That(result, Is.True);
        }

        [Test]
        public void folder_import_should_associate_only_the_unique_live_download_with_exact_snapshot_paths()
        {
            var matching = CreatePathTrackedDownload(
                "matching",
                TrackedDownloadState.ImportPending,
                "/downloads/Book/Disc 1.mp3",
                "/downloads/Book/Disc 2.mp3");
            var other = CreatePathTrackedDownload(
                "other",
                TrackedDownloadState.ImportPending,
                "/downloads/Other/Other.mp3");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book/Disc 1.mp3", "/downloads/Book/Disc 2.mp3" },
                new[] { matching, other },
                out var reason);

            Assert.That(result, Is.SameAs(matching));
            Assert.That(reason, Is.EqualTo("MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_UNIQUE_PATH"));
        }

        [Test]
        public void folder_import_should_not_use_output_folder_when_snapshot_disagrees()
        {
            var tracked = CreatePathTrackedDownload(
                "download-1",
                TrackedDownloadState.ImportPending,
                "/downloads/Book/Disc 1.mp3");
            tracked.DownloadItem.OutputPath = new OsPath("/downloads/Book");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book/Disc 2.mp3" },
                new[] { tracked },
                out var reason);

            Assert.That(result, Is.Null);
            Assert.That(reason, Is.EqualTo("MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_NOT_FOUND"));
        }

        [Test]
        public void folder_import_should_use_exact_output_folder_only_when_no_file_snapshot_exists()
        {
            var tracked = CreatePathTrackedDownload("download-1", TrackedDownloadState.ImportBlocked);
            tracked.DownloadItem.OutputPath = new OsPath("/downloads/Book");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book/Disc 1.mp3", "/downloads/Book/Disc 2.mp3" },
                new[] { tracked },
                out _);

            Assert.That(result, Is.SameAs(tracked));
        }

        [Test]
        public void folder_import_should_remain_unassociated_when_two_live_downloads_claim_the_paths()
        {
            var first = CreatePathTrackedDownload("first", TrackedDownloadState.ImportPending, "/downloads/shared.epub");
            var second = CreatePathTrackedDownload("second", TrackedDownloadState.Importing, "/downloads/shared.epub");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/shared.epub" },
                new[] { first, second },
                out var reason);

            Assert.That(result, Is.Null);
            Assert.That(reason, Is.EqualTo("MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_AMBIGUOUS"));
        }

        [Test]
        public void folder_import_should_not_reassociate_an_already_imported_download()
        {
            var imported = CreatePathTrackedDownload("done", TrackedDownloadState.Imported, "/downloads/Book.epub");

            var result = ManualImportService.FindUniqueTrackedDownloadBySourcePaths(
                new[] { "/downloads/Book.epub" },
                new[] { imported },
                out _);

            Assert.That(result, Is.Null);
        }

        private static TrackedDownload CreateTrackedDownload(QualityModel quality, params Book[] books)
        {
            return new TrackedDownload
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = "download-1",
                    Title = "Manual.Import.Test",
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                },
                RemoteBook = new RemoteBook
                {
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        Quality = quality
                    },
                    Books = new List<Book>(books)
                }
            };
        }

        private static TrackedDownload CreatePathTrackedDownload(string downloadId, TrackedDownloadState state, params string[] filePaths)
        {
            return new TrackedDownload
            {
                State = state,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = downloadId,
                    Title = downloadId,
                    FilePaths = filePaths.ToList(),
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 1,
                        Name = "qBittorrent",
                        Protocol = DownloadProtocol.Torrent
                    }
                }
            };
        }

        private static ImportResult CreateImportResult(int bookId, BookMediaType mediaType, params string[] errors)
        {
            return CreateImportResultForPath(bookId, mediaType, null, errors);
        }

        private static ImportResult CreateImportResultForPath(int bookId, BookMediaType mediaType, string path, params string[] errors)
        {
            var author = new Author { Id = 7, Name = "Test Author" };
            var book = new Book
            {
                Id = bookId,
                AuthorId = author.Id,
                Author = author,
                Title = $"Book {bookId}",
                MediaType = mediaType
            };

            var localBook = new LocalBook
            {
                Author = author,
                Book = book,
                Path = path
            };

            return new ImportResult(new ImportDecision<LocalBook>(localBook), errors);
        }

        private static ManualImportFile CreatePreviewFile(int bookId, string path)
        {
            return new ManualImportFile
            {
                BookId = bookId,
                Path = path,
                DownloadId = "download-1"
            };
        }

        private static ImportResult CreateGeneratedConversionResult(int bookId, params string[] sourcePaths)
        {
            var result = CreateImportResultForPath(bookId, BookMediaType.Audiobook, "/downloads/Babbitt.m4b");
            result.ImportDecision.Item.GeneratedConversionSourcePaths = sourcePaths.ToList();
            return result;
        }
    }
}
