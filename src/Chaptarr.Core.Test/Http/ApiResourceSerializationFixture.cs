using System.Text.Json;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using Chaptarr.Api.V1.Books;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class ApiResourceSerializationFixture
    {
        [Test]
        public void edition_resource_should_omit_grabbed_when_default()
        {
            var resource = new EditionResource
            {
                Id = 5,
                Title = "Project Hail Mary",
                Grabbed = false
            };

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(resource));

            Assert.That(json.RootElement.TryGetProperty(nameof(EditionResource.Grabbed), out _), Is.False);
            Assert.That(json.RootElement.GetProperty(nameof(EditionResource.Title)).GetString(), Is.EqualTo("Project Hail Mary"));
        }

        [Test]
        public void author_resource_should_serialize_next_book_without_following_domain_cycles()
        {
            var author = new NzbDrone.Core.Books.Author
            {
                Id = 10,
                Name = "Cory Doctorow",
                SortNameLastFirst = "Doctorow, Cory"
            };

            var book = new Book
            {
                Id = 22,
                Title = "Enshittification",
                TitleSlug = "enshittification",
                Author = author
            };

            var edition = new Edition
            {
                Id = 33,
                Book = book,
                Title = "Enshittification"
            };

            book.Editions = new System.Collections.Generic.List<Edition> { edition };

            var nextBook = book.ToResource();
            nextBook.Author = null;

            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                NextBook = nextBook
            };

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(resource));

            Assert.That(json.RootElement.GetProperty(nameof(AuthorResource.NextBook)).GetProperty(nameof(BookResource.Title)).GetString(), Is.EqualTo("Enshittification"));
            Assert.That(json.RootElement.GetProperty(nameof(AuthorResource.NextBook)).TryGetProperty(nameof(BookResource.Author), out _), Is.False);
        }
    }
}
