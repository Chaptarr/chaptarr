using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshAuthorServiceBulkDiffFixture
    {
        private static readonly Logger TestLogger = LogManager.GetCurrentClassLogger();
        private const int Limit = RefreshAuthorService.BulkAuthorDiffMaxItemsPerRequest;

        private static List<V5AuthorETag> MakeItems(string prefix, int count)
        {
            return Enumerable.Range(1, count)
                .Select(i => new V5AuthorETag { RequestedId = $"{prefix}:{i}", ETag = "etag" })
                .ToList();
        }

        private static (int AuthorId, List<V5AuthorETag> Items) Group(int authorId, string prefix, int count)
        {
            return (authorId, MakeItems($"{prefix}{authorId}", count));
        }

        [Test]
        public void should_send_all_groups_in_one_chunk_when_under_limit()
        {
            var calls = new List<List<V5AuthorETag>>();
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                calls.Add(items.ToList());
                return new V5AuthorChangesResponse();
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                Group(1, "hc", 2),
                Group(2, "gr", 3)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Not.Null);
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Has.Count.EqualTo(5));
        }

        [Test]
        public void should_split_chunks_without_splitting_an_author_id_set()
        {
            var calls = new List<List<V5AuthorETag>>();
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                calls.Add(items.ToList());
                return new V5AuthorChangesResponse();
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                Group(1, "a", Limit - 3000),
                Group(2, "b", Limit - 3000)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Not.Null);
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(calls[0], Has.Count.EqualTo(Limit - 3000));
            Assert.That(calls[1], Has.Count.EqualTo(Limit - 3000));
            Assert.That(calls[0].All(item => item.RequestedId.StartsWith("a1:")), Is.True);
            Assert.That(calls[1].All(item => item.RequestedId.StartsWith("b2:")), Is.True);
        }

        [Test]
        public void should_fill_chunk_exactly_to_limit_before_splitting()
        {
            var calls = new List<List<V5AuthorETag>>();
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                calls.Add(items.ToList());
                return new V5AuthorChangesResponse();
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                Group(1, "a", 4000),
                Group(2, "b", Limit - 4000),
                Group(3, "c", 1)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Not.Null);
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(calls[0], Has.Count.EqualTo(Limit));
            Assert.That(calls[1], Has.Count.EqualTo(1));
        }

        [Test]
        public void should_keep_oversize_author_id_set_in_a_single_chunk()
        {
            var calls = new List<List<V5AuthorETag>>();
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                calls.Add(items.ToList());
                return new V5AuthorChangesResponse();
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                Group(1, "a", Limit + 500)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Not.Null);
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Has.Count.EqualTo(Limit + 500));
        }

        [Test]
        public void should_skip_empty_and_null_groups()
        {
            var calls = new List<List<V5AuthorETag>>();
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                calls.Add(items.ToList());
                return new V5AuthorChangesResponse();
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                (1, new List<V5AuthorETag>()),
                (2, null),
                Group(3, "hc", 1)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Not.Null);
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Has.Count.EqualTo(1));
        }

        [Test]
        public void should_aggregate_all_sections_across_chunks()
        {
            var chunk = 0;
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                chunk++;
                return new V5AuthorChangesResponse
                {
                    Changed = new List<V5ChangedAuthor> { new V5ChangedAuthor { RequestedId = $"hc:{chunk}" } },
                    Merged = new List<V5MergedAuthor> { new V5MergedAuthor { From = $"gr:{chunk}", To = $"gr:{chunk + 100}" } },
                    Deleted = new List<string> { $"ol:{chunk}" },
                    Rejected = new List<V5RejectedAuthor> { new V5RejectedAuthor() }
                };
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                Group(1, "a", Limit - 3000),
                Group(2, "b", Limit - 3000)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Changed, Has.Count.EqualTo(2));
            Assert.That(result.Merged, Has.Count.EqualTo(2));
            Assert.That(result.Deleted, Has.Count.EqualTo(2));
            Assert.That(result.Rejected, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_return_null_when_any_chunk_returns_no_response()
        {
            var chunk = 0;
            Func<List<V5AuthorETag>, V5AuthorChangesResponse> fetch = items =>
            {
                chunk++;
                return chunk == 2 ? null : new V5AuthorChangesResponse();
            };

            var groups = new List<(int, List<V5AuthorETag>)>
            {
                Group(1, "a", Limit - 3000),
                Group(2, "b", Limit - 3000)
            };

            var result = RefreshAuthorService.GetBulkAuthorChangesInChunks(fetch, groups, TestLogger);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_add_first_change_for_author()
        {
            var changes = new Dictionary<int, (Author LocalAuthor, V5ChangedAuthor Change, bool BypassEtag)>();
            var author = new Author { Id = 1 };
            var change = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = "hc:1", ETag = "e1" };

            RefreshAuthorService.AddActionableAuthorChange(changes, author, change, bypassEtag: false);

            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[1].Change, Is.SameAs(change));
            Assert.That(changes[1].BypassEtag, Is.False);
        }

        [Test]
        public void should_keep_existing_change_when_both_carry_canonical_ids()
        {
            var changes = new Dictionary<int, (Author LocalAuthor, V5ChangedAuthor Change, bool BypassEtag)>();
            var author = new Author { Id = 1 };
            var first = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = "hc:1", ETag = "e1" };
            var second = new V5ChangedAuthor { RequestedId = "gr:2", CanonicalId = "gr:2", ETag = "e2" };

            RefreshAuthorService.AddActionableAuthorChange(changes, author, first, bypassEtag: false);
            RefreshAuthorService.AddActionableAuthorChange(changes, author, second, bypassEtag: false);

            Assert.That(changes[1].Change, Is.SameAs(first));
        }

        [Test]
        public void should_replace_canonical_less_change_with_canonical_bearing_one()
        {
            var changes = new Dictionary<int, (Author LocalAuthor, V5ChangedAuthor Change, bool BypassEtag)>();
            var author = new Author { Id = 1 };
            var first = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = null, ETag = "e1" };
            var second = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = "hc:9", ETag = "e2" };

            RefreshAuthorService.AddActionableAuthorChange(changes, author, first, bypassEtag: false);
            RefreshAuthorService.AddActionableAuthorChange(changes, author, second, bypassEtag: false);

            Assert.That(changes[1].Change, Is.SameAs(second));
        }

        [Test]
        public void should_propagate_existing_etag_onto_replacement_without_one()
        {
            var changes = new Dictionary<int, (Author LocalAuthor, V5ChangedAuthor Change, bool BypassEtag)>();
            var author = new Author { Id = 1 };
            var first = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = null, ETag = "e1" };
            var second = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = "hc:9", ETag = null };

            RefreshAuthorService.AddActionableAuthorChange(changes, author, first, bypassEtag: false);
            RefreshAuthorService.AddActionableAuthorChange(changes, author, second, bypassEtag: false);

            Assert.That(changes[1].Change, Is.SameAs(second));
            Assert.That(changes[1].Change.ETag, Is.EqualTo("e1"));
            Assert.That(changes[1].BypassEtag, Is.False);
        }

        [Test]
        public void should_keep_bypass_flag_when_later_change_does_not_replace()
        {
            var changes = new Dictionary<int, (Author LocalAuthor, V5ChangedAuthor Change, bool BypassEtag)>();
            var author = new Author { Id = 1 };
            var mergeRepair = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = "hc:9", ETag = null };
            var later = new V5ChangedAuthor { RequestedId = "gr:2", CanonicalId = "gr:8", ETag = "e2" };

            RefreshAuthorService.AddActionableAuthorChange(changes, author, mergeRepair, bypassEtag: true);
            RefreshAuthorService.AddActionableAuthorChange(changes, author, later, bypassEtag: false);

            Assert.That(changes[1].Change, Is.SameAs(mergeRepair));
            Assert.That(changes[1].BypassEtag, Is.True);
        }

        [Test]
        public void should_not_report_author_deleted_when_only_some_of_its_ids_are_deleted()
        {
            var author = new Author { Id = 1 };
            var requested = new Dictionary<int, HashSet<string>>
            {
                [1] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hc:1", "gr:2" }
            };

            var reportable = RefreshAuthorService.GetDeletedIdsToReport(
                new[] { "hc:1" },
                id => author,
                requested,
                TestLogger);

            Assert.That(reportable, Is.Empty);
        }

        [Test]
        public void should_report_author_deleted_when_all_of_its_ids_are_deleted()
        {
            var author = new Author { Id = 1 };
            var requested = new Dictionary<int, HashSet<string>>
            {
                [1] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hc:1", "gr:2" }
            };

            var reportable = RefreshAuthorService.GetDeletedIdsToReport(
                new[] { "hc:1", "gr:2" },
                id => author,
                requested,
                TestLogger);

            Assert.That(reportable, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_report_deleted_id_that_resolves_to_no_local_author()
        {
            var reportable = RefreshAuthorService.GetDeletedIdsToReport(
                new[] { "hc:404" },
                id => null,
                new Dictionary<int, HashSet<string>>(),
                TestLogger);

            Assert.That(reportable, Is.EqualTo(new List<string> { "hc:404" }));
        }

        [Test]
        public void should_prefer_canonical_id_for_refresh()
        {
            var change = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = "hc:9" };

            Assert.That(RefreshAuthorService.GetAuthorRefreshId(change), Is.EqualTo("hc:9"));
        }

        [Test]
        public void should_fall_back_to_requested_id_when_canonical_missing()
        {
            var change = new V5ChangedAuthor { RequestedId = "hc:1", CanonicalId = " " };

            Assert.That(RefreshAuthorService.GetAuthorRefreshId(change), Is.EqualTo("hc:1"));
            Assert.That(RefreshAuthorService.GetAuthorRefreshId(null), Is.Null);
        }

        [Test]
        public void should_collect_distinct_provider_ids_for_diff()
        {
            var author = new Author
            {
                Id = 1,
                HardcoverAuthorId = "hc:123",
                GoodreadsAuthorId = "gr:456",
                RemoteProviderIds = new HashSet<string> { "hc:123" }
            };

            var ids = RefreshAuthorService.GetAuthorDiffProviderIds(author);

            Assert.That(ids, Has.Count.EqualTo(2));
            Assert.That(RefreshAuthorService.GetAuthorDiffProviderIds(null), Is.Empty);
        }

        [Test]
        public void should_not_schedule_identity_repair_for_aliases_on_the_same_local_author()
        {
            var author = new Author { Id = 1 };

            Assert.That(RefreshAuthorService.IsAlreadyKnownProviderAlias(author, author), Is.True);
            Assert.That(RefreshAuthorService.IsAlreadyKnownProviderAlias(author, new Author { Id = 1 }), Is.True);
        }

        [Test]
        public void should_not_treat_new_or_cross_row_canonical_ids_as_known_aliases()
        {
            var source = new Author { Id = 1 };

            Assert.That(RefreshAuthorService.IsAlreadyKnownProviderAlias(source, null), Is.False);
            Assert.That(RefreshAuthorService.IsAlreadyKnownProviderAlias(source, new Author { Id = 2 }), Is.False);
            Assert.That(RefreshAuthorService.IsAlreadyKnownProviderAlias(null, source), Is.False);
        }
    }
}
