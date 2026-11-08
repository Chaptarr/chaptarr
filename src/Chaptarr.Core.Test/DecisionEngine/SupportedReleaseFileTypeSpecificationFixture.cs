using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class SupportedReleaseFileTypeSpecificationFixture
    {
        private static SupportedReleaseFileTypeSpecification CreateSubject()
        {
            return new SupportedReleaseFileTypeSpecification(LogManager.GetCurrentClassLogger());
        }

        [TestCase("cbr")]
        [TestCase("cbz")]
        [TestCase("djvu")]
        [TestCase("mkv")]
        public void should_reject_known_unsupported_indexer_file_types(string fileType)
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook(fileType), null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Format"));
            Assert.That(decision.Reason, Is.EqualTo($"Unsupported file type: {fileType}"));
        }

        [TestCase("rar")]
        [TestCase("zip")]
        [TestCase("7z")]
        [TestCase("tar")]
        [TestCase("iso")]
        public void should_not_reject_archive_container_file_types_as_known_incompatible_content(string fileType)
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook(fileType), null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_sons_of_ares_cbr_before_download()
        {
            var remoteBook = BuildRemoteBook(
                "cbr",
                "Pierce Brown's Red Rising - Sons of Ares v02 - Wrath (2020) (digital) (Son of Ultron-Empire)");

            var decision = CreateSubject().IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("Unsupported file type: cbr"));
        }

        [TestCase("The Hitchhiker's Guide to the Galaxy 2005 1080p BluRay x264.mkv", "mkv")]
        [TestCase("Example Release.avi", "avi")]
        [TestCase("Example Release.webm", "webm")]
        [TestCase("Free.Comic.Book.Day.2025.cbz-xpost", "cbz")]
        [TestCase("10.HTML_and_CSS_overview", "html")]
        public void should_reject_known_unsupported_title_file_extensions_when_indexer_file_type_is_missing(string title, string fileType)
        {
            var remoteBook = BuildRemoteBook(null, title);

            var decision = CreateSubject().IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Format"));
            Assert.That(decision.Reason, Is.EqualTo($"Unsupported file type: {fileType}"));
        }

        [TestCase("Book.cbz𠀀", true)]
        [TestCase("Book.cbz村", true)]
        [TestCase("Book.cbz 🌶️", false)]
        [TestCase("Book.cbz", false)]
        public void title_extension_boundary_should_classify_whole_unicode_characters(string title, bool accepted)
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook(null, title), null);
            Assert.That(decision.Accepted, Is.EqualTo(accepted));
        }

        [Test]
        public void should_not_reject_unknown_title_file_extension_as_known_unsupported()
        {
            var remoteBook = BuildRemoteBook(null, "Example Release.xyz");

            var decision = CreateSubject().IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase("Brandon Sanderson - Mistborn (mp3) [includes_readme.txt]")]
        [TestCase("Sanderson [readme.txt sample] audiobook.mp3")]
        [TestCase("Author - Book.epub [cover.jpg readme.txt]")]
        public void should_not_reject_title_sidecar_extensions_when_supported_payload_extension_is_present(string title)
        {
            var remoteBook = BuildRemoteBook(null, title);

            var decision = CreateSubject().IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase("epub")]
        [TestCase("azw3")]
        [TestCase("pdf")]
        [TestCase("m4b")]
        [TestCase("mp3")]
        [TestCase("flac")]
        [TestCase("mka")]
        [TestCase("MKA")]
        public void should_accept_supported_indexer_file_types(string fileType)
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook(fileType), null);

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase("Example Release.mka")]
        [TestCase("Example Release [MKA]")]
        [TestCase("Example Release mp3 mka")]
        [TestCase("Example Release.mp3.mka")]
        [TestCase("Example Release epub mka")]
        public void should_accept_matroska_audio_title_file_type_as_supported_unknown_audio(string title)
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook(null, title), null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_text_file_type_for_audiobook_request()
        {
            var remoteBook = BuildRemoteBook("epub");
            var criteria = BuildCriteria(BookMediaType.Audiobook);

            var decision = CreateSubject().IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Format"));
            Assert.That(decision.Reason, Is.EqualTo("File type epub is not compatible with audiobook request"));
        }

        [Test]
        public void should_reject_audio_file_type_for_ebook_request()
        {
            var remoteBook = BuildRemoteBook("m4b");
            var criteria = BuildCriteria(BookMediaType.Ebook);

            var decision = CreateSubject().IsSatisfiedBy(remoteBook, criteria);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Format"));
            Assert.That(decision.Reason, Is.EqualTo("File type m4b is not compatible with ebook request"));
        }

        [Test]
        public void should_accept_supported_file_type_for_matching_requested_media_type()
        {
            var audiobookDecision = CreateSubject().IsSatisfiedBy(BuildRemoteBook("m4b"), BuildCriteria(BookMediaType.Audiobook));
            var ebookDecision = CreateSubject().IsSatisfiedBy(BuildRemoteBook("epub"), BuildCriteria(BookMediaType.Ebook));

            Assert.That(audiobookDecision.Accepted, Is.True);
            Assert.That(ebookDecision.Accepted, Is.True);
        }

        [Test]
        public void should_accept_mixed_supported_file_types_for_either_requested_media_type()
        {
            var audiobookDecision = CreateSubject().IsSatisfiedBy(BuildRemoteBook("epub m4b"), BuildCriteria(BookMediaType.Audiobook));
            var ebookDecision = CreateSubject().IsSatisfiedBy(BuildRemoteBook("epub m4b"), BuildCriteria(BookMediaType.Ebook));

            Assert.That(audiobookDecision.Accepted, Is.True);
            Assert.That(ebookDecision.Accepted, Is.True);
        }

        [Test]
        public void should_accept_multi_format_release_when_any_supported_file_type_is_present()
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook("epub cbr"), null);

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("unknown")]
        public void should_keep_true_unknown_file_types_in_unknown_flow(string fileType)
        {
            var decision = CreateSubject().IsSatisfiedBy(BuildRemoteBook(fileType), null);

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_not_treat_known_unsupported_file_type_as_ebook_media_type_hint()
        {
            var mediaType = QualityMediaTypeHelper.DetectMediaType(
                Quality.Unknown,
                new TorrentInfo
                {
                    Title = "Red Rising: Sons of Ares Vol. 2: Wrath",
                    FileType = "cbr"
                });

            Assert.That(mediaType, Is.Null);
        }

        [Test]
        public void should_still_treat_supported_file_type_as_media_type_hint()
        {
            var mediaType = QualityMediaTypeHelper.DetectMediaType(
                Quality.Unknown,
                new TorrentInfo
                {
                    Title = "Red Rising",
                    FileType = "epub"
                });

            Assert.That(mediaType, Is.EqualTo(BookMediaType.Ebook));
        }

        [TestCase("The Ebook Handbook")]
        [TestCase("The Audiobook Handbook")]
        [TestCase("Audible Silence")]
        [TestCase("A Full Cast of Characters")]
        [TestCase("The Graphic Audio Guide")]
        [TestCase("EPUB for Beginners")]
        public void unknown_quality_should_not_infer_media_from_book_title_words(string title)
        {
            Assert.That(QualityMediaTypeHelper.DetectMediaType(Quality.Unknown, new ReleaseInfo { Title = title }), Is.Null);
        }

        [TestCase("MP3", BookMediaType.Audiobook)]
        [TestCase("m4b;flac", BookMediaType.Audiobook)]
        [TestCase("mka", BookMediaType.Audiobook)]
        [TestCase("EPUB", BookMediaType.Ebook)]
        [TestCase("pdf,mobi", BookMediaType.Ebook)]
        [TestCase("epub m4b", null)]
        [TestCase("unknown", null)]
        public void structured_file_types_should_preserve_only_unambiguous_media_evidence(string fileType, BookMediaType? mediaType)
        {
            Assert.That(QualityMediaTypeHelper.DetectMediaType(Quality.Unknown,
                new TorrentInfo { Title = "The Ebook Handbook", FileType = fileType }), Is.EqualTo(mediaType));
        }

        [Test]
        public void known_quality_should_take_precedence_over_structured_file_type()
        {
            Assert.That(QualityMediaTypeHelper.DetectMediaType(Quality.M4B,
                new TorrentInfo { Title = "The Ebook Handbook", FileType = "epub" }), Is.EqualTo(BookMediaType.Audiobook));
        }

        [TestCase(null, null)]
        [TestCase(new int[0], null)]
        [TestCase(new[] { 7000 }, null)]
        [TestCase(new[] { 7010, 7030, 7040 }, null)]
        [TestCase(new[] { 39, 108, 107020 }, null)]
        [TestCase(new[] { 7000, 7020, 107020 }, BookMediaType.Ebook)]
        [TestCase(new[] { 3000, 3030, 7000 }, BookMediaType.Audiobook)]
        [TestCase(new[] { 3030, 7020 }, null)]
        public void category_fallback_requires_unambiguous_media_evidence(int[] categories, BookMediaType? expected)
        {
            var report = new ReleaseInfo { Title = "Frank Herbert - Dune", Categories = categories?.ToList() };
            var quality = QualityParser.ParseQuality(report.Title, null, report.Categories);

            Assert.That(QualityMediaTypeHelper.DetectMediaType(quality.Quality, report), Is.EqualTo(expected));
            Assert.That(quality.Quality, Is.EqualTo(expected == BookMediaType.Audiobook ? Quality.UnknownAudio : Quality.Unknown));
        }

        [TestCase("EPUB", 3030, BookMediaType.Ebook)]
        [TestCase("M4B", 7020, BookMediaType.Audiobook)]
        [TestCase("EPUB", 7000, BookMediaType.Ebook)]
        [TestCase("M4B", 7000, BookMediaType.Audiobook)]
        public void explicit_title_format_takes_precedence_over_category(string format, int category, BookMediaType expected)
        {
            var report = new ReleaseInfo { Title = $"Frank Herbert - Dune [{format}]", Categories = new List<int> { category } };
            var quality = QualityParser.ParseQuality(report.Title, null, report.Categories);

            Assert.That(QualityMediaTypeHelper.DetectMediaType(quality.Quality, report), Is.EqualTo(expected));
        }

        [TestCase("epub", 3030, BookMediaType.Ebook)]
        [TestCase("m4b", 7020, BookMediaType.Audiobook)]
        [TestCase("epub m4b", 7020, null)]
        [TestCase("unknown", 7020, BookMediaType.Ebook)]
        public void structured_media_evidence_precedes_category_fallback(string fileType, int category, BookMediaType? expected)
        {
            var report = new TorrentInfo { FileType = fileType, Categories = new List<int> { category } };
            Assert.That(QualityMediaTypeHelper.DetectMediaType(Quality.Unknown, report), Is.EqualTo(expected));
        }

        private static RemoteBook BuildRemoteBook(string fileType, string title = "Example Release")
        {
            return new RemoteBook
            {
                Release = new TorrentInfo
                {
                    Title = title,
                    FileType = fileType
                }
            };
        }

        private static BookSearchCriteria BuildCriteria(BookMediaType mediaType)
        {
            return new BookSearchCriteria
            {
                Author = new Author { Name = "Example Author" },
                Books = new System.Collections.Generic.List<Book>
                {
                    new Book
                    {
                        Title = "Example Book",
                        MediaType = mediaType
                    }
                }
            };
        }
    }
}
