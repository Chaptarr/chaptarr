using System.Collections.Generic;
using Chaptarr.Api.V1.ProviderIds;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class ProviderAmbiguityHelperFixture
    {
        [Test]
        public void should_return_sorted_local_book_candidates_for_a_conflict()
        {
            var result = ProviderAmbiguityHelper.GetBookAmbiguity(
                new[]
                {
                    CreateBook(42, "Second record"),
                    CreateBook(41, "First record"),
                    CreateBook(42, "Duplicate candidate")
                },
                "hc:1001",
                BookMediaType.Audiobook,
                "workProviderId",
                logger: null,
                operation: "adding book");

            Assert.Multiple(() =>
            {
                Assert.That(result.Error, Is.EqualTo("Ambiguous local book match"));
                Assert.That(result.EntityType, Is.EqualTo("book"));
                Assert.That(result.ProviderId, Is.EqualTo("hc:1001"));
                Assert.That(result.MediaType, Is.EqualTo("audiobook"));
                Assert.That(result.Candidates, Has.Count.EqualTo(2));
                Assert.That(result.Candidates[0].Id, Is.EqualTo(41));
                Assert.That(result.Candidates[0].Title, Is.EqualTo("First record"));
                Assert.That(result.Candidates[0].AuthorName, Is.EqualTo("Example Author"));
                Assert.That(result.Candidates[1].Id, Is.EqualTo(42));
            });
        }

        [Test]
        public void should_not_return_an_ambiguity_for_one_local_book()
        {
            var result = ProviderAmbiguityHelper.GetBookAmbiguity(
                new[] { CreateBook(41, "Only record") },
                "hc:1001",
                BookMediaType.Audiobook,
                "workProviderId",
                logger: null);

            Assert.That(result, Is.Null);
        }

        private static Book CreateBook(int id, string title)
        {
            return new Book
            {
                Id = id,
                Title = title,
                TitleSlug = title.ToLowerInvariant().Replace(' ', '-'),
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                Author = new Author { Name = "Example Author" },
                Editions = new List<Edition>()
            };
        }
    }
}
