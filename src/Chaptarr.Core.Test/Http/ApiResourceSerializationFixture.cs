using System.Text.Json;
using System.Linq;
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

        [TestCase(false)]
        [TestCase(true)]
        public void author_index_should_use_format_monitoring_without_a_legacy_work_override(bool legacy)
        {
            var book = new Book { Monitored = true, MediaType = BookMediaType.Ebook, EbookMonitored = false };
            var context = legacy ? new Chaptarr.Http.Middleware.ReadarrFacadeContext("hc", null, "/api", isBookshelfCompatibility: true) : null;

            Assert.That(book.ToAuthorIndexResource(15, context).Monitored, Is.False);
        }

        [Test]
        public void author_resource_should_preserve_the_legacy_embedded_book_shape_without_cycles()
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
                Author = author,
                ForeignEditionId = "hc:edition:23",
                HardcoverBookId = "22",
                CleanTitle = "enshittification",
                RelatedBooks = new System.Collections.Generic.List<int> { 23 },
                AudiobookMonitored = true,
                LastInfoSync = System.DateTime.UtcNow
            };
            author.Books = new System.Collections.Generic.List<Book> { book };

            var nextBook = book.ToAuthorIndexResource(authorMetadataId: 15);

            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                NextBook = nextBook
            };

            var options = NzbDrone.Common.Serializer.STJson.GetSerializerSettings();
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(resource, options));
            var serializedBook = json.RootElement.GetProperty("nextBook");

            Assert.That(serializedBook.GetProperty("title").GetString(), Is.EqualTo("Enshittification"));
            Assert.That(serializedBook.GetProperty("authorMetadataId").GetInt32(), Is.EqualTo(15));
            Assert.That(serializedBook.GetProperty("foreignBookId").GetString(), Is.EqualTo("hc:22"));
            Assert.That(serializedBook.GetProperty("foreignEditionId").GetString(), Is.EqualTo("hc:edition:23"));
            Assert.That(serializedBook.GetProperty("relatedBooks")[0].GetInt32(), Is.EqualTo(23));
            Assert.That(serializedBook.GetProperty("cleanTitle").GetString(), Is.EqualTo("enshittification"));
            Assert.That(serializedBook.GetProperty("monitored").GetBoolean(), Is.True);
            Assert.That(serializedBook.GetProperty("author").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(serializedBook.GetProperty("authorMetadata").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(serializedBook.GetProperty("editions").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(serializedBook.GetProperty("bookFiles").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(serializedBook.GetProperty("seriesLinks").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(serializedBook.EnumerateObject().Select(property => property.Name), Is.EquivalentTo(new[]
            {
                "id", "authorMetadataId", "foreignBookId", "foreignEditionId", "titleSlug", "title",
                "links", "genres", "relatedBooks", "ratings", "cleanTitle",
                "monitored", "anyEditionOk", "lastInfoSync", "added", "addOptions", "authorMetadata", "author",
                "editions", "bookFiles", "seriesLinks"
            }));
            Assert.That(serializedBook.TryGetProperty("authorId", out _), Is.False);
        }
    }
}
