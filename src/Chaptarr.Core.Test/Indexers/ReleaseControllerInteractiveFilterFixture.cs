using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseControllerInteractiveFilterFixture
    {
        [TestCase("audiobook", BookMediaType.Audiobook)]
        [TestCase("ebook", BookMediaType.Ebook)]
        [TestCase(null, null)]
        [TestCase("all", null)]
        public async Task author_release_search_should_forward_optional_media_scope(string mediaType, BookMediaType? expected)
        {
            var search = DispatchProxy.Create<ISearchForReleases, SearchScopeProxy>();
            var controller = BuildSearchController(search);

            await controller.GetReleases(null, 10, mediaType: mediaType);

            Assert.That(((SearchScopeProxy)(object)search).Scopes, Is.EqualTo(new[] { expected }));
        }

        [Test]
        public void invalid_author_search_media_scope_should_fail_before_any_search()
        {
            var search = DispatchProxy.Create<ISearchForReleases, SearchScopeProxy>();
            var controller = BuildSearchController(search);

            Assert.ThrowsAsync<BadRequestException>(() => controller.GetReleases(null, 10, mediaType: "invalid"));
            Assert.That(((SearchScopeProxy)(object)search).Scopes, Is.Empty);
        }

        [TestCase(0, 0)]
        [TestCase(2, 0)]
        [TestCase(2, 1)]
        [TestCase(2, 2)]
        public async Task book_response_carries_indexer_counts_without_a_second_book_lookup(int queried, int failed)
        {
            var author = new Author { Id = 10 };
            var book = new Book { Id = 11, AuthorId = 10 };
            var outcome = new BookSearchResult();
            for (var id = 1; id <= queried; id++) outcome.Indexers.RecordRequest(id);
            for (var id = 1; id <= failed; id++) outcome.Indexers.RecordFailure(id);
            var searches = 0;
            var search = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<ISearchForReleases>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(ISearchForReleases.BookSearchWithOutcome)));
                Assert.That(args[0], Is.SameAs(book));
                Assert.That(book.Author, Is.SameAs(author));
                searches++;
                return Task.FromResult(outcome);
            });
            var controller = new ReleaseController(null, search, null,
                DispatchProxy.Create<IPrioritizeDownloadDecision, SearchScopeProxy>(), null,
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IAuthorService>((_, _) => author),
                Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IBookService>((method, _) => method.Name == nameof(IBookService.GetBook) ? book : throw new NotImplementedException(method.Name)),
                null, null, new CacheManager(), LogManager.GetCurrentClassLogger(), null)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Request = { QueryString = new QueryString("?bookId=11") } } }
            };
            var response = (ReleaseSearchResponse)await controller.GetReleases(book.Id, null);
            Assert.That(response.IndexersQueried, Is.EqualTo(queried));
            Assert.That(response.IndexersFailed, Is.EqualTo(failed));
            Assert.That(response.Releases, Is.Empty);
            Assert.That(searches, Is.EqualTo(1));
        }

        private static ReleaseController BuildSearchController(ISearchForReleases search)
        {
            return new ReleaseController(null, search, null,
                DispatchProxy.Create<IPrioritizeDownloadDecision, SearchScopeProxy>(),
                null, null, null, null, null, new CacheManager(), LogManager.GetCurrentClassLogger(), null)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        Request = { QueryString = new QueryString("?authorId=10") }
                    }
                }
            };
        }


        private class SearchScopeProxy : DispatchProxy
        {
            public List<BookMediaType?> Scopes { get; } = new();

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(ISearchForReleases.AuthorSearch))
                {
                    Scopes.Add((BookMediaType?)args[4]);
                    return Task.FromResult(new List<DownloadDecision>());
                }

                if (method.Name == nameof(IPrioritizeDownloadDecision.PrioritizeDecisions))
                {
                    return args[0];
                }

                throw new NotImplementedException(method.Name);
            }
        }

        [Test]
        public void should_hide_pack_rejections_from_visible_interactive_search_results()
        {
            var decision = new DownloadDecision(
                new RemoteBook { Release = new ReleaseInfo { Title = "House Harkonnen" } },
                new Rejection("Release appears to contain multiple books", RejectionType.Permanent, true, "Pack", 3));

            var result = NzbDrone.Core.DecisionEngine.InteractiveSearchVisibility.Evaluate(decision);

            // A pack is filtered out of the default list because "Pack" is a mismatch category, but it is a
            // soft filter, so it reaches the hidden-results collection as a bypassable release.
            Assert.Multiple(() =>
            {
                Assert.That(result.ShouldFilter, Is.True);
                Assert.That(result.IsHardFilter, Is.False);
            });
        }

        [TestCase("Quality")]
        [TestCase("Format")]
        [TestCase("Release Profile")]
        public void should_show_profile_rejections_when_the_interactive_result_has_a_resolved_book(string category)
        {
            var decision = new DownloadDecision(
                new RemoteBook
                {
                    Books = new List<Book> { new() { Id = 42, Title = "Storm Front" } },
                    Release = new ReleaseInfo { Title = "Storm Front" }
                },
                new Rejection("Rejected by configured preference", RejectionType.Permanent, true, category));

            var result = NzbDrone.Core.DecisionEngine.InteractiveSearchVisibility.Evaluate(decision);

            Assert.That(result.ShouldFilter, Is.False);
        }

        [Test]
        public void filter_summary_should_not_report_visible_profile_rejections_as_hidden()
        {
            var decision = new DownloadDecision(
                new RemoteBook
                {
                    Books = new List<Book> { new() { Id = 42, Title = "Storm Front" } },
                    Release = new ReleaseInfo { Title = "Storm Front" }
                },
                new Rejection("Custom Format score -1000 is below profile minimum 0", RejectionType.Permanent, true, "Format"));

            var summary = InteractiveReleaseMapper.CreateFilterSummary(
                new[] { decision },
                new List<ReleaseResource> { new() },
                hardFilteredCount: 0,
                softFilteredCount: 0,
                bypassFilters: false);

            Assert.Multiple(() =>
            {
                Assert.That(summary.FilteredCount, Is.Zero);
                Assert.That(summary.DisplayedCount, Is.EqualTo(1));
                Assert.That(summary.FilterWarnings, Is.Empty);
                Assert.That(summary.FilterBreakdown, Is.Empty);
                Assert.That(summary.SummaryText, Is.EqualTo("Showing all 1 results"));
            });
        }
    }
}
