using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    [TestFixture]
    public class ReleaseTitleMatchPreparationFixture
    {
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void reuse_should_preserve_complete_results_and_per_target_identity_exclusions(BookMediaType mediaType)
        {
            var author = new Author { Name = "Alan Dean Foster" };
            Book MakeBook(int id, string title, string providerId) => new Book
            {
                Id = id, Author = author, Title = title, MediaType = mediaType,
                GoodreadsBookId = providerId,
                Editions = new List<Edition> { new Edition { Id = id, Title = title, Monitored = true } }
            };

            var target = MakeBook(1, "Aliens", "gr:target");
            var sibling = MakeBook(2, "Aliens: Resurrection", "gr:sibling");
            var copy = MakeBook(3, "Aliens: Different Edition", "gr:target");
            var astral = MakeBook(4, "Astorias𠀀Smut", "gr:astral");
            var catalog = new[] { target, sibling, copy, astral };
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            foreach (var candidates in new[] { new[] { target }, new[] { sibling }, new[] { copy }, catalog, catalog.Reverse().ToArray() })
            {
                foreach (var title in new[] { "Aliens", "Aliens: Resurrection", "Aliens: Different Edition", "Astorias𠀀Smut 🌶️", "Astorias𠀁Smut", "Aliens 1.5", "Aliens: Resurrection Aliens", "" })
                {
                    var release = $"{author.Name} - {title}";
                    var fresh = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, candidates, author.Name, catalog);
                    var reused = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, candidates, author.Name, catalog, preparation);
                    Assert.That(Snapshot(reused), Is.EqualTo(Snapshot(fresh)), release);
                    Assert.That(reused?.Book, Is.SameAs(fresh?.Book));
                }
            }
        }

        [Test]
        public void preparation_should_reuse_tokens_without_collapsing_value_equal_book_pockets()
        {
            var first = new Book { Title = "Shared", Editions = new List<Edition> { new Edition { Title = "First Pocket" } } };
            var second = new Book { Title = "Shared", Editions = new List<Edition> { new Edition { Title = "Second Pocket" } } };
            Assert.That(first.Equals(second), Is.True, "edition-only pockets have value equality, not reference identity");
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            var original = preparation.GetVariants(first);
            Assert.That(preparation.GetVariants(first), Is.SameAs(original));
            Assert.That(preparation.GetVariants(second).Select(v => v.Title), Does.Contain("Second Pocket"));
            Assert.That(original.Select(v => v.Title), Does.Not.Contain("Second Pocket"));
        }

        [Test]
        public void a_new_search_should_see_catalog_changes_without_invalidating_another_search()
        {
            var book = new Book { Title = "Original Title", Editions = new List<Edition>() };
            var firstSearch = new ReleaseTitleMatchScorer.CatalogPreparation();
            var prepared = firstSearch.GetVariants(book);
            book.Title = "Changed Title";
            var secondSearch = new ReleaseTitleMatchScorer.CatalogPreparation();
            Assert.That(secondSearch.GetVariants(book).Select(v => v.Title), Does.Contain("Changed Title"));
            Assert.That(prepared.Select(v => v.Title), Does.Not.Contain("Changed Title"));
        }

        private static string Snapshot(TitleMatchResult result)
        {
            return result == null ? "null" : JsonConvert.SerializeObject(typeof(TitleMatchResult).GetProperties()
                .Where(p => p.Name != nameof(TitleMatchResult.Book))
                .ToDictionary(p => p.Name, p => p.GetValue(result)));
        }
    }
}
