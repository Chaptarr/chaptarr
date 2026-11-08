using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorResourcePseudonymsFixture
    {
        private static Author CreateAuthor()
        {
            return new Author
            {
                Id = 7,
                Name = "C.S. Lewis",
                Pseudonyms = new List<string> { "C S Lewis", "C. S. Lewis", "Clive Staples Lewis" }
            };
        }

        [Test]
        public void resource_should_expose_stored_spellings_as_a_copy()
        {
            var author = CreateAuthor();

            var resource = author.ToResource();

            Assert.That(resource.Pseudonyms, Is.EqualTo(new[] { "C S Lewis", "C. S. Lewis", "Clive Staples Lewis" }));
            Assert.That(resource.Pseudonyms, Is.Not.SameAs(author.Pseudonyms));
        }

        [Test]
        public void resource_should_preserve_every_server_spelling_when_the_identity_warning_threshold_is_exceeded()
        {
            var spellings = Enumerable.Range(1, 24).Select(index => $"Author spelling {index}").ToList();
            var author = CreateAuthor();
            author.Pseudonyms = spellings.ValidatePseudonyms(author.Name);

            var resource = author.ToResource();

            Assert.That(author.Pseudonyms, Is.EqualTo(spellings));
            Assert.That(resource.Pseudonyms, Is.EqualTo(spellings));
            Assert.That(resource.Pseudonyms, Is.Not.SameAs(author.Pseudonyms));
        }

        [Test]
        public void update_should_keep_server_spellings_whatever_the_client_sends()
        {
            var stored = CreateAuthor();
            var resource = CreateAuthor().ToResource();
            resource.Pseudonyms = new List<string> { "Client Edited" };

            var updated = resource.ToModel(stored);

            Assert.That(updated.Pseudonyms, Is.EqualTo(new[] { "C S Lewis", "C. S. Lewis", "Clive Staples Lewis" }));
        }
    }
}
