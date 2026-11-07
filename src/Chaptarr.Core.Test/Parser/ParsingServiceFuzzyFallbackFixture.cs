using System.Collections.Generic;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ParsingServiceFuzzyFallbackFixture
    {
        private static ParsingService CreateService(int matchPercent)
        {
            var author = new Author { Id = 1, Name = "Frank Herbert" };
            var book = new Book { Id = 2, AuthorId = author.Id, Title = "The Mote in God's Eye" };

            return CreateService(matchPercent, author, new List<Book> { book }, new List<Edition>());
        }

        private static ParsingService CreateService(int matchPercent, Author author, List<Book> books, List<Edition> editions)
        {
            var authorService = new Mock<IAuthorService>();
            authorService.Setup(service => service.GetReportCandidates(It.IsAny<string>()))
                .Returns(new List<Author> { author });

            var bookService = new Mock<IBookService>();
            bookService.Setup(service => service.GetCandidates(author.Id, It.IsAny<string>()))
                .Returns(books);

            var editionService = new Mock<IEditionService>();
            editionService.Setup(service => service.GetCandidates(author.Id, It.IsAny<string>()))
                .Returns(editions);

            var configService = new Mock<IConfigService>();
            configService.Setup(service => service.BookImportMinimumMatchPercent).Returns(matchPercent);

            return new ParsingService(authorService.Object,
                                      bookService.Object,
                                      editionService.Object,
                                      null,
                                      null,
                                      LogManager.GetCurrentClassLogger(),
                                      configService.Object);
        }

        [Test]
        public void should_accept_report_title_at_the_saved_bookshelf_threshold()
        {
            var result = CreateService(90).ParseBookTitleFuzzy("The Mote in Gxd's Eye");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Frank Herbert"));
            Assert.That(result.AuthorTitleInfo.Title, Is.EqualTo("Frank Herbert"));
            Assert.That(result.BookTitle, Is.EqualTo("The Mote in God's Eye"));
        }

        [Test]
        public void should_reject_report_title_below_the_saved_bookshelf_threshold()
        {
            var result = CreateService(99).ParseBookTitleFuzzy("The Mote in Gxd's Eye");

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_preserve_parser_validation_for_releases_rejected_as_password_yenc()
        {
            var result = CreateService(70).ParseBookTitleFuzzy("Frank Herbert - The Mote in God's Eye password yenc");

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_preserve_the_matched_edition_title_when_it_beats_the_book_title()
        {
            var author = new Author { Id = 1, Name = "Jane Austen" };
            var book = new Book { Id = 2, AuthorId = author.Id, Title = "Unknown Catalog Title" };
            var edition = new Edition { Id = 3, Book = book, BookId = book.Id, Title = "The Hobbit", Monitored = true };

            var result = CreateService(70, author, new List<Book>(), new List<Edition> { edition })
                .ParseBookTitleFuzzy("Jane Austen - The Hobbit");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AuthorName, Is.EqualTo("Jane Austen"));
            Assert.That(result.BookTitle, Is.EqualTo("The Hobbit"));
        }
    }
}
