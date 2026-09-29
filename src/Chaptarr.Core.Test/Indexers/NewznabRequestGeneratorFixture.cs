using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabRequestGeneratorFixture
    {
        private const string AudibleStyleTitle = "Harry Potter and the Goblet of Fire, Book 4";
        private const string TitleInUrl = "Harry%20Potter%20and%20the%20Goblet%20of%20Fire";
        private const string BookNumberInUrl = "Book%204";

        private sealed class StubCapabilitiesProvider : INewznabCapabilitiesProvider
        {
            private readonly NewznabCapabilities _capabilities;

            public StubCapabilitiesProvider(NewznabCapabilities capabilities)
            {
                _capabilities = capabilities;
            }

            public NewznabCapabilities GetCapabilities(NewznabSettings settings, int? proxyId) => _capabilities;
        }

        private static NewznabRequestGenerator CreateGenerator(bool supportsBookSearch = true)
        {
            var capabilities = new NewznabCapabilities
            {
                SupportedSearchParameters = new[] { "q" },
                SupportedBookSearchParameters = supportsBookSearch ? new[] { "q", "author", "title" } : new[] { "q" }
            };

            return new NewznabRequestGenerator(new StubCapabilitiesProvider(capabilities))
            {
                MaxPages = 1,
                Settings = new NewznabSettings
                {
                    BaseUrl = "http://indexer.test",
                    ApiPath = "/api",
                    ApiKey = "key",
                    Categories = new[] { 3030, 7020 }
                }
            };
        }

        private static BookSearchCriteria Criteria(string title, bool interactive, string author = "J.K. Rowling")
        {
            return new BookSearchCriteria
            {
                Author = new Author { Name = author },
                BookTitle = title,
                InteractiveSearch = interactive
            };
        }

        private static List<string> UrlsInTier(IndexerPageableRequestChain chain, int tier)
        {
            return chain.GetTier(tier).SelectMany(requests => requests).Select(request => request.Url.FullUri).ToList();
        }

        [Test]
        public void should_add_a_book_number_fallback_as_the_last_tier_for_interactive_search()
        {
            var generator = CreateGenerator();
            var automatic = generator.GetSearchRequests(Criteria(AudibleStyleTitle, interactive: false));
            var interactive = generator.GetSearchRequests(Criteria(AudibleStyleTitle, interactive: true));

            Assert.That(interactive.Tiers, Is.EqualTo(automatic.Tiers + 1));

            var fallback = UrlsInTier(interactive, interactive.Tiers - 1);
            Assert.That(fallback, Is.Not.Empty);
            Assert.That(fallback, Has.All.Contains(TitleInUrl));
            Assert.That(fallback, Has.None.Contains(BookNumberInUrl));
        }

        [Test]
        public void should_keep_the_book_number_in_every_tier_before_the_fallback()
        {
            var generator = CreateGenerator();
            var automatic = generator.GetSearchRequests(Criteria(AudibleStyleTitle, interactive: false));
            var interactive = generator.GetSearchRequests(Criteria(AudibleStyleTitle, interactive: true));

            for (var tier = 0; tier < automatic.Tiers; tier++)
            {
                Assert.That(UrlsInTier(interactive, tier), Is.Not.Empty.And.All.Contains(BookNumberInUrl), $"tier {tier}");
            }
        }

        [Test]
        public void should_search_the_fallback_by_title_and_by_query_when_the_indexer_supports_book_search()
        {
            var interactive = CreateGenerator(supportsBookSearch: true).GetSearchRequests(Criteria(AudibleStyleTitle, interactive: true));

            var fallback = UrlsInTier(interactive, interactive.Tiers - 1);

            Assert.That(fallback, Has.Some.Contains("t=book").And.Some.Contains("&title=" + TitleInUrl));
            Assert.That(fallback, Has.Some.Contains("t=search").And.Some.Contains("&q=" + TitleInUrl));
        }

        [Test]
        public void should_search_the_fallback_by_query_only_when_the_indexer_has_no_book_search()
        {
            var interactive = CreateGenerator(supportsBookSearch: false).GetSearchRequests(Criteria(AudibleStyleTitle, interactive: true));

            var fallback = UrlsInTier(interactive, interactive.Tiers - 1);

            Assert.That(fallback, Is.Not.Empty.And.All.Contains("t=search"));
            Assert.That(fallback, Has.All.Contains("&q=" + TitleInUrl));
            Assert.That(fallback, Has.None.Contains(BookNumberInUrl));
        }

        [Test]
        public void should_drop_a_fractional_book_number_in_the_fallback()
        {
            var interactive = CreateGenerator().GetSearchRequests(Criteria("The Hedge Knight, Book 0.5", interactive: true, author: "George R.R. Martin"));

            var fallback = UrlsInTier(interactive, interactive.Tiers - 1);

            Assert.That(fallback, Is.Not.Empty.And.All.Contains("Hedge%20Knight"));
            Assert.That(fallback, Has.None.Contains("Book"));
        }

        [Test]
        public void should_not_add_a_book_number_fallback_for_automatic_search()
        {
            var generator = CreateGenerator();
            var plain = generator.GetSearchRequests(Criteria("Harry Potter and the Goblet of Fire", interactive: false));
            var automatic = generator.GetSearchRequests(Criteria(AudibleStyleTitle, interactive: false));

            Assert.That(automatic.Tiers, Is.EqualTo(plain.Tiers));
            Assert.That(UrlsInTier(automatic, automatic.Tiers - 1), Has.All.Contains(BookNumberInUrl));
        }

        [Test]
        public void should_not_add_a_fallback_when_the_title_has_no_book_number()
        {
            var generator = CreateGenerator();
            var automatic = generator.GetSearchRequests(Criteria("The Book of Lost Things", interactive: false, author: "John Connolly"));
            var interactive = generator.GetSearchRequests(Criteria("The Book of Lost Things", interactive: true, author: "John Connolly"));

            Assert.That(interactive.Tiers, Is.EqualTo(automatic.Tiers));
        }

        [Test]
        public void should_not_add_a_fallback_when_the_title_is_only_a_book_number()
        {
            var generator = CreateGenerator();
            var automatic = generator.GetSearchRequests(Criteria("Book 4", interactive: false));
            var interactive = generator.GetSearchRequests(Criteria("Book 4", interactive: true));

            Assert.That(interactive.Tiers, Is.EqualTo(automatic.Tiers));
        }
    }
}
