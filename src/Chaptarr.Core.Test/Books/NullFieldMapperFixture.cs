using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using NzbDrone.Core.Books;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class NullFieldMapperFixture
    {
        [Test]
        public void should_not_throw_when_author_has_null_collections()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                Images = null,
                Links = null,
                Genres = null,
                Tags = null,
                Ratings = null
            };

            Assert.DoesNotThrow(() =>
            {
                var resource = author.ToResource();

                Assert.That(resource.Images, Is.Not.Null);
                Assert.That(resource.Links, Is.Not.Null);
                Assert.That(resource.Genres, Is.Not.Null);
                Assert.That(resource.Tags, Is.Not.Null);
                Assert.That(resource.Ratings, Is.Not.Null);
            });
        }

        [Test]
	        public void should_not_throw_when_book_genres_null_and_author_images_null()
	        {
            var book = new Book
            {
                Id = 1,
                Title = "Test Book",
                Genres = null,
                Images = null,
                Author = new Author
                {
                    Id = 2,
                    Name = "Nested Author",
                    Images = null,
                    Links = null,
                    Genres = null,
                    Tags = null,
                    Ratings = null
                },
                Editions = new List<Edition>()
            };

            Assert.DoesNotThrow(() =>
            {
                var resource = book.ToResource();

                Assert.That(resource.Genres, Is.Not.Null);
                Assert.That(resource.Genres, Is.Empty);
                Assert.That(resource.Author, Is.Not.Null);
                Assert.That(resource.Author.Images, Is.Not.Null);
            });
        }

	        [Test]
	        public void should_map_book_genres_as_array_values()
	        {
	            var book = new Book
	            {
	                Id = 3,
	                Title = "Genre Book",
	                Genres = new List<string> { "Fantasy", "Science Fiction" },
	                Author = new Author { Id = 4, Name = "Nested Author" },
	                Editions = new List<Edition>()
	            };

	            var resource = book.ToResource();

	            Assert.That(resource.Genres, Is.EquivalentTo(new[] { "Fantasy", "Science Fiction" }));
	        }
	    }
	}
