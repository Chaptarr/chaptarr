using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class BookshelfMatchCompatibilityScorerFixture
    {
        [Test]
        public void should_score_exact_legacy_metadata_as_a_full_match()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Ursula Le Guin" },
                ["title"] = new List<string> { "A Wizard of Earthsea" },
                ["isbn"] = new List<string> { "9780123456789" },
                ["asin"] = new List<string> { "B000000001" },
                ["language"] = new List<string> { "en; fr" },
                ["publisher"] = new List<string> { "Good Press; Second Press" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "Kindle Edition",
                Lang = "en",
                Publisher = "Good Press",
                ReleaseDate = new System.DateTime(2018, 1, 1)
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Isbn13 = "9780123456789",
                Asin = "B000000001",
                Format = "Kindle Edition",
                Language = "en",
                Publisher = "Good Press",
                ReleaseDate = new System.DateTime(2018, 1, 1)
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Ebook);

            Assert.That(percent, Is.EqualTo(100));
        }

        [Test]
        public void should_keep_bookshelf_weighting_for_strong_edition_identifiers()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Unrelated Author" },
                ["album"] = new List<string> { "Unrelated Title" },
                ["isbn"] = new List<string> { "9780123456789" },
                ["asin"] = new List<string> { "B000000001" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Expected Author",
                BookTitle = "Expected Title",
                EditionTitle = "Expected Title",
                EditionFormat = "Kindle Edition"
            };
            var edition = new Edition
            {
                Title = "Expected Title",
                Isbn13 = "9780123456789",
                Asin = "B000000001",
                Format = "Kindle Edition"
            };
            var book = new Book { Title = "Expected Title", Author = new Author { Name = "Expected Author" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Ebook);

            Assert.That(percent, Is.GreaterThan(70));
            Assert.That(percent, Is.LessThan(95));
        }

        [Test]
        public void should_score_weak_author_and_title_evidence_below_the_default_threshold()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Unrelated Author" },
                ["album"] = new List<string> { "Unrelated Title" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Expected Author",
                BookTitle = "Expected Title",
                EditionTitle = "Expected Title"
            };
            var edition = new Edition { Title = "Expected Title" };
            var book = new Book { Title = "Expected Title", Author = new Author { Name = "Expected Author" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Ebook);

            Assert.That(percent, Is.LessThan(70));
        }

        [Test]
        public void should_score_bookshelf_audiobook_identifier_language_and_publisher_evidence()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["albumartist"] = new List<string> { "Ursula Le Guin" },
                ["artist"] = new List<string> { "Ursula Le Guin" },
                ["composer"] = new List<string> { "A Different Author" },
                ["album"] = new List<string> { "A Wizard of Earthsea" },
                ["isbn"] = new List<string> { "9780123456789" },
                ["asin"] = new List<string> { "B000000001" },
                ["language"] = new List<string> { "en" },
                ["publisher"] = new List<string> { "Good Press" },
                ["originaldate"] = new List<string> { "1900" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "Audiobook"
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Isbn13 = "9780123456789",
                Asin = "B000000001",
                Format = "Audiobook",
                Language = "fr",
                Publisher = "Different Press",
                ReleaseDate = new System.DateTime(2024, 1, 1)
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Audiobook);

            Assert.That(percent, Is.GreaterThan(70));
            Assert.That(percent, Is.LessThan(100));
        }

        [Test]
        public void should_use_asin_evidence_when_scoring_an_audiobook_edition()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ALBUMARTIST"] = new List<string> { "Frank Herbert" },
                ["ALBUM"] = new List<string> { "Dune" },
                ["ASIN"] = new List<string> { "B000000001" }
            };
            var book = new Book { Title = "Dune", Author = new Author { Name = "Frank Herbert" } };
            var matchingEdition = new Edition { Title = "Dune", Asin = "B000000001", Format = "Audiobook" };
            var editionWithoutAsin = new Edition { Title = "Dune", Format = "Audiobook" };

            var matchingPercent = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                new[] { tags }, null, matchingEdition, book, BookMediaType.Audiobook);
            var missingPercent = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                new[] { tags }, null, editionWithoutAsin, book, BookMediaType.Audiobook);

            Assert.That(matchingPercent, Is.GreaterThan(missingPercent));
        }

        [Test]
        public void should_not_treat_audiobook_performers_as_narrator_credits()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["albumartist"] = new List<string> { "Ursula Le Guin" },
                ["performer"] = new List<string> { "Ursula Le Guin" },
                ["album"] = new List<string> { "A Wizard of Earthsea" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "Audiobook"
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Format = "Audiobook",
                Narrator = "A Different Narrator"
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Audiobook);

            Assert.That(percent, Is.EqualTo(100));
        }

        [Test]
        public void should_use_bookshelf_audio_title_and_sort_fallbacks()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["album"] = new List<string> { string.Empty },
                ["albumsort"] = new List<string> { "A Different Title" },
                ["title"] = new List<string> { "A Wizard of Earthsea" },
                ["albumartist"] = new List<string> { "Ursula Le Guin" },
                ["artist"] = new List<string> { "Ursula Le Guin" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "Audiobook"
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Format = "Audiobook"
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Audiobook);

            Assert.That(percent, Is.EqualTo(100));
        }

        [Test]
        public void should_match_null_album_sort_fallback_separately_from_empty_album_title_fallback()
        {
            var nullAlbumTags = new Dictionary<string, List<string>>
            {
                ["ALBUMSORT"] = new List<string> { "Sorted Book Title" },
                ["TITLE"] = new List<string> { "Track Title" }
            };
            var emptyAlbumTags = new Dictionary<string, List<string>>
            {
                ["BOOKSHELF_AUDIO_BOOK"] = new List<string> { string.Empty },
                ["BOOKSHELF_AUDIO_TITLE"] = new List<string> { "Track Title" },
                ["ALBUMSORT"] = new List<string> { "Must Not Win" }
            };

            Assert.Multiple(() =>
            {
                Assert.That(
                    BookshelfMatchCompatibilityScorer.GetBookshelfBookTitle(new[] { nullAlbumTags }, BookMediaType.Audiobook),
                    Is.EqualTo("Sorted Book Title"));
                Assert.That(
                    BookshelfMatchCompatibilityScorer.GetBookshelfBookTitle(new[] { emptyAlbumTags }, BookMediaType.Audiobook),
                    Is.EqualTo("Track Title"));
            });
        }

        [Test]
        public void existing_audio_tags_should_be_refreshed_when_legacy_projection_is_missing()
        {
            var existingTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = new List<string> { "Legacy album" }
            };
            var currentTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [BookshelfAudioTagFields.Book] = new List<string> { string.Empty },
                [BookshelfAudioTagFields.Title] = new List<string> { "Track title" }
            };

            Assert.Multiple(() =>
            {
                Assert.That(FileMatchingService.NeedsBookshelfAudioTagProjection("/books/book.m4b", existingTags), Is.True);
                Assert.That(FileMatchingService.NeedsBookshelfAudioTagProjection("/books/book.m4b", currentTags), Is.False);
                Assert.That(FileMatchingService.NeedsBookshelfAudioTagProjection("/books/book.epub", existingTags), Is.False);
            });
        }

        [Test]
        public void should_search_every_bookshelf_audio_author_tag()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["albumartist"] = new List<string> { "Book Author" },
                ["artist"] = new List<string> { "Performer Author" },
                ["album"] = new List<string> { "A Shared Book" }
            };

            var terms = BookshelfMatchCompatibilityScorer.GetBookshelfRemoteAuthorSearchTerms(
                new[] { tags },
                BookMediaType.Audiobook);

            Assert.That(terms, Is.EqualTo(new[] { "Book Author", "Performer Author" }));
        }

        [Test]
        public void should_split_all_bookshelf_ebook_authors()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["AUTHOR"] = new List<string> { "First Author; Second Author" },
                ["TITLE"] = new List<string> { "A Shared Book" }
            };

            var terms = BookshelfMatchCompatibilityScorer.GetBookshelfRemoteAuthorSearchTerms(
                new[] { tags },
                BookMediaType.Ebook);

            Assert.That(terms, Is.EqualTo(new[] { "First Author", "Second Author" }));
        }

        [TestCase("XIPH:PERFORMER", "XIPH:NARRATOR")]
        [TestCase("XIPH:NARRATOR", null)]
        [TestCase("AUDIBLE:NARRATOR", null)]
        [TestCase("MP4:----:com.apple.iTunes:NARRATOR", null)]
        public void should_read_bookshelf_narrator_from_legacy_container_keys(string narratorKey, string fallbackKey)
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ALBUMARTIST"] = new List<string> { "Ursula Le Guin" },
                ["ALBUM"] = new List<string> { "A Wizard of Earthsea" },
                [narratorKey] = new List<string> { "Correct Narrator" }
            };
            if (fallbackKey != null)
            {
                tags[fallbackKey] = new List<string> { "Wrong Narrator" };
            }

            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Format = "Audiobook",
                Narrator = "Correct Narrator"
            };
            var book = new Book { Title = edition.Title, Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                new[] { tags },
                candidate: null,
                edition,
                book,
                BookMediaType.Audiobook);

            Assert.That(percent, Is.EqualTo(100));
        }

        [Test]
        public void should_not_use_isbn10_or_audible_asin_as_bookshelf_match_identifiers()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Ursula Le Guin" },
                ["title"] = new List<string> { "A Wizard of Earthsea" },
                ["isbn"] = new List<string> { "9780123456789" },
                ["asin"] = new List<string> { "B000000001" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "Kindle Edition"
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Isbn10 = "9780123456789",
                AudibleASIN = "B000000001",
                Format = "Kindle Edition"
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Ebook);

            Assert.That(percent, Is.LessThan(100));
        }

        [Test]
        public void should_preserve_bookshelf_case_sensitive_format_and_identifier_comparisons()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Ursula Le Guin" },
                ["title"] = new List<string> { "A Wizard of Earthsea" },
                ["isbn"] = new List<string> { "9780123456789" },
                ["asin"] = new List<string> { "b000000001" }
            };
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "kindle edition"
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Isbn13 = "9780123456789",
                Asin = "B000000001",
                Format = "kindle edition"
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };

            var percent = BookshelfMatchCompatibilityScorer.GetMatchPercent(new[] { tags }, candidate, edition, book, BookMediaType.Ebook);

            Assert.That(percent, Is.LessThan(100));
        }

        [Test]
        public void should_fill_missing_legacy_title_and_author_from_grouped_filenames()
        {
            var tags = new Dictionary<string, List<string>>();
            var paths = new[]
            {
                "/library/Frank Herbert - 01 - Dune.m4b",
                "/library/Frank Herbert - 02 - Dune.m4b"
            };

            var title = BookshelfMatchCompatibilityScorer.GetBookshelfBookTitle(new[] { tags, tags }, BookMediaType.Audiobook, paths);
            var authors = BookshelfMatchCompatibilityScorer.GetBookshelfRemoteAuthorSearchTerms(new[] { tags, tags }, BookMediaType.Audiobook, paths);
            var candidate = new EditionFtsMatch { AuthorName = "Frank Herbert", BookTitle = "Dune", EditionTitle = "Dune" };
            var edition = new Edition { Title = "Dune", Format = "Audiobook" };
            var book = new Book { Title = "Dune", Author = new Author { Name = "Frank Herbert" } };
            var score = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                new[] { tags, tags }, candidate, edition, book, BookMediaType.Audiobook, filePaths: paths);

            Assert.Multiple(() =>
            {
                Assert.That(title, Is.EqualTo("Dune"));
                Assert.That(authors, Is.EqualTo(new[] { "Frank Herbert" }));
                Assert.That(score, Is.EqualTo(100));
            });
        }

        [Test]
        public void should_score_an_existing_tagless_file_from_its_filename()
        {
            var tags = new Dictionary<string, List<string>>();
            var paths = new[] { "/library/Frank Herbert - Dune.m4b" };
            var candidate = new EditionFtsMatch { AuthorName = "Frank Herbert", BookTitle = "Dune", EditionTitle = "Dune" };
            var edition = new Edition { Title = "Dune", Format = "Audiobook" };
            var book = new Book { Title = "Dune", Author = new Author { Name = "Frank Herbert" } };

            var score = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                new[] { tags }, candidate, edition, book, BookMediaType.Audiobook, filePaths: paths);

            Assert.That(score, Is.EqualTo(100));
        }

        [Test]
        public void should_use_calibre_metadata_as_a_transient_legacy_override()
        {
            var rawTags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Wrong Author" },
                ["title"] = new List<string> { "Wrong Title" },
                ["isbn13"] = new List<string> { "9780000000002" },
                ["asin"] = new List<string> { "B000000002" }
            };
            var compatibilityTags = BookshelfMatchCompatibilityScorer.ApplyCalibreMetadata(
                rawTags,
                "A Wizard of Earthsea",
                new[] { "Ursula Le Guin" },
                "9780000000001",
                "B000000001",
                "123");
            var candidate = new EditionFtsMatch
            {
                AuthorName = "Ursula Le Guin",
                BookTitle = "A Wizard of Earthsea",
                EditionTitle = "A Wizard of Earthsea",
                EditionFormat = "Kindle Edition"
            };
            var edition = new Edition
            {
                Title = "A Wizard of Earthsea",
                Isbn13 = "9780000000001",
                Asin = "B000000001",
                Format = "Kindle Edition"
            };
            var book = new Book { Title = "A Wizard of Earthsea", Author = new Author { Name = "Ursula Le Guin" } };
            var score = BookshelfMatchCompatibilityScorer.GetMatchPercent(
                new[] { compatibilityTags }, candidate, edition, book, BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(score, Is.EqualTo(100));
                Assert.That(rawTags["title"], Is.EqualTo(new[] { "Wrong Title" }));
                Assert.That(rawTags["author"], Is.EqualTo(new[] { "Wrong Author" }));
            });
        }

        [Test]
        public void empty_calibre_fields_should_override_conflicting_embedded_metadata()
        {
            var rawTags = new Dictionary<string, List<string>>
            {
                ["author"] = new List<string> { "Embedded Author" },
                ["title"] = new List<string> { "Embedded Title" }
            };
            var compatibilityTags = BookshelfMatchCompatibilityScorer.ApplyCalibreMetadata(
                rawTags, null, null, null, null, null);

            Assert.Multiple(() =>
            {
                Assert.That(BookshelfMatchCompatibilityScorer.GetBookshelfBookTitle(
                    new[] { compatibilityTags }, BookMediaType.Ebook), Is.Null);
                Assert.That(BookshelfMatchCompatibilityScorer.GetBookshelfRemoteAuthorSearchTerms(
                    new[] { compatibilityTags }, BookMediaType.Ebook), Is.Empty);
            });
        }

        [Test]
        public void calibre_recall_tags_should_replace_conflicting_embedded_identifiers()
        {
            var rawTags = new Dictionary<string, List<string>>
            {
                ["TITLE"] = new List<string> { "Embedded Title" },
                ["AUTHOR"] = new List<string> { "Embedded Author" },
                ["ISBN13"] = new List<string> { "9780000000002" },
                ["ASIN"] = new List<string> { "B000000002" },
                ["GOODREADS_ID"] = new List<string> { "999" }
            };
            var compatibilityTags = BookshelfMatchCompatibilityScorer.ApplyCalibreMetadata(
                rawTags,
                "Dune",
                new[] { "Frank Herbert" },
                "9780000000001",
                null,
                null);

            var recallTags = FileMatchingService.BuildBookshelfCalibreRecallTags(
                rawTags,
                new[] { compatibilityTags },
                BookMediaType.Ebook,
                new[] { "/incoming/Dune.epub" });

            Assert.Multiple(() =>
            {
                Assert.That(recallTags["TITLE"], Is.EqualTo(new[] { "Dune" }));
                Assert.That(recallTags["AUTHOR"], Is.EqualTo(new[] { "Frank Herbert" }));
                Assert.That(recallTags["ISBN"], Is.EqualTo(new[] { "9780000000001" }));
                Assert.That(recallTags.ContainsKey("ISBN13"), Is.False);
                Assert.That(recallTags.ContainsKey("ASIN"), Is.False);
                Assert.That(recallTags.ContainsKey("GOODREADS_ID"), Is.False);
                Assert.That(rawTags["ISBN13"], Is.EqualTo(new[] { "9780000000002" }));
            });
        }
    }
}
