using System.Collections.Generic;
using Chaptarr.Api.V1.Indexers;
using NzbDrone.Core.Books;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseControllerInteractiveFilterFixture
    {
        [Test]
        public void should_hide_pack_rejections_from_visible_interactive_search_results()
        {
            var decision = new DownloadDecision(
                new RemoteBook { Release = new ReleaseInfo { Title = "House Harkonnen" } },
                new Rejection("Release appears to contain multiple books", RejectionType.Permanent, false, "Pack", 3));

            var result = ReleaseController.ShouldFilterFromInteractiveSearch(decision);

            Assert.Multiple(() =>
            {
                Assert.That(result.shouldFilter, Is.True);
                Assert.That(result.isHardFilter, Is.True);
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

            var result = ReleaseController.ShouldFilterFromInteractiveSearch(decision);

            Assert.That(result.shouldFilter, Is.False);
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

            var summary = ReleaseController.CreateFilterSummary(
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
