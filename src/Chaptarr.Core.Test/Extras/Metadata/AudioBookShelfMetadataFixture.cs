using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers.AudioBookShelf;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Extras.Metadata
{
    [TestFixture]
    public class AudioBookShelfMetadataFixture
    {
        [Test]
        public void should_not_write_sidecars_by_default()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/library", FolderType = FolderType.Mixed }
            });

            var author = BuildAuthor();
            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            bookFile.Edition.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/cover.webp"));

            Assert.Multiple(() =>
            {
                Assert.That(subject.BookMetadata(author, bookFile), Is.Null);
                Assert.That(subject.BookImages(author, bookFile), Is.Empty);
            });
        }

        [Test]
        public void should_write_metadata_json_for_selected_mixed_ebook_root()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(ebookMetadata: true)
            });

            var author = BuildAuthor();
            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            bookFile.Edition.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/cover.webp"));

            var result = subject.BookMetadata(author, bookFile);
            var json = JObject.Parse(result.Contents);

            Assert.Multiple(() =>
            {
                Assert.That(result.RelativePath, Is.EqualTo("A Parade of Horribles/metadata.json"));
                Assert.That((string)json["title"], Is.EqualTo("A Parade of Horribles"));
                Assert.That(json["authors"].Select(a => (string)a), Is.EqualTo(new[] { "Matt Dinniman" }));
                Assert.That((string)json["asin"], Is.EqualTo("B0TESTASIN"));
                Assert.That((string)json["language"], Is.EqualTo("eng"));
                Assert.That(json["series"].Select(s => (string)s), Is.EqualTo(new[] { "Dungeon Crawler Carl #8" }));
                Assert.That((string)json["publishedDate"], Is.EqualTo("2026-01-02"));
                Assert.That(result.OverwriteExisting, Is.True);
                Assert.That(subject.BookImages(author, bookFile), Is.Empty);
            });
        }

        [Test]
        public void should_prefer_matched_edition_metadata_but_keep_book_series_for_metadata_json()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(ebookMetadata: true)
            });

            var author = BuildAuthor();
            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            var book = bookFile.Edition.Book;
            var edition = bookFile.Edition;

            book.Title = "Canonical Work Title";
            book.Subtitle = "Canonical Work Subtitle";
            book.Publisher = "Work Publisher";
            book.Overview = "Work overview";
            book.ISBN13 = "9780000000001";
            book.ASIN = "B0WORKASIN";
            book.LanguageCode = "work-language";
            book.PublicationYear = 2019;
            book.ReleaseDate = new DateTime(2019, 1, 1);
            book.SeriesName = "Work Series";
            book.SeriesPosition = "2";

            edition.Title = "Matched Edition Title";
            edition.Subtitle = "Matched Edition Subtitle";
            edition.Publisher = "Edition Publisher";
            edition.Overview = "Edition overview";
            edition.Isbn13 = "9780000000002";
            edition.Asin = "B0EDITIONASIN";
            edition.AudibleASIN = "B0EDITIONAUDIBLE";
            edition.Language = "edition-language";
            edition.ReleaseDate = new DateTime(2024, 5, 6);
            edition.NarratorNames = new List<string> { "Edition Narrator" };

            var result = subject.BookMetadata(author, bookFile);
            var json = JObject.Parse(result.Contents);

            Assert.Multiple(() =>
            {
                Assert.That((string)json["title"], Is.EqualTo("Matched Edition Title"));
                Assert.That((string)json["subtitle"], Is.EqualTo("Matched Edition Subtitle"));
                Assert.That((string)json["publisher"], Is.EqualTo("Edition Publisher"));
                Assert.That((string)json["description"], Is.EqualTo("Edition overview"));
                Assert.That((string)json["isbn"], Is.EqualTo("9780000000002"));
                Assert.That((string)json["asin"], Is.EqualTo("B0EDITIONAUDIBLE"));
                Assert.That((string)json["language"], Is.EqualTo("edition-language"));
                Assert.That((string)json["publishedDate"], Is.EqualTo("2024-05-06"));
                Assert.That((string)json["publishedYear"], Is.EqualTo("2024"));
                Assert.That(json["narrators"].Select(n => (string)n), Is.EqualTo(new[] { "Edition Narrator" }));
                Assert.That(json["series"].Select(s => (string)s), Is.EqualTo(new[] { "Work Series #2" }));
            });
        }

        [Test]
        public void should_not_write_ebook_sidecars_when_mixed_root_is_enabled_only_for_audiobooks()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(audiobookMetadata: true, audiobookCover: true)
            });

            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            bookFile.Edition.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/cover.webp"));

            Assert.Multiple(() =>
            {
                Assert.That(subject.BookMetadata(BuildAuthor(), bookFile), Is.Null);
                Assert.That(subject.BookImages(BuildAuthor(), bookFile), Is.Empty);
            });
        }

        [Test]
        public void should_skip_second_audio_part_to_avoid_duplicate_sidecars()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(audiobookMetadata: true, audiobookCover: true)
            });

            var bookFile = BuildBookFile("audiobook", "/library/Matt Dinniman/A Parade of Horribles/part002.mp3");
            bookFile.Part = 2;

            Assert.Multiple(() =>
            {
                Assert.That(subject.BookMetadata(BuildAuthor(), bookFile), Is.Null);
                Assert.That(subject.BookImages(BuildAuthor(), bookFile), Is.Empty);
            });
        }

        [Test]
        public void should_skip_unassigned_multipart_audio_file_to_avoid_duplicate_sidecars()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(audiobookMetadata: true)
            });

            var bookFile = BuildBookFile("audiobook", "/library/Matt Dinniman/A Parade of Horribles/part001.mp3");
            bookFile.Part = 0;
            bookFile.PartCount = 8;

            Assert.That(subject.BookMetadata(BuildAuthor(), bookFile), Is.Null);
        }

        [Test]
        public void should_write_cover_sidecar_with_supported_abs_extension()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(ebookCover: true)
            });

            var author = BuildAuthor();
            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            bookFile.Edition.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/cover.webp"));

            var result = subject.BookImages(author, bookFile);

            Assert.Multiple(() =>
            {
                Assert.That(subject.BookMetadata(author, bookFile), Is.Null);
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].RelativePath, Is.EqualTo("A Parade of Horribles/cover.webp"));
                Assert.That(result[0].Url, Is.EqualTo("https://images.example.test/cover.webp"));
                Assert.That(result[0].OverwriteExisting, Is.True);
            });
        }

        [Test]
        public void should_not_write_cover_sidecar_from_book_cover_without_matched_edition_cover()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(ebookCover: true)
            });

            var author = BuildAuthor();
            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            bookFile.Edition.Book.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/book.jpg"));

            var result = subject.BookImages(author, bookFile);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void should_write_cover_sidecar_from_matched_edition_when_book_cover_also_exists()
        {
            var subject = CreateSubject(new List<RootFolder>
            {
                BuildRoot(ebookCover: true)
            });

            var author = BuildAuthor();
            var bookFile = BuildBookFile("ebook", "/library/Matt Dinniman/A Parade of Horribles/book.epub");
            bookFile.Edition.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/edition.webp"));
            bookFile.Edition.Book.Images.Add(new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example.test/book.jpg"));

            var result = subject.BookImages(author, bookFile);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].RelativePath, Is.EqualTo("A Parade of Horribles/cover.webp"));
                Assert.That(result[0].Url, Is.EqualTo("https://images.example.test/edition.webp"));
                Assert.That(result[0].OverwriteExisting, Is.True);
            });
        }

        private static AudioBookShelfMetadata CreateSubject(List<RootFolder> rootFolders)
        {
            var subject = new AudioBookShelfMetadata(new FakeRootFolderService(rootFolders), null, null);
            subject.Definition = new MetadataDefinition
            {
                Settings = new AudioBookShelfMetadataSettings()
            };

            return subject;
        }

        private static RootFolder BuildRoot(bool audiobookMetadata = false, bool audiobookCover = false, bool ebookMetadata = false, bool ebookCover = false)
        {
            var rootFolder = new RootFolder { Id = 1, Path = "/library", FolderType = FolderType.Mixed };
            rootFolder.SetAudiobookSettings(new MediaTypeSettings
            {
                WriteAudioBookShelfMetadataJson = audiobookMetadata,
                WriteAudioBookShelfCover = audiobookCover
            });
            rootFolder.SetEbookSettings(new MediaTypeSettings
            {
                WriteAudioBookShelfMetadataJson = ebookMetadata,
                WriteAudioBookShelfCover = ebookCover
            });

            return rootFolder;
        }

        private static Author BuildAuthor()
        {
            return new Author
            {
                Id = 10,
                Name = "Matt Dinniman",
                Path = "/library/Matt Dinniman",
                AudiobookPath = "/library/Matt Dinniman",
                EbookPath = "/library/Matt Dinniman"
            };
        }

        private static BookFile BuildBookFile(string mediaType, string path)
        {
            var book = new Book
            {
                Id = 20,
                Title = "A Parade of Horribles",
                ASIN = "B0TESTASIN",
                LanguageCode = "eng",
                Publisher = "Ace",
                PublicationYear = 2026,
                ReleaseDate = new DateTime(2026, 1, 2),
                SeriesName = "Dungeon Crawler Carl",
                SeriesPosition = "8"
            };

            var edition = new Edition
            {
                Id = 30,
                BookId = book.Id,
                Title = "A Parade of Horribles",
                ReleaseDate = new DateTime(2026, 1, 2),
                Book = book
            };

            return new BookFile
            {
                Id = 40,
                EditionId = edition.Id,
                Edition = edition,
                MediaType = mediaType,
                Path = path,
                Part = 1
            };
        }

        private class FakeRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public FakeRootFolderService(List<RootFolder> rootFolders)
            {
                _rootFolders = rootFolders;
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => _rootFolders;
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => _rootFolders.First(r => r.Id == id);
            public List<RootFolder> AllForTag(int tagId) => _rootFolders;
            public RootFolder GetBestRootFolder(string path) => GetBestRootFolder(path, _rootFolders);

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return allRootFolders
                    .Where(r => path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();
            }

            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path, allRootFolders)?.Path;
        }
    }
}
