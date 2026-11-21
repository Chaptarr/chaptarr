using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture(false)]
    [TestFixture(true)]
    public class NewznabSearchCategoryDiagnosticFixture
    {
        private readonly bool _torznab;
        private LogFactory _logs;
        private MemoryTarget _messages;
        private CapabilitiesProvider _capabilities;
        private NewznabRequestGenerator _generator;

        public NewznabSearchCategoryDiagnosticFixture(bool torznab)
        {
            _torznab = torznab;
        }

        [SetUp]
        public void SetUp()
        {
            _messages = new MemoryTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, _messages);
            _logs = new LogFactory { Configuration = configuration };
            var logger = _logs.GetLogger("CategoryDiagnostic");
            _capabilities = new CapabilitiesProvider();
            _generator = _torznab
                ? new TorznabRequestGenerator(_capabilities, logger, "Selected Indexer")
                : new NewznabRequestGenerator(_capabilities, logger, "Selected Indexer");
            _generator.Settings = new NewznabSettings { BaseUrl = "https://indexer.example", Categories = new[] { 7000, 7020 } };
            _generator.MaxPages = 1;
        }

        [TearDown]
        public void TearDown()
        {
            _logs.Dispose();
        }

        [TestCase(BookMediaType.Audiobook, 7020, true, "Warn")]
        [TestCase(BookMediaType.Audiobook, 7020, false, "Debug")]
        [TestCase(BookMediaType.Ebook, 3030, true, "Warn")]
        [TestCase(BookMediaType.Ebook, 3030, false, "Debug")]
        public void should_explain_skipped_search_once_without_generating_requests(BookMediaType mediaType, int category, bool interactive, string level)
        {
            _generator.Settings.Categories = new[] { category };

            var chain = _generator.GetSearchRequests(Criteria(interactive, mediaType));

            Assert.That(Requests(chain), Is.Empty);
            Assert.That(Requests(chain), Is.Empty);
            Assert.That(_messages.Logs, Has.Count.EqualTo(1));
            Assert.That(_messages.Logs[0], Is.EqualTo($"{level}|Skipping {mediaType.ToString().ToLowerInvariant()} search on indexer 'Selected Indexer': configured categories [{category}] contain no categories for this format. No request was sent."));
            Assert.That(_capabilities.Calls, Is.Zero);
            Assert.That(_generator.Settings.Categories, Is.EqualTo(new[] { category }));
        }

        [TestCase(BookMediaType.Audiobook, 3030)]
        [TestCase(BookMediaType.Ebook, 7020)]
        public void should_keep_matching_categories_and_search_tiers_without_warning(BookMediaType mediaType, int category)
        {
            _generator.Settings.Categories = new[] { 3030, 7020 };

            var chain = _generator.GetSearchRequests(Criteria(true, mediaType));
            var requests = Requests(chain);

            Assert.That(requests, Has.Count.EqualTo(5));
            Assert.That(requests.All(r => r.Url.FullUri.Contains($"cat={category}&")), Is.True);
            Assert.That(_messages.Logs, Is.Empty);
            Assert.That(_generator.Settings.Categories, Is.EqualTo(new[] { 3030, 7020 }));
        }

        [Test]
        public void should_search_selected_generic_books_for_either_media_type([Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool interactive, [Values(false, true)] bool genericOnly)
        {
            var configured = genericOnly ? new[] { 7000 } : new[] { 7000, 3030, 7020, 7030 };
            var expectedCategories = genericOnly ? "7000" : mediaType == BookMediaType.Audiobook ? "7000,3030" : "7000,7020,7030";
            _generator.Settings.Categories = configured;

            var requests = Requests(_generator.GetSearchRequests(Criteria(interactive, mediaType)));

            Assert.That(requests, Has.Count.EqualTo(5));
            Assert.That(requests.All(r => r.Url.FullUri.Contains($"cat={expectedCategories}&")), Is.True);
            Assert.That(_messages.Logs, Is.Empty);
            Assert.That(_generator.Settings.Categories, Is.EqualTo(configured));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_keep_all_configured_categories_for_mixed_or_unspecified_media(bool mixed)
        {
            var criteria = mixed
                ? Criteria(true, BookMediaType.Audiobook, BookMediaType.Ebook)
                : Criteria(true);

            var requests = Requests(_generator.GetSearchRequests(criteria));

            Assert.That(requests, Has.Count.EqualTo(5));
            Assert.That(requests.All(r => r.Url.FullUri.Contains("cat=7000,7020&")), Is.True);
            Assert.That(_messages.Logs, Is.Empty);
        }

        [Test]
        public void should_explain_empty_configuration_without_inventing_categories()
        {
            _generator.Settings.Categories = new int[0];

            Assert.That(Requests(_generator.GetSearchRequests(Criteria(true, BookMediaType.Audiobook))), Is.Empty);
            Assert.That(_messages.Logs, Has.Count.EqualTo(1));
            Assert.That(_messages.Logs[0], Does.Contain("configured categories []"));
        }

        [Test]
        public void should_leave_recent_requests_unfiltered()
        {
            var requests = Requests(_generator.GetRecentRequests());

            Assert.That(requests, Has.Count.EqualTo(1));
            Assert.That(requests[0].Url.FullUri, Does.Contain("cat=7000,7020&"));
            Assert.That(_messages.Logs, Is.Empty);
        }

        private static BookSearchCriteria Criteria(bool interactive, params BookMediaType[] mediaTypes)
        {
            return new BookSearchCriteria
            {
                Author = new Author { Name = "Test Author" },
                BookTitle = "Test Book",
                InteractiveSearch = interactive,
                Books = mediaTypes.Select(mediaType => new Book { MediaType = mediaType }).ToList()
            };
        }

        private static List<IndexerRequest> Requests(IndexerPageableRequestChain chain)
        {
            return chain.GetAllTiers().SelectMany(page => page).ToList();
        }

        private sealed class CapabilitiesProvider : INewznabCapabilitiesProvider
        {
            public int Calls { get; private set; }

            public NewznabCapabilities GetCapabilities(NewznabSettings settings, int? proxyId, bool forceRefresh = false)
            {
                Calls++;
                return new NewznabCapabilities();
            }
        }
    }
}
