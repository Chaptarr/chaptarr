using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BuildWorkGroupsFixture
    {
        private sealed class FailOnLoad<T> : LazyLoaded<T>
        {
            public override void LazyLoad() => throw new InvalidOperationException("Unexpected edition load while building work groups.");
        }

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
            var reference = BuildWorkGroupsReference(books);
            var positions = new Dictionary<Book, int>(BookReferenceComparer.Instance);
            for (var i = 0; i < books.Count; i++)
            {
                positions.Add(books[i], i);
            }

            List<string> NormalizeGroups(List<List<Book>> groups) => groups
                .Select(group => string.Join(",", group.Select(book => positions[book]).OrderBy(index => index)))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            Assert.That(NormalizeGroups(fast), Is.EqualTo(NormalizeGroups(reference)));
        }

        private static List<List<Book>> BuildWorkGroupsReference(List<Book> books)
        {
            var remaining = new List<Book>(books ?? new List<Book>());
            var groups = new List<List<Book>>();

            while (remaining.Count > 0)
            {
                var seed = remaining[0];
                remaining.RemoveAt(0);

                var group = new List<Book> { seed };
                var queue = new Queue<Book>();
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    for (var index = remaining.Count - 1; index >= 0; index--)
                    {
                        var candidate = remaining[index];
                        if (!WorkIdMatcher.CrossFormatSafeMatches(current, candidate))
                        {
                            continue;
                        }

                        remaining.RemoveAt(index);
                        group.Add(candidate);
                        queue.Enqueue(candidate);
                    }
                }

                groups.Add(group);
            }

            return groups;
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
        public void distinct_unsaved_books_are_compared_by_reference()
        {
            var audiobook = Book(0, BookMediaType.Audiobook, hcBook: "hc:1");
            var ebook = Book(0, BookMediaType.Ebook, hcBook: "hc:1");
            var unrelated = Book(0, BookMediaType.Ebook, hcBook: "hc:2");

            AssertSameGroups(new List<Book> { audiobook, ebook, unrelated });
        }

        [Test]
        public void bare_work_id_is_indexed_for_cross_format_matching()
        {
            var audiobook = Book(0, BookMediaType.Audiobook, grWork: "123");
            var ebook = Book(0, BookMediaType.Ebook, grWork: "gr:123");

            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(audiobook, ebook), Is.True);
            AssertSameGroups(new List<Book> { audiobook, ebook });
            Assert.That(BookService.BuildWorkGroups(new List<Book> { audiobook, ebook }), Has.Count.EqualTo(1));
        }

        [Test]
        public void remote_work_alias_is_indexed_for_cross_format_matching()
        {
            var audiobook = Book(0, BookMediaType.Audiobook, hcBook: "hc:42");
            var ebook = Book(0, BookMediaType.Ebook);
            ebook.RemoteProviderIds = new HashSet<string> { "HC:42" };

            Assert.That(WorkIdMatcher.CrossFormatSafeMatches(audiobook, ebook), Is.True);
            AssertSameGroups(new List<Book> { audiobook, ebook });
            Assert.That(BookService.BuildWorkGroups(new List<Book> { audiobook, ebook }), Has.Count.EqualTo(1));
        }

        [Test]
        public void work_backed_books_do_not_load_editions_to_build_groups()
        {
            var audiobook = Book(1, BookMediaType.Audiobook, hcBook: "hc:1");
            var ebook = Book(2, BookMediaType.Ebook, hcBook: "hc:1");
            audiobook.LazyEditions = new FailOnLoad<List<Edition>>();
            ebook.LazyEditions = new FailOnLoad<List<Edition>>();

            AssertSameGroups(new List<Book> { audiobook, ebook });
        }

        [Test]
        public void work_backed_books_ignore_malformed_edition_fallback_ids()
        {
            var audiobook = Book(1, BookMediaType.Audiobook, hcBook: "hc:1");
            var ebook = Book(2, BookMediaType.Ebook, hcBook: "hc:1");
            audiobook.OpenLibraryEditionId = "hc:wrong";

            AssertSameGroups(new List<Book> { audiobook, ebook });
        }

        [Test]
        public void lone_workless_book_does_not_load_editions_without_a_fallback_peer()
        {
            var workless = Book(1, BookMediaType.Ebook);
            workless.LazyEditions = new FailOnLoad<List<Edition>>();
            workless.OpenLibraryEditionId = "hc:wrong";
            var workBacked = Book(2, BookMediaType.Ebook, hcBook: "hc:2");

            AssertSameGroups(new List<Book> { workless, workBacked });
        }

        [Test]
        public void a_large_shared_work_token_bucket_groups_without_pairwise_checks()
        {
            var books = Enumerable.Range(1, 7000)
                .Select(index => Book(index, index % 2 == 0 ? BookMediaType.Audiobook : BookMediaType.Ebook,
                    hcBook: "hc:shared-work"))
                .ToList();

            var groups = BookService.BuildWorkGroups(books);

            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0], Has.Count.EqualTo(books.Count));
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
