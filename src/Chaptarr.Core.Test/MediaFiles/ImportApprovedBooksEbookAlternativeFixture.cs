using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksEbookAlternativeFixture
    {
        [Test]
        public void should_keep_one_best_ebook_file_when_download_contains_multiple_text_formats()
        {
            var profile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = true, Quality = Quality.PDF },
                    new() { Allowed = true, Quality = Quality.MOBI },
                    new() { Allowed = true, Quality = Quality.EPUB },
                    new() { Allowed = true, Quality = Quality.AZW3 }
                }
            };

            var author = new Author
            {
                Id = 38,
                Name = "Joe Abercrombie",
                EbookQualityProfileId = profile.Id,
                EbookQualityProfile = profile
            };

            var book = new Book
            {
                Id = 5792,
                Title = "Best Served Cold",
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Ebook
            };

            var decisions = new List<ImportDecision<LocalBook>>
            {
                BuildDecision(book, author, Quality.PDF, "/downloads/Best Served Cold - Joe Abercrombie.pdf"),
                BuildDecision(book, author, Quality.EPUB, "/downloads/Best Served Cold - Joe Abercrombie.epub"),
                BuildDecision(book, author, Quality.MOBI, "/downloads/Best Served Cold - Joe Abercrombie.mobi"),
                BuildDecision(book, author, Quality.AZW3, "/downloads/Best Served Cold - Joe Abercrombie.azw3")
            };

            var importResults = new List<ImportResult>();
            var subject = new ImportApprovedBooks(
                null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null,
                LogManager.GetLogger("ImportApprovedBooksEbookAlternativeFixture"));

            var retained = subject.SelectSingleEbookAlternative(decisions, author, importResults);

            Assert.That(retained, Has.Count.EqualTo(1));
            Assert.That(retained.Single().Item.Quality.Quality, Is.EqualTo(Quality.AZW3));
            Assert.That(importResults, Has.Count.EqualTo(3));
            Assert.That(importResults.All(r => r.Result == ImportResultType.Rejected), Is.True);
        }

        [Test]
        public void should_prefer_allowed_format_over_higher_ranked_disallowed_format()
        {
            var profile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = false, Quality = Quality.PDF },
                    new() { Allowed = false, Quality = Quality.MOBI },
                    new() { Allowed = true, Quality = Quality.EPUB },
                    new() { Allowed = false, Quality = Quality.AZW3 }
                }
            };

            var author = BuildAuthor(profile);
            var book = BuildBook(author);

            var decisions = new List<ImportDecision<LocalBook>>
            {
                BuildDecision(book, author, Quality.AZW3, "/downloads/Best Served Cold - Joe Abercrombie.azw3"),
                BuildDecision(book, author, Quality.EPUB, "/downloads/Best Served Cold - Joe Abercrombie.epub"),
                BuildDecision(book, author, Quality.MOBI, "/downloads/Best Served Cold - Joe Abercrombie.mobi")
            };

            var importResults = new List<ImportResult>();
            var subject = BuildSubject();

            var retained = subject.SelectSingleEbookAlternative(decisions, author, importResults);

            Assert.That(retained, Has.Count.EqualTo(1));
            Assert.That(retained.Single().Item.Quality.Quality, Is.EqualTo(Quality.EPUB),
                "the only profile-allowed format must win over a higher-ranked disabled format");
            Assert.That(importResults, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_fall_back_to_profile_order_when_no_format_is_allowed()
        {
            var profile = new QualityProfile
            {
                Id = 1,
                Name = "Ebooks",
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Allowed = false, Quality = Quality.PDF },
                    new() { Allowed = false, Quality = Quality.MOBI },
                    new() { Allowed = false, Quality = Quality.EPUB },
                    new() { Allowed = false, Quality = Quality.AZW3 }
                }
            };

            var author = BuildAuthor(profile);
            var book = BuildBook(author);

            var decisions = new List<ImportDecision<LocalBook>>
            {
                BuildDecision(book, author, Quality.EPUB, "/downloads/Best Served Cold - Joe Abercrombie.epub"),
                BuildDecision(book, author, Quality.AZW3, "/downloads/Best Served Cold - Joe Abercrombie.azw3")
            };

            var importResults = new List<ImportResult>();
            var subject = BuildSubject();

            var retained = subject.SelectSingleEbookAlternative(decisions, author, importResults);

            Assert.That(retained, Has.Count.EqualTo(1));
            Assert.That(retained.Single().Item.Quality.Quality, Is.EqualTo(Quality.AZW3),
                "with nothing allowed the existing profile-order pick should be preserved");
        }

        private static Author BuildAuthor(QualityProfile profile)
        {
            return new Author
            {
                Id = 38,
                Name = "Joe Abercrombie",
                EbookQualityProfileId = profile.Id,
                EbookQualityProfile = profile
            };
        }

        private static Book BuildBook(Author author)
        {
            return new Book
            {
                Id = 5792,
                Title = "Best Served Cold",
                Author = author,
                AuthorId = author.Id,
                MediaType = BookMediaType.Ebook
            };
        }

        private static ImportApprovedBooks BuildSubject()
        {
            return new ImportApprovedBooks(
                null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null,
                LogManager.GetLogger("ImportApprovedBooksEbookAlternativeFixture"));
        }

        private static ImportDecision<LocalBook> BuildDecision(Book book, Author author, Quality quality, string path)
        {
            return new ImportDecision<LocalBook>(new LocalBook
            {
                Path = path,
                Book = book,
                Author = author,
                Edition = new Edition { Id = 15629, BookId = book.Id, Book = book, Title = book.Title, IsEbook = true },
                Quality = new QualityModel(quality),
                Part = 1,
                PartCount = 1
            });
        }
    }
}
