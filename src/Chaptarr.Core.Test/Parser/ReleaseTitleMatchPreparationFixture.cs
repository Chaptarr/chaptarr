using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

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

                    foreach (var scopedCatalog in new[] { catalog, candidates })
                    {
                        var report = new ReleaseInfo { Title = release };
                        fresh = ReleaseTitleMatchScorer.FindBestMatchForSearch(report, author.Name, candidates, author.Name, scopedCatalog, new ReleaseTitleMatchScorer.CatalogPreparation());
                        reused = ReleaseTitleMatchScorer.FindBestMatchForSearch(report, author.Name, candidates, author.Name, scopedCatalog, preparation);
                        Assert.That(Snapshot(reused), Is.EqualTo(Snapshot(fresh)), release);
                        Assert.That(reused?.Book, Is.SameAs(fresh?.Book));
                    }
                }
            }
        }

        [Test]
        public void preparation_should_reuse_tokens_without_collapsing_value_equal_book_pockets()
        {
            var first = new Book { Title = "Shared", Editions = new List<Edition> { new Edition { Title = "First Pocket", Monitored = true } } };
            var second = new Book { Title = "Shared", Editions = new List<Edition> { new Edition { Title = "Second Pocket", Monitored = true } } };
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
            var book = new Book { Title = "Original Title", Editions = new List<Edition> { new Edition { Title = "Original Title", Monitored = true } } };
            var firstSearch = new ReleaseTitleMatchScorer.CatalogPreparation();
            var prepared = firstSearch.GetVariants(book);
            book.Title = "Changed Title";
            book.Editions[0].Title = "Changed Title";
            var secondSearch = new ReleaseTitleMatchScorer.CatalogPreparation();
            Assert.That(secondSearch.GetVariants(book).Select(v => v.Title), Does.Contain("Changed Title"));
            Assert.That(prepared.Select(v => v.Title), Does.Not.Contain("Changed Title"));
            Assert.That(firstSearch.GetContext(book).SelectedEditionTitleTokens, Is.EqualTo(new[] { "original", "title" }));
            Assert.That(secondSearch.GetContext(book).SelectedEditionTitleTokens, Is.EqualTo(new[] { "changed", "title" }));
        }

        [Test]
        public void prepared_work_identity_should_preserve_provider_and_media_boundaries()
        {
            Book Make(string work, string edition, BookMediaType media) => new Book
            {
                Title = "Shared Title", HardcoverBookId = work, MediaType = media,
                Editions = new List<Edition> { new Edition { Title = "Shared Title", ForeignEditionId = edition, Asin = edition, Monitored = true } }
            };

            var first = Make("hc:123", "az:B012345678", BookMediaType.Audiobook);
            var alias = Make(null, "az:B098765432", BookMediaType.Audiobook);
            alias.RemoteProviderIds = new HashSet<string> { "hc:123" };
            var differentWork = Make("hc:456", "az:B012345678", BookMediaType.Audiobook);
            var editionOnly = Make(null, "az:B012345678", BookMediaType.Audiobook);
            var editionCopy = Make(null, "az:B012345678", BookMediaType.Audiobook);
            var otherMedia = Make(null, "az:B012345678", BookMediaType.Ebook);
            var noIds = Make(null, null, BookMediaType.Audiobook);
            var books = new[] { first, alias, differentWork, editionOnly, editionCopy, otherMedia, noIds, null };
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            foreach (var left in books)
            {
                foreach (var right in books)
                {
                    Assert.That(preparation.IsSameWork(left, right), Is.EqualTo(WorkIdMatcher.WorkIdMatches(left, right)));
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(preparation.IsSameWork(first, alias), Is.True);
                Assert.That(preparation.IsSameWork(first, differentWork), Is.False);
                Assert.That(preparation.IsSameWork(first, editionOnly), Is.False);
                Assert.That(preparation.IsSameWork(editionOnly, editionCopy), Is.True);
                Assert.That(preparation.IsSameWork(editionOnly, otherMedia), Is.False);
                Assert.That(preparation.IsSameWork(noIds, noIds), Is.False);
            });

            alias.RemoteProviderIds.Clear();
            Assert.That(new ReleaseTitleMatchScorer.CatalogPreparation().IsSameWork(first, alias), Is.False);
        }

        [Test]
        public void repeated_search_should_reuse_catalog_preparation_without_retokenizing_every_sibling_pair()
        {
            var author = new Author { Id = 901, Name = "Example Writer" };
            var catalog = Enumerable.Range(1, 1000).Select(id =>
            {
                var title = $"The Journey to the Distant Horizon {id}";
                return new Book
                {
                    Id = id, Author = author, Title = title, HardcoverBookId = $"hc:{id}",
                    Editions = new List<Edition> { new Edition { Id = id, Title = title, Monitored = true, Language = "English" } }
                };
            }).ToArray();
            var target = catalog[0];
            var report = new ReleaseInfo { Title = $"{author.Name} - {target.Editions[0].Title}" };
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            TitleMatchResult Match() => ReleaseTitleMatchScorer.FindBestMatchForSearch(report, author.Name, new[] { target }, author.Name, catalog, preparation);
            Match();

            var timer = Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = Match();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            timer.Stop();
            TestContext.WriteLine($"catalog={catalog.Length}, elapsedMs={timer.Elapsed.TotalMilliseconds:F3}, allocatedBytes={allocated}");
            Assert.That(result?.IsMatch, Is.True);
            Assert.That(result.Book, Is.SameAs(target));
            Assert.That(allocated, Is.LessThan(16L * 1024 * 1024));
        }

        [TestCase("Søren", "Soren", true)]
        [TestCase("Series Vol 2", "Series Volume 2", true)]
        [TestCase("War & Peace", "War and Peace", true)]
        [TestCase("が", "か", false)]
        [TestCase("Series 2", "Series 3", false)]
        [TestCase("", "", false)]
        public void prepared_equality_should_preserve_normalization_and_token_synonyms(string left, string right, bool equal)
        {
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            Assert.That(preparation.TitlesEqual(left, right), Is.EqualTo(equal));
            Assert.That(preparation.TitlesEqual(right, left), Is.EqualTo(equal));
            Assert.That(preparation.GetTokens(left), Is.SameAs(preparation.GetTokens(left)));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void release_and_catalog_specific_numeric_evidence_must_not_leak_between_reports(BookMediaType media)
        {
            var author = new Author { Id = 91, Name = "Erin North" };
            Book Make(int id, string title) => new Book
            {
                Id = id, Author = author, Title = title, MediaType = media, HardcoverBookId = $"hc:{id}",
                Editions = new List<Edition> { new Edition { Id = id, Title = title, Language = "English", Monitored = true } }
            };
            var target = Make(1, "Signal 7");
            var sibling = Make(2, "Signal Seven");
            var preparation = new ReleaseTitleMatchScorer.CatalogPreparation();
            TitleMatchResult Match(string title, Book[] catalog) => ReleaseTitleMatchScorer.FindBestMatchForSearch(
                new ReleaseInfo { Title = $"Erin North - {title}" }, author.Name, new[] { target }, null, catalog, preparation);

            for (var repeat = 0; repeat < 2; repeat++)
            {
                Assert.That(Match("Signal Seven", new[] { target })?.IsMatch, Is.True);
                Assert.That(Match("Signal Seven", new[] { target, sibling })?.IsMatch, Is.Not.True);
                Assert.That(Match("Signal Eight", new[] { target })?.IsMatch, Is.Not.True);
                Assert.That(Match("Signal 7", new[] { target })?.Book, Is.SameAs(target));
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void empty_release_should_not_prepare_the_catalog(string title)
        {
            var target = new Book { Title = "Example" };
            var catalog = new[] { target }.Select<Book, Book>(_ => throw new InvalidOperationException("Catalog should not be read for empty evidence"));
            Assert.That(ReleaseTitleMatchScorer.FindBestMatch(title, "Example Writer", new[] { target }, null, catalog), Is.Null);
            Assert.That(ReleaseTitleMatchScorer.FindBestMatches(new ReleaseInfo { Title = title }, "Example Writer", new[] { target }, null,
                catalog, new ReleaseTitleMatchScorer.CatalogPreparation()), Is.Empty);
        }

        private static string Snapshot(TitleMatchResult result)
        {
            return result == null ? "null" : JsonConvert.SerializeObject(typeof(TitleMatchResult).GetProperties()
                .Where(p => p.Name != nameof(TitleMatchResult.Book))
                .ToDictionary(p => p.Name, p => p.GetValue(result)));
        }
    }
}
