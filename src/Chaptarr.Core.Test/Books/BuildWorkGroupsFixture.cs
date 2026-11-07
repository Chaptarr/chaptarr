using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;

namespace Chaptarr.Core.Test.Books
{
    // Covers the semantic cases BuildWorkGroups's fast (token-bucketed union-find) implementation must
    // get exactly right relative to the original O(N^2) pairwise algorithm (kept as
    // BuildWorkGroupsReference): shared work-level ids, shared edition-level ids, the cross-media-type
    // work-only restriction, transitive chains, and untokened books. Each case is also asserted against
    // BuildWorkGroupsReference directly, so a regression in either implementation is caught.
    //
    // Correctness of the fast path was additionally verified once, out-of-band, by a differential run
    // against ~22,000 real books from five real large-catalogue authors (Charles Dickens, Mark Twain,
    // Nora Roberts, Lewis Carroll, Charles River Editors) - identical groupings in every case.
    [TestFixture]
    public class BuildWorkGroupsFixture
    {
        private static Book Book(int id, BookMediaType mediaType,
            string hcBook = null, string grWork = null, string olWork = null,
            string foreignEditionId = null)
        {
            return new Book
            {
                Id = id,
                AuthorId = 1,
                MediaType = mediaType,
                HardcoverBookId = hcBook,
                GoodreadsWorkId = grWork,
                OpenLibraryWorkId = olWork,
                ForeignEditionId = foreignEditionId,
                Editions = new List<Edition>(),
            };
        }

        private static Edition Edition(string hardcoverEditionId = null, string asin = null)
        {
            return new Edition { HardcoverEditionId = hardcoverEditionId, Asin = asin };
        }

        private static void AssertSameGroups(List<Book> books)
        {
            var fast = BookService.BuildWorkGroups(books);
            var reference = BookService.BuildWorkGroupsReference(books);

            var fastSets = fast.Select(g => new HashSet<int>(g.Select(b => b.Id))).ToList();
            var refSets = reference.Select(g => new HashSet<int>(g.Select(b => b.Id))).ToList();
            var fastNorm = new HashSet<string>(fastSets.Select(s => string.Join(",", s.OrderBy(x => x))));
            var refNorm = new HashSet<string>(refSets.Select(s => string.Join(",", s.OrderBy(x => x))));

            Assert.That(fastNorm.SetEquals(refNorm), $"fast={string.Join(" | ", fastNorm)} reference={string.Join(" | ", refNorm)}");
        }

        [Test]
        public void cross_format_siblings_sharing_a_work_id_group_together()
        {
            var audiobook = Book(1, BookMediaType.Audiobook, hcBook: "hc:1");
            var ebook = Book(2, BookMediaType.Ebook, hcBook: "hc:1");
            var books = new List<Book> { audiobook, ebook };

            AssertSameGroups(books);
            var groups = BookService.BuildWorkGroups(books);
            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].Select(b => b.Id), Is.EquivalentTo(new[] { 1, 2 }));
        }

        [Test]
        public void cross_format_books_sharing_only_an_edition_id_do_not_group()
        {
            // CrossFormatSafeMatches deliberately restricts cross-media-type matching to work-level ids -
            // an edition-only overlap (e.g. a shared ASIN reused between unrelated formats) must not merge
            // an audiobook and an ebook into the same work group.
            var audiobook = Book(1, BookMediaType.Audiobook);
            audiobook.Editions.Add(Edition(asin: "B000SHARED"));
            var ebook = Book(2, BookMediaType.Ebook);
            ebook.Editions.Add(Edition(asin: "B000SHARED"));
            var books = new List<Book> { audiobook, ebook };

            AssertSameGroups(books);
            var groups = BookService.BuildWorkGroups(books);
            Assert.That(groups.Count, Is.EqualTo(2));
        }

        [Test]
        public void same_format_books_matching_only_via_edition_fallback_agree_with_reference()
        {
            // Exercises MatchesByProviderIdIntersection's edition-token fallback path (used when neither
            // book has any work-level id). The exact matching decision belongs to WorkIdMatcher's own
            // tests; what this asserts is that the fast path reaches the same decision as the reference
            // algorithm for this path, not what that decision has to be.
            var a = Book(1, BookMediaType.Ebook);
            a.Editions.Add(Edition(hardcoverEditionId: "he:1"));
            var b = Book(2, BookMediaType.Ebook);
            b.Editions.Add(Edition(hardcoverEditionId: "he:1"));
            var books = new List<Book> { a, b };

            AssertSameGroups(books);
        }

        [Test]
        public void transitive_chain_across_different_shared_tokens_groups_into_one()
        {
            // A<->B via a work id, B<->C via a different work id, A and C share nothing directly -
            // the original BFS/queue algorithm still merges all three; the union-find replacement must too.
            var a = Book(1, BookMediaType.Audiobook, grWork: "gr:100");
            var b = Book(2, BookMediaType.Audiobook, grWork: "gr:100", olWork: "ol:200");
            var c = Book(3, BookMediaType.Audiobook, olWork: "ol:200");
            var books = new List<Book> { a, b, c };

            AssertSameGroups(books);
            var groups = BookService.BuildWorkGroups(books);
            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].Select(x => x.Id), Is.EquivalentTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void books_with_no_identity_tokens_at_all_stay_singleton()
        {
            var a = Book(1, BookMediaType.Ebook);
            var b = Book(2, BookMediaType.Ebook);
            var books = new List<Book> { a, b };

            AssertSameGroups(books);
            var groups = BookService.BuildWorkGroups(books);
            Assert.That(groups.Count, Is.EqualTo(2));
        }

        [Test]
        public void unrelated_books_do_not_group()
        {
            var a = Book(1, BookMediaType.Audiobook, hcBook: "hc:1");
            var b = Book(2, BookMediaType.Ebook, hcBook: "hc:2");
            var books = new List<Book> { a, b };

            AssertSameGroups(books);
            var groups = BookService.BuildWorkGroups(books);
            Assert.That(groups.Count, Is.EqualTo(2));
        }

        [Test]
        public void empty_and_null_input_return_no_groups()
        {
            Assert.That(BookService.BuildWorkGroups(new List<Book>()), Is.Empty);
            Assert.That(BookService.BuildWorkGroups(null), Is.Empty);
        }

        [Test]
        public void larger_mixed_scenario_matches_reference_exactly()
        {
            var books = new List<Book>
            {
                Book(1, BookMediaType.Audiobook, hcBook: "hc:1"),
                Book(2, BookMediaType.Ebook, hcBook: "hc:1"),
                Book(3, BookMediaType.Audiobook, grWork: "gr:50"),
                Book(4, BookMediaType.Audiobook, grWork: "gr:50"),
                Book(5, BookMediaType.Ebook),
                Book(6, BookMediaType.Ebook, olWork: "ol:9"),
                Book(7, BookMediaType.Audiobook, olWork: "ol:9"),
            };
            books[4].Editions.Add(Edition(hardcoverEditionId: "he:7"));

            AssertSameGroups(books);
        }
    }
}
