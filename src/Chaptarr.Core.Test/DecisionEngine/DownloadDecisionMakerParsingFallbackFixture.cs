using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Indexers;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    [NonParallelizable]
    public class DownloadDecisionMakerParsingFallbackFixture
    {
        private sealed class TestParsingService : IParsingService
        {
            public Author MappedAuthor { get; set; }
            public List<Book> MappedBooks { get; set; } = new();
            public List<int> MappedBookIds { get; private set; } = new();
            public int GetAuthorCalls { get; private set; }

            public Author GetAuthor(string title)
            {
                GetAuthorCalls++;
                return null;
            }

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, SearchCriteriaBase searchCriteria = null)
            {
                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = searchCriteria?.Author ?? MappedAuthor,
                    Books = searchCriteria?.Books ?? MappedBooks ?? new List<Book>()
                };
            }

            public RemoteBook Map(ParsedBookInfo parsedBookInfo, int authorId, IEnumerable<int> bookIds)
            {
                MappedBookIds = bookIds?.Distinct().ToList() ?? new List<int>();
                var books = MappedBooks.Where(book => MappedBookIds.Contains(book.Id)).ToList();

                return new RemoteBook
                {
                    ParsedBookInfo = parsedBookInfo,
                    Author = books.Select(book => book.Author).FirstOrDefault(author => author != null),
                    Books = books
                };
            }

            public List<List<Book>> GetBooks(IReadOnlyList<ParsedBookInfo> parsedBooks, Author author) => throw new NotImplementedException();

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null)
            {
                return searchCriteria?.Books ?? new List<Book>();
            }

            public Book GetLocalBook(string filename, Author author)
            {
                return null;
            }
        }

        private class CatalogBookService : DispatchProxy
        {
            public List<Book> Books { get; set; }
            public int Calls { get; private set; }

            public static IBookService Create(List<Book> books)
            {
                var service = Create<IBookService, CatalogBookService>();
                ((CatalogBookService)(object)service).Books = books;
                return service;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.GetBooksByAuthor)));
                Assert.That(args[1], Is.True);
                Calls++;
                return Books.Where(book => book.AuthorId == (int)args[0]).ToList();
            }
        }

        private sealed class TestEditionFtsRepository : IEditionFtsRepository
        {
            public List<BookFtsMatch> Recalls { get; } = new();
            public Action OnRecall { get; set; }
            public List<(BookMediaType MediaType, bool MonitoredOnly, List<string> Tokens)> Requests { get; } = new();

            public void EnsureIndexPopulated() => RebuildIndex();
            public bool FtsTableExists() => true;

            public void RebuildIndex()
            {
            }


            public List<BookFtsMatch> RecallBooks(
                int? authorId,
                IEnumerable<string> tokens,
                BookMediaType mediaType,
                Action<EditionFtsTraceEvent> trace = null,
                int limit = 20,
                bool monitoredOnly = false)
            {
                OnRecall?.Invoke();
                Requests.Add((mediaType, monitoredOnly, tokens.ToList()));
                return Recalls.Take(limit).ToList();
            }

            public List<EditionFtsMatch> RankEditions(
                IReadOnlyCollection<BookFtsMatch> recalledBooks,
                IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries,
                BookMediaType mediaType,
                Action<EditionFtsTraceEvent> trace = null)
            {
                throw new AssertionException("RSS only needs book recall.");
            }
        }

        private sealed class NoOpCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public Func<RemoteBook, List<CustomFormat>> ParseRelease { get; init; }
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => ParseRelease?.Invoke(remoteBook) ?? new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        private sealed class NoOpRemoteBookAggregationService : IRemoteBookAggregationService
        {
            public RemoteBook Augment(RemoteBook remoteBook)
            {
                return remoteBook;
            }
        }

        private sealed class NoOpReleaseNarratorMetadataEnricher : IReleaseNarratorMetadataEnricher
        {
            public IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
            {
                return System.Array.Empty<ReleaseInfo>();
            }
        }

        private sealed class RecordingReleaseNarratorMetadataEnricher : IReleaseNarratorMetadataEnricher
        {
            public List<string> OfferedTitles { get; } = new();
            public int CallCount { get; private set; }

            public IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
            {
                CallCount++;
                OfferedTitles.AddRange(releases.Select(release => release.Title));

                foreach (var release in releases)
                {
                    release.Narrator = "Scott Brick";
                }

                return releases;
            }
        }

        private sealed class FixedRejectionSpecification(string category) : IDecisionEngineSpecification
        {
            public SpecificationPriority Priority => SpecificationPriority.Default;
            public RejectionType Type => RejectionType.Permanent;
            public Decision IsSatisfiedBy(RemoteBook remoteBook, SearchCriteriaBase searchCriteria) =>
                Decision.RejectHardFilter("Cannot be changed by narrator metadata", category);
        }

        private sealed class RecordingReleaseSourceSpecification : IDecisionEngineSpecification
        {
            public ReleaseSourceType? SeenSource { get; private set; }
            public SpecificationPriority Priority => SpecificationPriority.Default;
            public RejectionType Type => RejectionType.Permanent;

            public Decision IsSatisfiedBy(RemoteBook remoteBook, SearchCriteriaBase searchCriteria)
            {
                SeenSource = remoteBook.ReleaseSource;
                return Decision.Accept();
            }
        }

        private static (DownloadDecisionMaker Maker, IBookService Books) CreateTitleDecisionMaker(
            Author author, List<Book> catalog, Book target, BookMatchingStrictness mode, Logger logger = null)
        {
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = catalog };
            var books = CatalogBookService.Create(catalog);
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = target.Id, AuthorId = author.Id, BookTitle = target.Title });
            var config = ConfigServiceTestProxy.Create(mode);
            logger ??= LogManager.GetCurrentClassLogger();
            var maker = new DownloadDecisionMaker(new IDecisionEngineSpecification[]
            {
                new ReleaseTitleMatchSpecification(logger, config),
                new MultiBookReleaseSpecification(logger),
                new QualityAllowedByProfileSpecification(logger),
                new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(logger)
            }, parsing, books, fts, new NoOpCustomFormatCalculationService(), new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(), config, logger);
            return (maker, books);
        }

        [Test]
        public void sibling_comparison_cost_is_logged_once_per_release_batch()
        {
            using var factory = new LogFactory();
            var log = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration(factory);
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, log);
            factory.Configuration = logging;
            var author = new Author { Id = 1, Name = "Frank Herbert" };
            Book MakeBook(int id, string title) => new Book
            {
                Id = id, AuthorId = author.Id, Author = author, Title = title, MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = id, BookId = id, Title = title, Monitored = true } }
            };
            var target = MakeBook(1, "Dune");
            var catalog = new List<Book> { target, MakeBook(2, "Dune Messiah") };
            var (maker, _) = CreateTitleDecisionMaker(author, catalog, target, BookMatchingStrictness.Balanced, factory.GetLogger("SiblingCost"));
            var reports = new[] { "Frank Herbert Dune MP3", "Frank Herbert Dune Messiah MP3" }
                .Select((title, index) => new ReleaseInfo { Guid = index.ToString(), Title = title, Indexer = "Fixture", Categories = new List<int> { 3030 } }).ToList();

            maker.GetSearchDecision(reports, new BookSearchCriteria { Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog });

            var lines = log.Logs.Where(line => line.StartsWith("[TITLE-SIBLINGS]", StringComparison.Ordinal)).ToList();
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines.Single(), Does.StartWith("[TITLE-SIBLINGS] 2 releases"));
            Assert.That(lines.Single(), Does.Match(@"[1-9]\d* sibling comparisons \(largest scan 1\)"));
            Assert.That(lines.Single(), Does.Contain("numeric variant checks; sibling scans took"));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void title_identity_should_agree_across_rss_automatic_and_interactive_search(int path)
        {
            foreach (var mode in new[] { BookMatchingStrictness.Strict, BookMatchingStrictness.Balanced, BookMatchingStrictness.Aggressive })
            {
                var profile = new QualityProfile
                {
                    Id = 1, ProfileType = ProfileType.Audiobook, Cutoff = Quality.MP3.Id,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                        new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true },
                        new QualityProfileQualityItem { Quality = Quality.FLAC, Allowed = false }
                    }
                };
                var author = new Author { Id = 1, Name = "Frank Herbert", AudiobookMonitored = true, AudiobookQualityProfileId = 1, AudiobookQualityProfile = profile };
                Book MakeBook(int id, string title, bool monitored) => new Book
                {
                    Id = id, AuthorId = author.Id, Author = author, Title = title, MediaType = BookMediaType.Audiobook,
                    AudiobookMonitored = monitored, AnyEditionOk = true,
                    Editions = new List<Edition> { new Edition { Id = id, BookId = id, Title = title, Monitored = true } }
                };
                var target = MakeBook(1, "Dune", true);
                target.Editions[0].Narrator = "Scott Brick";
                target.SeriesName = "Dune";
                target.SeriesPosition = "1";
                var catalog = new List<Book> { target, MakeBook(2, "Dune Messiah", false), MakeBook(3, "Children of Dune", false) };
                var (maker, books) = CreateTitleDecisionMaker(author, catalog, target, mode);
                var cases = new[]
                {
                    ("Frank.Herbert.Dune.Repack.m4b.Scott.Brick.Euan.Morton.Simon.vance.320kps", true),
                    ("Frank.Herbert.Dune.320kbps.Repack.m4b.Scott.Brick.Euan.Morton.Simon.vance.", true),
                    ("Frank Herbert - Dune réédition MP3", true),
                    ("Frank Herbert - Dune [64-128 kbps] MP3", true),
                    ("Frank Herbert - Dune [Dune 64-128 kbps] MP3", true),
                    ("Frank Herbert - Dune 1 MP3", true),
                    ("Frank Herbert - Dune 4 MP3", false),
                    ("Frank Herbert Dune series Dune 4 MP3", false),
                    ("Frank Herbert Dune series du02 Dune Messiah MP3", false),
                    ("Frank Herbert - Children of Dune MP3", false),
                    ("Frank Herbert - Dune FLAC", false)
                }.Select(item => (item.Item1, item.Item2, Author: author.Name)).Concat(new[]
                {
                    ("Dune - Scott Brick MP3", false, Author: (string)null),
                    ("Dune - Dune MP3", false, Author: (string)null),
                    ("Frank Herbert - Dune - Scott Brick MP3", true, Author: (string)null)
                }).ToArray();
                var reports = cases.Select((item, index) => new ReleaseInfo
                {
                    Guid = index.ToString(), Title = item.Item1, Author = item.Author, Indexer = "Generic",
                    Categories = new List<int> { 3030 }, PublishDate = DateTime.UtcNow
                }).ToList();
                var decisions = path == 0 ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
                {
                    Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog,
                    InteractiveSearch = path == 2
                });
                for (var index = 0; index < cases.Length; index++)
                {
                    var decision = decisions.SingleOrDefault(item => item.RemoteBook.Release.Guid == index.ToString());
                    if (path == 0 && !cases[index].Item2 && cases[index].Item1 != "Frank Herbert - Dune FLAC")
                    {
                        Assert.That(decision, Is.Null, "RSS must discard unmatched identity: " + cases[index].Item1);
                    }
                    else
                    {
                        Assert.That(decision, Is.Not.Null, mode + ": " + cases[index].Item1);
                        Assert.That(decision.Approved, Is.EqualTo(cases[index].Item2), mode + ": " + cases[index].Item1);
                    }
                }

                Assert.That(((CatalogBookService)(object)books).Calls, Is.EqualTo(path == 0 ? 1 : 0));
            }
        }

        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(0, 2)]
        [TestCase(1, 2)]
        [TestCase(2, 2)]
        public void possessive_author_credits_should_preserve_title_identity_on_every_search_path(int path, int targetId)
        {
            foreach (var mode in new[] { BookMatchingStrictness.Balanced, BookMatchingStrictness.Strict })
            {
                var profile = new QualityProfile
                {
                    Id = 1, ProfileType = ProfileType.Audiobook, Cutoff = Quality.MP3.Id,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Quality = Quality.UnknownAudio, Allowed = true },
                        new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                        new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true },
                        new QualityProfileQualityItem { Quality = Quality.FLAC, Allowed = true }
                    }
                };
                var author = new Author { Id = 1, Name = "Frank Herbert", AudiobookMonitored = true, AudiobookQualityProfileId = 1, AudiobookQualityProfile = profile };
                Book MakeBook(int id, string title, string position) => new Book
                {
                    Id = id, AuthorId = author.Id, Author = author, Title = title, MediaType = BookMediaType.Audiobook,
                    AudiobookMonitored = id == targetId, AnyEditionOk = true, SeriesName = "Dune", SeriesPosition = position,
                    Editions = new List<Edition> { new Edition { Id = id, BookId = id, Title = title, Monitored = true } }
                };
                var catalog = new List<Book> { MakeBook(1, "Dune", "1"), MakeBook(2, "Dune Messiah", "2"), MakeBook(3, "Children of Dune", "3") };
                catalog[0].Editions[0].Narrator = "Scott Brick";
                var target = catalog.Single(b => b.Id == targetId);
                var (maker, books) = CreateTitleDecisionMaker(author, catalog, target, mode);
                var cases = new[]
                {
                    ("Frank Herberts Dune, Bk 1 - Dune (NMR", 1),
                    ("Frank Herberts Dune. Bk 1 - Dune (NMR", 1),
                    ("Frank Herberts Dune, Bk 2 - Dune Messiah (NMR", 2),
                    ("Frank Herberts Dune. Bk 2 - Dune Messiah (NMR", 2),
                    ("Frank Herberts Dune Messiah", 2),
                    ("Frank Herberts Dune, Bk 3 - Children of Dune (NMR", 3),
                    ("Frank Herbert's Dune, Bk 1 - Dune (NMR", 1),
                    ("Frank Herbert's Dune, Bk 2 - Dune Messiah (NMR", 2),
                    // Captured release spellings retain the existing different-author title gap.
                    ("Frank Herberts Prelude to Dune, Bk 1 - House Atreides (NMR", 1),
                    ("Frank Herbert's 'Prelude to Dune', Bk 1 - House Atreides (NMR 48 kbps)", 1),
                    ("Frank Herberts Legends of Dune, Bk 1 - The Butlerian Jihad (NMR", 1),
                    ("Frank Herbert's 'Dune' - The Legends of Dune, Bk 1 - The Butlerian Jihad NMR", 1),
                    ("Brian Herberts Dune, Bk 1 - Dune (NMR", -1),
                    ("Brian Herbert - Dune - House Atreides", -1),
                    ("Frank Hebert - Dune (NMR", -1),
                    ("Dune - Scott Brick MP3", -1)
                };
                var reports = cases.Select((item, index) => new ReleaseInfo
                {
                    Guid = index.ToString(), Title = item.Item1, Author = null, Indexer = "Generic",
                    Categories = new List<int> { 3030 }, PublishDate = DateTime.UtcNow
                }).ToList();
                var decisions = path == 0 ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
                {
                    Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog,
                    InteractiveSearch = path == 2
                });
                for (var index = 0; index < cases.Length; index++)
                {
                    var decision = decisions.SingleOrDefault(item => item.RemoteBook.Release.Guid == index.ToString());
                    if (path == 0 && cases[index].Item2 != targetId)
                    {
                        Assert.That(decision, Is.Null, "RSS must discard unmatched identity: " + cases[index].Item1);
                        continue;
                    }

                    Assert.That(decision, Is.Not.Null, $"path={path}, target={target.Title}, release={cases[index].Item1}");
                    Assert.That(decision.Approved, Is.EqualTo(cases[index].Item2 == targetId),
                        $"path={path}, target={target.Title}, mode={mode}, release={cases[index].Item1}");
                    if (decision.Approved)
                    {
                        Assert.That(decision.RemoteBook.Books.Select(book => book.Id), Is.EqualTo(new[] { targetId }));
                    }
                }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void captured_dune_companion_ebooks_should_record_current_decisions(int path)
        {
            var profile = new QualityProfile
            {
                Id = 1, ProfileType = ProfileType.Ebook, Cutoff = Quality.EPUB.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.PDF, Allowed = true }
                }
            };
            var author = new Author { Id = 1, Name = "Frank Herbert", EbookMonitored = true,
                EbookQualityProfileId = 1, EbookQualityProfile = profile };
            var target = new Book { Id = 1, AuthorId = 1, Author = author, Title = "Dune", MediaType = BookMediaType.Ebook,
                EbookMonitored = true, AnyEditionOk = true, SeriesName = "Dune", SeriesPosition = "1",
                Editions = new List<Edition> { new Edition { Id = 1, BookId = 1, Title = "Dune", Monitored = true } } };
            var catalog = new List<Book> { target };
            var (maker, _) = CreateTitleDecisionMaker(author, catalog, target, BookMatchingStrictness.Balanced);
            var titles = new[]
            {
                "Kara Kennedy - Frank Herberts Dune- A Critical Companion (pdf)",
                "The Secrets of Frank Herberts Dune- The Authorized Behind-the-Scenes Story of the New Epic Mini-Series (retail) (epub)"
            };
            titles = titles.Concat(titles.Select(title => title.Replace("Herberts", "Herbert's"))).ToArray();
            var reports = titles.Select((title, index) => new ReleaseInfo { Guid = index.ToString(), Title = title,
                Indexer = "Generic", Categories = new List<int> { 7020 }, PublishDate = DateTime.UtcNow }).ToList();
            var decisions = path == 0 ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
            {
                Author = author, Books = catalog, AuthorCatalog = catalog, InteractiveSearch = path == 2
            });
            for (var index = 0; index < titles.Length; index++)
            {
                var decision = decisions.SingleOrDefault(item => item.RemoteBook.Release.Guid == index.ToString());
                Assert.That(decision, Is.Not.Null, titles[index]);
                TestContext.Progress.WriteLine($"Companion decision: path={path}, title={titles[index]}, approved={decision.Approved}, rejections={string.Join("; ", decision.Rejections.Select(item => item.Reason))}");
                Assert.That(decision.Approved, Is.True, titles[index]);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void structured_book_evidence_should_use_the_same_title_comparison(bool structuredAuthor)
        {
            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book
            {
                Author = author, Title = "Harry Potter and the Chamber of Secrets",
                Editions = new List<Edition> { new Edition { Monitored = true, Title = "Harry Potter and the Chamber of Secrets (Narrated by Stephen Fry)", Narrator = "Stephen Fry" } }
            };
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = structuredAuthor ? "MP3 unabridged" : "J K Rowling MP3 unabridged", Book = "Harry Potter and the Chamber of Secrets", Author = structuredAuthor ? author.Name : null,
                Narrator = "Stephen Fry", Indexer = "MyAnonaMouse", Categories = new List<int> { 3030 }, PublishDate = DateTime.UtcNow
            });
            Assert.That(decision.Approved, Is.True);
            Assert.That(decision.RemoteBook.SearchCriteriaMatch.EvidenceTitle, Is.EqualTo("Harry Potter and the Chamber of Secrets"));
        }

        [TestCase("J.K. Rowling", "J K Rowling Q MP3 unabridged", false)]
        [TestCase("Ann Smith", "Mary Ann Smith MP3 unabridged", true)]
        [TestCase("John Smith", "John Smith MP3 Mary Jones", true)]
        [TestCase("John Smith", "John Smith MP3 unabridged", true)]
        [TestCase("John Mobi", "John Mobi", true)]
        [TestCase("John Mobi", "John Mobi MP3 unabridged", true)]
        [TestCase("John A. Smith", "John Q. Smith MP3 unabridged", false)]
        [TestCase("Jennifer L. Armentrout", "Armentrout, Jennifer Q. MP3", false)]
        [TestCase("Jennifer Q. Armentrout", "Armentrout, Jennifer L. MP3", false)]
        [TestCase("John Smith", "Pat Smith, John Doe MP3 unabridged", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q MP3 unabridged", false)]
        public void structured_book_raw_author_occurrence_should_allow_surrounding_words_and_keep_conflicts(string authorName, string rawTitle, bool approved)
        {
            var author = new Author { Name = authorName };
            var book = new Book
            {
                Author = author,
                Title = "The Book Title",
                Editions = new List<Edition> { new Edition { Monitored = true, Title = "The Book Title" } }
            };
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = rawTitle,
                Book = book.Title,
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Approved, Is.EqualTo(approved));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("MP3")]
        [TestCase("MP3 unabridged")]
        public void structured_book_without_a_raw_author_credit_must_not_acquire_author_evidence(string rawTitle)
        {
            var author = new Author { Name = "John Smith" };
            var book = new Book
            {
                Author = author,
                Title = "The Book Title",
                Editions = new List<Edition> { new Edition { Monitored = true, Title = "The Book Title" } }
            };
            var release = new ReleaseInfo { Title = rawTitle, Book = book.Title };
            var match = ReleaseTitleMatchScorer.FindBestMatch(release, author.Name, new[] { book }, new[] { book });

            Assert.That(match, Is.Not.Null);
            Assert.That(match.IsMatch, Is.False);
        }

        [TestCase("Frank Herbert", "Dune", "Dune by Frank Herbert EPUB", true)]
        [TestCase("John Smith", "The Book Title", "The Book Title John Smith Collection", true)]
        [TestCase("Frank Herbert", "Dune", "Dune by Frank Herbert Q EPUB", false)]
        public void raw_author_after_the_title_should_allow_surrounding_words_and_keep_initial_conflicts(string authorName, string bookTitle, string rawTitle, bool approved)
        {
            var author = new Author { Name = authorName };
            var book = new Book
            {
                Author = author,
                Title = bookTitle,
                Editions = new List<Edition> { new Edition { Monitored = true, Title = bookTitle } }
            };
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = rawTitle,
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Approved, Is.EqualTo(approved));
        }

        [Test]
        public void sibling_rejection_should_stay_within_the_media_enum_for_every_release_path(
            [Values(0, 1, 2)] int path,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool sameMediaType)
        {
            var author = new Author { Id = 1, Name = "Frank Herbert" };
            var target = new Book
            {
                Id = 1, AuthorId = author.Id, Author = author, Title = "Dune", MediaType = mediaType,
                Editions = new List<Edition> { new Edition { Id = 1, BookId = 1, Title = "Dune", Monitored = true, ReadingFormatId = 2 } }
            };
            var sibling = new Book
            {
                Id = 2, AuthorId = author.Id, Author = author, Title = "Dune Messiah",
                MediaType = sameMediaType ? mediaType : mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                Editions = new List<Edition> { new Edition { Id = 2, BookId = 2, Title = "Dune Messiah", Monitored = true, ReadingFormatId = 1 } }
            };
            var catalog = new List<Book> { target, sibling };
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = new List<Book> { target } };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = target.Id, AuthorId = author.Id, BookTitle = target.Title });
            var logger = LogManager.GetCurrentClassLogger();
            var maker = new DownloadDecisionMaker(new IDecisionEngineSpecification[] { new ReleaseTitleMatchSpecification(logger) },
                parsing, CatalogBookService.Create(catalog), fts, new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(), new NoOpReleaseNarratorMetadataEnricher(), (IConfigService)null, logger);
            var reports = new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = "Frank Herbert - Dune Messiah " + (mediaType == BookMediaType.Audiobook ? "MP3" : "EPUB"),
                    Author = author.Name, PublishDate = DateTime.UtcNow, Indexer = "Test",
                    Categories = new List<int> { mediaType == BookMediaType.Audiobook ? 3030 : 7020 }
                }
            };

            var decisions = path == 0 ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
            {
                Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog, InteractiveSearch = path == 2
            });

            Assert.That(decisions.Any(decision => decision.Approved), Is.EqualTo(!sameMediaType));
            Assert.That(decisions.Where(decision => decision.Approved).SelectMany(decision => decision.RemoteBook.Books),
                sameMediaType ? Is.Empty : Is.EqualTo(new[] { target }));
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void should_match_unicode_titles_and_continue_processing_releases(bool rss, bool emojiInRelease)
        {
            var author = new Author { Id = 1, Name = "Test Author" };
            var book = new Book
            {
                Id = 101,
                AuthorId = author.Id,
                Author = author,
                Title = emojiInRelease ? "Astorias Smutty Smut" : "Astorias Smutty Smut 🌶️",
                MediaType = BookMediaType.Ebook
            };
            author.Books = new List<Book> { book };
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = author.Books };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = book.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = book.Title });
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification> { new ReleaseTitleMatchSpecification(logger) },
                parsing, CatalogBookService.Create(parsing.MappedBooks), fts, new NoOpCustomFormatCalculationService(), new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(), (IConfigService)null, logger);
            var reports = new[] { emojiInRelease ? "Astorias Smutty Smut 🌶️" : "Astorias Smutty Smut", "Astorias Smutty Smut" }
                .Select(title => new ReleaseInfo
                {
                    Title = title,
                    Author = author.Name,
                    Indexer = "MyAnonaMouse",
                    Categories = new List<int>(),
                    PublishDate = DateTime.UtcNow
                }).ToList();

            var decisions = rss
                ? decisionMaker.GetRssDecision(reports)
                : decisionMaker.GetSearchDecision(reports, new BookSearchCriteria
                {
                    Author = author,
                    Books = author.Books
                });

            Assert.That(decisions, Has.Count.EqualTo(2));
            foreach (var decision in decisions)
            {
                Assert.That(decision.Rejections, Is.Empty);
                Assert.That(decision.RemoteBook.Books, Is.EqualTo(new[] { book }));
            }
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public void should_accept_a_structured_narrator_credit_outside_the_release_title(bool rss, bool interactive)
        {
            var author = new Author { Id = 1, Name = "J.K. Rowling", GoodreadsAuthorId = "gr:1077326" };
            var book = new Book
            {
                Id = 1,
                AuthorId = author.Id,
                Author = author,
                Title = "Harry Potter und die Kammer des Schreckens - Gesprochen von Rufus Beck",
                HardcoverBookId = "hc:429306",
                GoodreadsWorkId = "gr:167643570",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Harry Potter and the Chamber of Secrets (Narrated by Stephen Fry)",
                        ForeignEditionId = "az:B0D1CTCS5D-audiobook",
                        Monitored = true,
                        ReadingFormatId = 2,
                        Narrator = "Stephen Fry"
                    }
                }
            };
            author.Books = new List<Book> { book };
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = author.Books };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = book.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = book.Title });
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification>
                {
                    new ReleaseTitleMatchSpecification(logger, ConfigServiceTestProxy.Create(BookMatchingStrictness.Strict))
                },
                parsing, CatalogBookService.Create(parsing.MappedBooks), fts, new NoOpCustomFormatCalculationService(), new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(), (IConfigService)null, logger);
            var reports = new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = "J K Rowling read by Stephen Fry - Harry Potter and the Chamber of Secrets (2000) MP3",
                    Indexer = "DrunkenSlug (Prowlarr)",
                    Categories = new List<int> { 3030 },
                    PublishDate = DateTime.UtcNow
                }
            };

            var decisions = rss
                ? decisionMaker.GetRssDecision(reports)
                : decisionMaker.GetSearchDecision(reports, new BookSearchCriteria
                {
                    Author = author,
                    Books = author.Books,
                    UserInvokedSearch = interactive,
                    InteractiveSearch = interactive
                });

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(decisions[0].Rejections, Is.Empty);
            Assert.That(decisions[0].RemoteBook.Books, Is.EqualTo(new[] { book }));
        }

        [TestCase(false, false, BookMediaType.Audiobook)]
        [TestCase(false, true, BookMediaType.Audiobook)]
        [TestCase(true, false, BookMediaType.Audiobook)]
        [TestCase(true, true, BookMediaType.Audiobook)]
        [TestCase(false, false, BookMediaType.Ebook)]
        [TestCase(false, true, BookMediaType.Ebook)]
        [TestCase(true, false, BookMediaType.Ebook)]
        [TestCase(true, true, BookMediaType.Ebook)]
        public void should_accept_edition_derived_subtitle_fallback_through_search_rss_and_resource_mapping(bool rss, bool structured, BookMediaType mediaType)
        {
            var author = new Author { Id = 1, Name = "Shirtaloon" };
            var editionTitle = "He Who Fights with Monsters 11: A LitRPG Adventure";
            var releaseTitle = "He Who Fights with Monsters 11";
            var book = new Book
            {
                Id = 11, AuthorId = author.Id, Author = author,
                Title = "He Who Fights with Monsters", MediaType = mediaType,
                SeriesName = "He Who Fights with Monsters", SeriesPosition = "11",
                Editions = new List<Edition> { new Edition { Title = editionTitle, Monitored = true } }
            };
            author.Books = new List<Book> { book };
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = author.Books };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = book.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = book.Title });
            var logger = LogManager.GetCurrentClassLogger();
            var maker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification> { new ReleaseTitleMatchSpecification(logger) },
                parsing, CatalogBookService.Create(author.Books), fts, new NoOpCustomFormatCalculationService(), new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(), (IConfigService)null, logger);
            var format = mediaType == BookMediaType.Ebook ? "EPUB" : "M4B";
            var reports = new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = structured ? $"Upload {format}" : $"{author.Name} - {releaseTitle} {format}",
                    Book = structured ? releaseTitle : null,
                    Author = structured ? "Shirtaloon, Travis Deverell" : null,
                    Indexer = "MyAnonaMouse", PublishDate = DateTime.UtcNow,
                    Categories = new List<int> { mediaType == BookMediaType.Ebook ? 7020 : 3030 }
                }
            };
            var decisions = rss ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
            {
                Author = author, Books = author.Books, AuthorCatalog = author.Books, InteractiveSearch = true
            });

            Assert.That(decisions, Has.Count.EqualTo(1));
            var decision = decisions.Single();
            Assert.That(decision.Rejections, Is.Empty);
            Assert.That(decision.RemoteBook.Books.Single(), Is.SameAs(book));
            Assert.That(decision.RemoteBook.SearchCriteriaMatch.MatchedVariant, Is.EqualTo(releaseTitle));
            Assert.That(decision.RemoteBook.SearchCriteriaMatch.PrimaryTitle, Is.EqualTo(editionTitle));
            var resource = decision.ToResource();
            Assert.That(resource.MatchedTitle, Is.EqualTo(editionTitle));
            Assert.That(resource.BookId, Is.EqualTo(book.Id));
            Assert.That(resource.FormatType, Is.EqualTo(mediaType == BookMediaType.Ebook ? "ebook" : "audiobook"));
            if (structured)
            {
                Assert.That(resource.MatchedTitleCharStart, Is.Null);
            }
            else
            {
                Assert.That(resource.MatchedTitleCharStart, Is.Not.Null);
                Assert.That(resource.DisplayTitle.Substring(resource.MatchedTitleCharStart.Value,
                    resource.MatchedTitleCharEnd.Value - resource.MatchedTitleCharStart.Value), Is.EqualTo(releaseTitle));
            }
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void should_preserve_quoted_filename_evidence_through_search_rss_and_resource_mapping(bool rss, BookMediaType mediaType)
        {
            var author = new Author { Id = 1, Name = "Shirtaloon" };
            var book = new Book
            {
                Id = 1, AuthorId = author.Id, Author = author,
                Title = "He Who Fights with Monsters", MediaType = mediaType,
                SeriesName = "He Who Fights with Monsters", SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "He Who Fights with Monsters: A LitRPG Adventure", Monitored = true }
                }
            };
            author.Books = new List<Book> { book };
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = author.Books };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = book.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = book.Title });
            var logger = LogManager.GetCurrentClassLogger();
            var maker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification> { new ReleaseTitleMatchSpecification(logger) },
                parsing, CatalogBookService.Create(author.Books), fts, new NoOpCustomFormatCalculationService(), new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(), (IConfigService)null, logger);
            var extension = mediaType == BookMediaType.Ebook ? "epub" : "mp3";
            var reports = new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = $"[1/3] - \"Shirtaloon - He Who Fights with Monsters 01 (2021) [Chaptered].{extension}\"",
                    Indexer = "DrunkenSlug", PublishDate = DateTime.UtcNow,
                    Categories = new List<int> { mediaType == BookMediaType.Ebook ? 7020 : 3030 }
                }
            };
            var decisions = rss ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
            {
                Author = author, Books = author.Books, AuthorCatalog = author.Books, InteractiveSearch = true
            });

            var decision = decisions.Single();
            Assert.That(decision.Rejections, Is.Empty);
            Assert.That(decision.RemoteBook.Books.Single(), Is.SameAs(book));
            var resource = decision.ToResource();
            Assert.That(resource.MatchedTitle, Is.EqualTo(book.Editions.Single().Title));
            Assert.That(resource.BookId, Is.EqualTo(book.Id));
            Assert.That(resource.FormatType, Is.EqualTo(mediaType == BookMediaType.Ebook ? "ebook" : "audiobook"));
            Assert.That(resource.MatchedTitleCharStart, Is.Not.Null);
            Assert.That(resource.DisplayTitle.Substring(resource.MatchedTitleCharStart.Value,
                resource.MatchedTitleCharEnd.Value - resource.MatchedTitleCharStart.Value), Is.EqualTo(book.Title));
        }

        private static DownloadDecision RunSingleSearchDecision(Author author, Book book, ReleaseInfo report)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            return decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria)[0];
        }

        [Test]
        public void should_expose_interactive_source_before_decision_specifications_run()
        {
            var author = new Author { Id = 1, Name = "Author" };
            var book = new Book { Id = 2, Author = author, AuthorId = author.Id, Title = "Book" };
            var specification = new RecordingReleaseSourceSpecification();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification> { specification },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          LogManager.GetCurrentClassLogger());

            decisionMaker.GetSearchDecision(new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = "Author - Book EPUB",
                    Author = "Author",
                    Indexer = "MyAnonaMouse",
                    Categories = new List<int>(),
                    PublishDate = DateTime.UtcNow
                }
            }, new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                InteractiveSearch = true,
                UserInvokedSearch = true
            });

            Assert.That(specification.SeenSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
        }

        [Test]
        public void should_fallback_to_search_criteria_when_title_parse_yields_no_author()
        {
            var author = new Author
            {
                Name = "Freida McFadden"
            };

            var book = new Book
            {
                Author = author,
                AuthorId = author.Id,
                Title = "Want to Know a Secret"
            };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var report = new ReleaseInfo
            {
                Title = "Want to Know a Secret by Freida McFadden EPUB",
                Indexer = "The Pirate Bay (Prowlarr)",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();

            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>(),
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decisions = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria);

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(decisions[0].Rejections, Is.Empty);
            Assert.That(decisions[0].RemoteBook.ParsedBookInfo.AuthorName, Is.EqualTo("Freida McFadden"));
        }

        [Test]
        public void should_apply_pack_detection_to_rss_decisions()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Brandon Sanderson"
            };

            var book = new Book
            {
                Id = 101,
                Author = author,
                AuthorId = author.Id,
                Title = "The Final Empire",
                SeriesName = "Mistborn",
                SeriesPosition = "1"
            };

            author.Books = new List<Book>
            {
                book,
                new Book
                {
                    Id = 102,
                    Author = author,
                    AuthorId = author.Id,
                    Title = "The Well of Ascension",
                    SeriesName = "Mistborn",
                    SeriesPosition = "2"
                }
            };

            var parsingService = new TestParsingService
            {
                MappedAuthor = author,
                MappedBooks = new List<Book> { book }
            };

            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.Add(new BookFtsMatch
            {
                BookId = book.Id,
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookTitle = book.Title
            });

            var report = new ReleaseInfo
            {
                Title = "Brandon Sanderson - The Final Empire Mistborn Trilogy",
                Author = "Brandon Sanderson",
                Indexer = "MyAnonaMouse",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new MultiBookReleaseSpecification(logger)
                                                          },
                                                          parsingService,
                                                          CatalogBookService.Create(parsingService.MappedBooks),
                                                          ftsRepository,
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decision = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report })[0];

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.RemoteBook.PackDetection.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(decision.Rejections.Select(r => r.Reason), Is.EqualTo(new[] { "Release appears to contain multiple books" }));
        }

        [TestCase("Søren", "søren", "soren", false)]
        [TestCase("Søren", "søren", "soren", true)]
        [TestCase("Łukasz", "łukasz", "lukasz", false)]
        [TestCase("Łukasz", "łukasz", "lukasz", true)]
        [TestCase("Ælfric", "ælfric", "aelfric", false)]
        [TestCase("Ælfric", "ælfric", "aelfric", true)]
        [TestCase("Straße", "straße", "strasse", false)]
        [TestCase("Straße", "straße", "strasse", true)]
        [TestCase("Королёв", "королёв", "королев", false)]
        [TestCase("Королёв", "королёв", "королев", true)]
        [TestCase("がとう", "がとう", "がとう", false)]
        [TestCase("𠀀Reader", "𠀀reader", "𠀀reader", true)]
        public void rss_recall_should_use_shared_fold_and_preserve_raw_release_metadata(string spelling, string originalTerm, string foldedTerm, bool structuredAuthor)
        {
            var author = new Author { Id = 7, Name = spelling + " Reader" };
            var book = new Book { Id = 11, Author = author, AuthorId = author.Id, Title = "Quite Ugly One Evening", MediaType = BookMediaType.Audiobook };
            var parsingService = new TestParsingService { MappedAuthor = author, MappedBooks = new List<Book> { book } };
            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.Add(new BookFtsMatch { BookId = book.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = book.Title });
            var report = new TorrentInfo
            {
                Title = structuredAuthor ? book.Title : author.Name + " - " + book.Title,
                Author = structuredAuthor ? author.Name : null,
                Indexer = "MyAnonaMouse",
                FileType = "mp3",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            };
            var originalTitle = report.Title;
            var originalAuthor = report.Author;
            var maker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>(), parsingService,
                CatalogBookService.Create(parsingService.MappedBooks), ftsRepository,
                new NoOpCustomFormatCalculationService(), new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(), (IConfigService)null, LogManager.GetCurrentClassLogger());

            var decision = maker.GetRssDecision(new List<ReleaseInfo> { report }).Single();
            Assert.Multiple(() =>
            {
                Assert.That(ftsRepository.Requests, Has.Count.EqualTo(1));
                Assert.That(ftsRepository.Requests[0].Tokens, Does.Contain(foldedTerm));
                if (originalTerm != foldedTerm)
                {
                    Assert.That(ftsRepository.Requests[0].Tokens, Does.Not.Contain(originalTerm));
                }

                Assert.That(ftsRepository.Requests[0].MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(ftsRepository.Requests[0].MonitoredOnly, Is.True);
                Assert.That(parsingService.MappedBookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(decision.RemoteBook.Author, Is.SameAs(author));
                Assert.That(decision.RemoteBook.Books, Is.EqualTo(new[] { book }));
                Assert.That(report.Title, Is.EqualTo(originalTitle));
                Assert.That(report.Author, Is.EqualTo(originalAuthor));
                Assert.That(author.Name, Is.EqualTo(spelling + " Reader"));
                Assert.That(decision.Rejections, Is.Empty);
            });
        }

        [Test]
        public void should_use_mam_structured_author_in_monitored_fts_recall()
        {
            var author = new Author
            {
                Id = 7,
                Name = "Chris Brookmyre"
            };
            var book = new Book
            {
                Id = 11,
                Author = author,
                AuthorId = author.Id,
                Title = "Quite Ugly One Evening",
                MediaType = BookMediaType.Audiobook
            };
            var parsingService = new TestParsingService
            {
                MappedAuthor = author,
                MappedBooks = new List<Book> { book }
            };
            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.Add(new BookFtsMatch
            {
                BookId = book.Id,
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookTitle = book.Title
            });
            var report = new TorrentInfo
            {
                Title = book.Title,
                Author = author.Name,
                Indexer = "MyAnonaMouse",
                FileType = "mp3",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>(),
                                                          parsingService,
                                                          CatalogBookService.Create(parsingService.MappedBooks),
                                                          ftsRepository,
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decision = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report })[0];

            Assert.Multiple(() =>
            {
                Assert.That(ftsRepository.Requests, Has.Count.EqualTo(1));
                Assert.That(ftsRepository.Requests[0].MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(ftsRepository.Requests[0].MonitoredOnly, Is.True);
                Assert.That(ftsRepository.Requests[0].Tokens, Does.Contain("chris"));
                Assert.That(ftsRepository.Requests[0].Tokens, Does.Contain("brookmyre"));
                Assert.That(parsingService.MappedBookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(parsingService.GetAuthorCalls, Is.Zero);
                Assert.That(decision.RemoteBook.Author, Is.SameAs(author));
                Assert.That(decision.RemoteBook.Books, Is.EqualTo(new[] { book }));
                Assert.That(decision.Rejections, Is.Empty);
            });
        }

        [Test]
        public void rss_fts_shortlist_should_not_select_an_unproven_single_candidate()
        {
            var author = new Author
            {
                Id = 13,
                Name = "Jewel E. Ann"
            };
            var book = new Book
            {
                Id = 19,
                Author = author,
                AuthorId = author.Id,
                Title = "One",
                MediaType = BookMediaType.Audiobook
            };
            author.Books = new List<Book> { book };

            var parsingService = new TestParsingService
            {
                MappedBooks = new List<Book> { book }
            };
            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.Add(new BookFtsMatch
            {
                BookId = book.Id,
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookTitle = book.Title
            });
            var report = new TorrentInfo
            {
                Title = "ONE-J-Gavriel EP-NS1365-WEB-2026-ZzZz",
                Indexer = "Generic",
                FileType = "mp3",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            };
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification> { new ReleaseTitleMatchSpecification(logger) },
                parsingService,
                CatalogBookService.Create(parsingService.MappedBooks),
                ftsRepository,
                new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(),
                (IConfigService)null,
                logger);

            var decisions = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report });

            Assert.Multiple(() =>
            {
                Assert.That(ftsRepository.Requests, Has.Count.EqualTo(1));
                Assert.That(ftsRepository.Requests[0].MonitoredOnly, Is.True);
                Assert.That(parsingService.MappedBookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(decisions, Is.Empty);
            });
        }

        private sealed class RssProgressTarget : Target
        {
            public List<string> Progress { get; } = new();
            public List<string> Messages { get; } = new();
            protected override void Write(LogEventInfo logEvent)
            {
                Messages.Add(logEvent.FormattedMessage);
                if (logEvent.Properties.ContainsKey("Status")) Progress.Add(logEvent.FormattedMessage);
            }
        }

        [Test]
        public void rss_reports_matching_progress_before_each_recall_then_decision_progress()
        {
            using var factory = new LogFactory();
            var target = new RssProgressTarget();
            var logging = new LoggingConfiguration(factory);
            logging.AddRuleForAllLevels(target);
            factory.Configuration = logging;
            var recall = new TestEditionFtsRepository { OnRecall = () => target.Progress.Add("recall") };
            var maker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>(), new TestParsingService(),
                CatalogBookService.Create(new List<Book>()), recall, new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(), new NoOpReleaseNarratorMetadataEnricher(), null, factory.GetLogger("rss-progress"));
            maker.GetRssDecision(new List<ReleaseInfo>
            {
                new() { Title = "First Work EPUB", Indexer = "Fixture", PublishDate = DateTime.UtcNow },
                new() { Title = "Second Work M4B", Indexer = "Fixture", PublishDate = DateTime.UtcNow }
            });
            factory.Flush();
            Assert.That(target.Progress, Is.EqualTo(new[]
            {
                "Processing 2 releases", "Matching release 1/2", "recall", "Matching release 2/2", "recall",
                "Processing release 1/2", "Processing release 2/2"
            }));
            Assert.That(target.Messages, Has.Some.StartsWith("RSS mapping: reports=2, mapped=0, recallCalls=2, elapsedMs="));
        }

        [Test]
        public void rss_debug_logging_should_summarize_without_per_release_fts_noise()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Logging Fixture - Warmup EPUB");
            var previousConfiguration = LogManager.Configuration;
            var previousGlobalThreshold = LogManager.GlobalThreshold;

            try
            {
                var memory = ConfigureLogging(LogLevel.Debug);
                var decisions = RunRssWithoutFtsRecall();
                LogManager.Flush();

                Assert.Multiple(() =>
                {
                    Assert.That(decisions, Is.Empty);
                    Assert.That(memory.Logs.Contains("Debug|RSS decision summary: reports=1, mapped=0, unmatched=1, accepted=0, permanentlyRejected=0, temporarilyRejected=0"), Is.True, string.Join(Environment.NewLine, memory.Logs));
                    Assert.That(memory.Logs.Any(log => log.Contains("RSS FTS recall", StringComparison.Ordinal)), Is.False);
                });
            }
            finally
            {
                LogManager.GlobalThreshold = previousGlobalThreshold;
                LogManager.Configuration = previousConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void rss_trace_logging_should_retain_the_exact_unmatched_release_reason()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Logging Fixture - Warmup EPUB");
            var previousConfiguration = LogManager.Configuration;
            var previousGlobalThreshold = LogManager.GlobalThreshold;

            try
            {
                var memory = ConfigureLogging(LogLevel.Trace);
                RunRssWithoutFtsRecall();
                LogManager.Flush();

                Assert.That(memory.Logs, Has.Some.Contains("Trace|RSS FTS recall found no monitored candidates for release 'Unrelated Release EPUB'"));
            }
            finally
            {
                LogManager.GlobalThreshold = previousGlobalThreshold;
                LogManager.Configuration = previousConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void rss_summary_should_include_the_most_common_rejection_reasons()
        {
            var remoteBook = new RemoteBook();
            var decisions = new List<DownloadDecision>
            {
                new DownloadDecision(remoteBook, new Rejection("Title/Author mismatch")),
                new DownloadDecision(remoteBook, new Rejection("Title/Author mismatch")),
                new DownloadDecision(remoteBook, new Rejection("Quality not wanted", RejectionType.Temporary))
            };

            var summary = DownloadDecisionMaker.BuildDecisionSummary("RSS", 4, decisions);

            Assert.That(summary, Is.EqualTo("RSS decision summary: reports=4, mapped=3, unmatched=1, accepted=0, permanentlyRejected=2, temporarilyRejected=1; topRejections=[Title/Author mismatch (2), Quality not wanted (1)]"));
        }

        [Test]
        public void search_debug_logging_should_name_the_target_and_summarize_rejections()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Logging Fixture - Warmup EPUB");
            var previousConfiguration = LogManager.Configuration;
            var previousGlobalThreshold = LogManager.GlobalThreshold;

            try
            {
                var memory = ConfigureLogging(LogLevel.Debug);
                var author = new Author { Name = "Fiona Cole" };
                var book = new Book
                {
                    Author = author,
                    Title = "Voyeur",
                    Editions = new List<Edition> { new Edition { Title = "Voyeur", Monitored = true } }
                };

                RunSingleSearchDecision(author, book, new ReleaseInfo
                {
                    Title = "Moongarden - Voyeur (2014) MP3",
                    Author = "Moongarden",
                    Indexer = "Generic",
                    Categories = new List<int> { 3010 },
                    PublishDate = DateTime.UtcNow
                });
                LogManager.Flush();

                Assert.That(memory.Logs, Has.Some.Contains("Debug|Interactive search (author='Fiona Cole', book='Voyeur') decision summary: reports=1, mapped=1, unmatched=0, accepted=0, permanentlyRejected=1, temporarilyRejected=0; topRejections=[Author mismatch: Moongarden (1)]"));
            }
            finally
            {
                LogManager.GlobalThreshold = previousGlobalThreshold;
                LogManager.Configuration = previousConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void search_debug_logging_should_not_emit_per_release_specification_details()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Logging Fixture - Warmup EPUB");
            var previousConfiguration = LogManager.Configuration;
            var previousGlobalThreshold = LogManager.GlobalThreshold;

            try
            {
                var memory = ConfigureLogging(LogLevel.Debug);

                GetMultiFormatDecision(CreateEbookProfile(Quality.EPUB), "Learn My Lesson", "epub");
                LogManager.Flush();

                Assert.Multiple(() =>
                {
                    Assert.That(memory.Logs.Any(log => log.Contains("[QUALITY_PROFILE_CHECK]", StringComparison.Ordinal)), Is.False);
                    Assert.That(memory.Logs.Any(log => log.Contains("[TITLE-MATCH]", StringComparison.Ordinal)), Is.False);
                    Assert.That(memory.Logs.Any(log => log.Contains("Accepting ebook format", StringComparison.Ordinal)), Is.False);
                    Assert.That(memory.Logs, Has.Some.Contains("Debug|Interactive search (author='Katee Robert', book='Learn My Lesson') decision summary"));
                });
            }
            finally
            {
                LogManager.GlobalThreshold = previousGlobalThreshold;
                LogManager.Configuration = previousConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void search_trace_logging_should_retain_per_release_specification_details()
        {
            NzbDrone.Core.Parser.Parser.ParseBookTitle("Logging Fixture - Warmup EPUB");
            var previousConfiguration = LogManager.Configuration;
            var previousGlobalThreshold = LogManager.GlobalThreshold;

            try
            {
                var memory = ConfigureLogging(LogLevel.Trace);

                GetMultiFormatDecision(CreateEbookProfile(Quality.EPUB), "Learn My Lesson", "epub");
                LogManager.Flush();

                Assert.Multiple(() =>
                {
                    Assert.That(memory.Logs, Has.Some.Contains("Trace|[QUALITY_PROFILE_CHECK] ===== QUALITY PROFILE CHECK STARTED ====="));
                    Assert.That(memory.Logs, Has.Some.Contains("Trace|[QUALITY_PROFILE_CHECK] Quality EPUB v1 ACCEPTED - allowed in profile"));
                    Assert.That(memory.Logs, Has.Some.Contains("Trace|Accepting ebook format EPUB - author has ebook quality profile configured"));
                });
            }
            finally
            {
                LogManager.GlobalThreshold = previousGlobalThreshold;
                LogManager.Configuration = previousConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [TestCase(BookMatchingStrictness.Balanced, true)]
        [TestCase(BookMatchingStrictness.Strict, false)]
        public void rss_fts_shortlist_should_apply_numeric_residue_policy_to_a_wanted_book(
            BookMatchingStrictness strictness,
            bool expectedApproved)
        {
            var author = new Author
            {
                Id = 23,
                Name = "Example Author",
                AudiobookMonitored = true
            };
            var book = new Book
            {
                Id = 29,
                Author = author,
                AuthorId = author.Id,
                Title = "Example Title",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                BookFiles = new List<BookFile>()
            };
            author.Books = new List<Book> { book };

            var parsingService = new TestParsingService
            {
                MappedBooks = new List<Book> { book }
            };
            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.Add(new BookFtsMatch
            {
                BookId = book.Id,
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookTitle = book.Title
            });
            var report = new TorrentInfo
            {
                Title = "Example Author - Example Title 3 MP3",
                Author = author.Name,
                Indexer = "Generic",
                FileType = "mp3",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            };
            var logger = LogManager.GetCurrentClassLogger();
            var configService = ConfigServiceTestProxy.Create(strictness);
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification>
                {
                    new ReleaseTitleMatchSpecification(logger, configService),
                    new NzbDrone.Core.DecisionEngine.Specifications.RssSync.MonitoredBookSpecification(logger),
                    new MonitoredMediaTypeSpecification(logger)
                },
                parsingService,
                CatalogBookService.Create(parsingService.MappedBooks),
                ftsRepository,
                new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(),
                configService,
                logger);

            var decisions = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report });

            Assert.Multiple(() =>
            {
                Assert.That(ftsRepository.Requests, Has.Count.EqualTo(1));
                Assert.That(ftsRepository.Requests[0].MonitoredOnly, Is.True);
                Assert.That(parsingService.MappedBookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(book.IsMonitored(), Is.True);
                Assert.That(book.BookFiles, Is.Empty);

                if (expectedApproved)
                {
                    var decision = decisions.Single();
                    Assert.That(decision.Approved, Is.True);
                    Assert.That(decision.RemoteBook.SearchCriteriaMatch.ProblemCode, Is.EqualTo(TitleMatchProblemCode.SuspiciousAdjacentNumber));
                }
                else
                {
                    Assert.That(decisions, Is.Empty);
                }
            });
        }

        [Test]
        public void rss_fts_shortlist_should_keep_a_specific_title_above_its_generic_sibling()
        {
            var author = new Author
            {
                Id = 17,
                Name = "Frank Herbert"
            };
            var genericBook = new Book
            {
                Id = 21,
                Author = author,
                AuthorId = author.Id,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook
            };
            var specificBook = new Book
            {
                Id = 22,
                Author = author,
                AuthorId = author.Id,
                Title = "Dune Messiah",
                MediaType = BookMediaType.Audiobook
            };
            author.Books = new List<Book> { genericBook, specificBook };

            var parsingService = new TestParsingService
            {
                MappedBooks = new List<Book> { genericBook, specificBook }
            };
            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.AddRange(new[]
            {
                new BookFtsMatch { BookId = genericBook.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = genericBook.Title },
                new BookFtsMatch { BookId = specificBook.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = specificBook.Title }
            });
            var report = new TorrentInfo
            {
                Title = "Frank Herbert - Dune Messiah MP3",
                Author = author.Name,
                Indexer = "Generic",
                FileType = "mp3",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            };
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification> { new ReleaseTitleMatchSpecification(logger) },
                parsingService,
                CatalogBookService.Create(parsingService.MappedBooks),
                ftsRepository,
                new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(),
                (IConfigService)null,
                logger);

            var decision = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report }).Single();

            Assert.Multiple(() =>
            {
                Assert.That(parsingService.MappedBookIds, Is.EqualTo(new[] { genericBook.Id, specificBook.Id }));
                Assert.That(decision.RemoteBook.Books.Single(), Is.SameAs(specificBook));
                Assert.That(decision.Rejections, Is.Empty);
            });
        }

        [Test]
        public void rss_fts_shortlist_should_use_structured_author_to_disambiguate_identical_titles()
        {
            var expectedAuthor = new Author
            {
                Id = 31,
                Name = "Stephen King"
            };
            var otherAuthor = new Author
            {
                Id = 32,
                Name = "Jane Doe"
            };
            var expectedBook = new Book
            {
                Id = 41,
                Author = expectedAuthor,
                AuthorId = expectedAuthor.Id,
                Title = "The Stand",
                MediaType = BookMediaType.Audiobook
            };
            var otherBook = new Book
            {
                Id = 42,
                Author = otherAuthor,
                AuthorId = otherAuthor.Id,
                Title = "The Stand",
                MediaType = BookMediaType.Audiobook
            };
            expectedAuthor.Books = new List<Book> { expectedBook };
            otherAuthor.Books = new List<Book> { otherBook };

            var parsingService = new TestParsingService
            {
                MappedBooks = new List<Book> { otherBook, expectedBook }
            };
            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.AddRange(new[]
            {
                new BookFtsMatch { BookId = otherBook.Id, AuthorId = otherAuthor.Id, AuthorName = otherAuthor.Name, BookTitle = otherBook.Title },
                new BookFtsMatch { BookId = expectedBook.Id, AuthorId = expectedAuthor.Id, AuthorName = expectedAuthor.Name, BookTitle = expectedBook.Title }
            });
            var report = new TorrentInfo
            {
                Title = "Stephen King - The Stand MP3",
                Author = expectedAuthor.Name,
                Indexer = "Generic",
                FileType = "mp3",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            };
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification> { new ReleaseTitleMatchSpecification(logger) },
                parsingService,
                CatalogBookService.Create(parsingService.MappedBooks),
                ftsRepository,
                new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(),
                (IConfigService)null,
                logger);

            var decision = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report }).Single();

            Assert.Multiple(() =>
            {
                Assert.That(decision.RemoteBook.Author, Is.SameAs(expectedAuthor));
                Assert.That(decision.RemoteBook.Books.Single(), Is.SameAs(expectedBook));
                Assert.That(decision.Rejections, Is.Empty);
            });
        }

        [Test]
        public void should_reject_structured_author_mismatch_during_interactive_search()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Fiona Cole"
            };

            var book = new Book
            {
                Id = 1,
                Author = author,
                AuthorId = author.Id,
                Title = "Voyeur"
            };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var report = new ReleaseInfo
            {
                Title = "Moongarden - Voyeur (2014) MP3",
                Author = "Moongarden",
                Indexer = "DrunkenSlug (Prowlarr)",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();

            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decisions = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria);

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(decisions[0].Rejected, Is.True);
            Assert.That(decisions[0].Rejections.Select(r => r.Reason), Has.Some.EqualTo("Author mismatch: Moongarden"));
            Assert.That(decisions[0].RemoteBook.ParsedBookInfo.BookTitle, Is.EqualTo("Moongarden - Voyeur (2014)"));
        }

        [Test]
        public void should_label_interactive_alias_match_with_primary_monitored_title()
        {
            var author = new Author
            {
                Id = 33,
                Name = "J.K. Rowling"
            };

            var book = new Book
            {
                Id = 1327,
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = true,
                Author = author,
                AuthorId = author.Id,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Harry Potter and the Sorcerer's Stone",
                        Monitored = true
                    },
                    new Edition
                    {
                        Id = 2,
                        Title = "Harry Potter and the Philosopher's Stone"
                    }
                }
            };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var report = new ReleaseInfo
            {
                Title = "Harry Potter and the Philosopher's Stone (aka Harry Potter and the Sorcerer's Stone)",
                Author = "J K Rowling",
                Indexer = "MyAnonaMouse",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();

            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decisions = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria);

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(decisions[0].Rejections, Is.Empty);
            Assert.That(decisions[0].RemoteBook.ParsedBookInfo.BookTitle, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
        }

        [Test]
        public void should_match_interactive_and_automatic_search_viability_for_short_title_false_positive()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Fiona Cole"
            };

            var book = new Book
            {
                Id = 1,
                Author = author,
                AuthorId = author.Id,
                Title = "Voyeur"
            };

            var interactiveCriteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var automaticCriteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = false,
                InteractiveSearch = false
            };

            var report = new ReleaseInfo
            {
                Title = "Moongarden - Voyeur (2014) MP3",
                Author = "Fiona Cole",
                Indexer = "DrunkenSlug (Prowlarr)",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var specs = new List<IDecisionEngineSpecification>
            {
                new ReleaseTitleMatchSpecification(logger)
            };

            var decisionMaker = new DownloadDecisionMaker(specs,
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var interactiveDecision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, interactiveCriteria)[0];
            var automaticDecision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, automaticCriteria)[0];

            Assert.That(interactiveDecision.Approved, Is.EqualTo(automaticDecision.Approved));
            Assert.That(interactiveDecision.Rejections.Select(r => r.Reason), Is.EqualTo(automaticDecision.Rejections.Select(r => r.Reason)));
            Assert.That(interactiveDecision.RemoteBook.ParsedBookInfo.BookTitle, Is.EqualTo(automaticDecision.RemoteBook.ParsedBookInfo.BookTitle));
        }

        [Test]
        public void should_match_interactive_and_automatic_search_primary_title_for_alias_release()
        {
            var author = new Author
            {
                Id = 33,
                Name = "J.K. Rowling"
            };

            var book = new Book
            {
                Id = 1327,
                Title = "Harry Potter and the Philosopher's Stone",
                AnyEditionOk = true,
                Author = author,
                AuthorId = author.Id,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Title = "Harry Potter and the Sorcerer's Stone",
                        Monitored = true
                    },
                    new Edition
                    {
                        Id = 2,
                        Title = "Harry Potter and the Philosopher's Stone"
                    }
                }
            };

            var interactiveCriteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var automaticCriteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = false,
                InteractiveSearch = false
            };

            var report = new ReleaseInfo
            {
                Title = "Harry Potter and the Philosopher's Stone (aka Harry Potter and the Sorcerer's Stone)",
                Author = "J K Rowling",
                Indexer = "MyAnonaMouse",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var specs = new List<IDecisionEngineSpecification>
            {
                new ReleaseTitleMatchSpecification(logger)
            };

            var decisionMaker = new DownloadDecisionMaker(specs,
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var interactiveDecision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, interactiveCriteria)[0];
            var automaticDecision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, automaticCriteria)[0];

            Assert.That(interactiveDecision.Approved, Is.EqualTo(automaticDecision.Approved));
            Assert.That(interactiveDecision.RemoteBook.ParsedBookInfo.BookTitle, Is.EqualTo("Harry Potter and the Sorcerer's Stone"));
            Assert.That(interactiveDecision.RemoteBook.ParsedBookInfo.BookTitle, Is.EqualTo(automaticDecision.RemoteBook.ParsedBookInfo.BookTitle));
        }

        [Test]
        public void should_defer_title_matching_rejection_to_explicit_pack_spec_for_multi_book_search_result()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Pierce Brown"
            };

            var book = new Book
            {
                Id = 5094,
                Author = author,
                AuthorId = author.Id,
                Title = "Red Rising",
                SeriesName = "Red Rising",
                SeriesPosition = "1"
            };

            author.Books = new List<Book>
            {
                book,
                new Book
                {
                    Id = 7245,
                    Author = author,
                    AuthorId = author.Id,
                    Title = "Golden Son",
                    SeriesName = "Red Rising",
                    SeriesPosition = "2"
                }
            };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var report = new ReleaseInfo
            {
                Title = "Pierce Brown Red Rising and Golden Son Book 1 and 2",
                Author = "Pierce Brown",
                Indexer = "NZBgeek (Prowlarr)",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var specs = new List<IDecisionEngineSpecification>
            {
                new MultiBookReleaseSpecification(logger),
                new ReleaseTitleMatchSpecification(logger)
            };

            var decisionMaker = new DownloadDecisionMaker(specs,
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria)[0];

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.RemoteBook.PackDetection.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(decision.Rejections.Select(r => r.Reason), Is.EqualTo(new[] { "Release appears to contain multiple books" }));
        }

        [Test]
        public void should_not_null_ref_when_mam_fallback_parsing_builds_minimal_parsed_book_info()
        {
            var author = new Author
            {
                Name = "Travis Beacham"
            };

            var book = new Book
            {
                Author = author,
                AuthorId = author.Id,
                Title = "Impact Winter"
            };

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var report = new TorrentInfo
            {
                Title = "Impact Winter: Evenfall",
                Author = "Travis Beacham",
                Indexer = "MyAnonaMouse",
                FileType = "cbr",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();

            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>(),
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decisions = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria);

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(decisions[0].RemoteBook.ParsedBookInfo, Is.Not.Null);
            Assert.That(decisions[0].RemoteBook.ParsedBookInfo.Quality, Is.Not.Null);
        }

        [Test]
        public void should_use_allowed_detected_quality_for_mam_multi_format_search_result()
        {
            var ebookProfile = CreateEbookProfile(Quality.EPUB);
            var author = new Author
            {
                Id = 68,
                Name = "Katee Robert",
                EbookQualityProfileId = ebookProfile.Id,
                EbookQualityProfile = new LazyLoaded<QualityProfile>(ebookProfile)
            };

            var book = new Book
            {
                Id = 901,
                Author = author,
                AuthorId = author.Id,
                Title = "Learn My Lesson",
                MediaType = BookMediaType.Ebook
            };

            author.Books = new List<Book> { book };

            var report = new TorrentInfo
            {
                Title = "Learn My Lesson",
                Author = "Katee Robert",
                Indexer = "MyAnonaMouse",
                FileType = "azw3 epub mobi",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new EbookFormatSpecification(logger),
                                                              new QualityAllowedByProfileSpecification(logger),
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var decision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria)[0];

            Assert.That(decision.Rejections, Is.Empty);
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.DetectedQualities,
                Is.EquivalentTo(new[] { Quality.AZW3, Quality.EPUB, Quality.MOBI }));
        }

        [TestCase("mobi-preferred", nameof(Quality.MOBI))]
        [TestCase("epub-preferred", nameof(Quality.EPUB))]
        public void should_promote_to_the_format_the_user_ranked_highest(string ranking, string expectedQualityName)
        {
            // Same release, same allowed set — only the user's ordering differs, and it alone decides
            // which format is taken. Nothing in the pipeline may prefer a format on its own.
            var ebookProfile = ranking == "mobi-preferred"
                ? CreateRankedEbookProfile(Quality.PDF, Quality.AZW3, Quality.EPUB, Quality.MOBI)
                : CreateRankedEbookProfile(Quality.PDF, Quality.AZW3, Quality.MOBI, Quality.EPUB);

            var decision = GetMultiFormatDecision(ebookProfile, "Learn My Lesson [azw3 epub mobi]", fileType: null);

            Assert.That(decision.Rejections, Is.Empty);
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality.Name, Is.EqualTo(expectedQualityName));
        }

        [Test]
        public void should_detect_a_multi_format_list_from_a_non_mam_title()
        {
            // The 2026-06-20 promotion only ever saw MAM's structured FileType. A plain Usenet title
            // advertising the same bundle now feeds it too.
            var ebookProfile = CreateEbookProfile(Quality.EPUB);

            var decision = GetMultiFormatDecision(ebookProfile, "Learn My Lesson [azw3 epub mobi]", fileType: null);

            Assert.That(decision.Rejections, Is.Empty);
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.DetectedQualities,
                Is.EquivalentTo(new[] { Quality.AZW3, Quality.EPUB, Quality.MOBI }));
        }

        [Test]
        public void should_reject_a_request_post_whose_payload_extension_is_not_allowed()
        {
            // The reported release: prose asks for an epub, the payload is a mobi. The payload wins,
            // so an EPUB-only profile never grabs it.
            var ebookProfile = CreateEbookProfile(Quality.EPUB);

            var decision = GetMultiFormatDecision(
                ebookProfile,
                "Learn My Lesson, epub, please...thanks - Katee Robert - Learn My Lesson/Katee Robert - Learn My Lesson.mobi",
                fileType: null);

            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.MOBI));
            Assert.That(decision.Rejections, Is.Not.Empty);
        }

        private static DownloadDecision GetMultiFormatDecision(QualityProfile ebookProfile, string title, string fileType)
        {
            var author = new Author
            {
                Id = 68,
                Name = "Katee Robert",
                EbookQualityProfileId = ebookProfile.Id,
                EbookQualityProfile = new LazyLoaded<QualityProfile>(ebookProfile)
            };

            var book = new Book
            {
                Id = 901,
                Author = author,
                AuthorId = author.Id,
                Title = "Learn My Lesson",
                MediaType = BookMediaType.Ebook
            };

            author.Books = new List<Book> { book };

            var report = new TorrentInfo
            {
                Title = title,
                Author = "Katee Robert",
                Indexer = fileType == null ? "NZBgeek" : "MyAnonaMouse",
                FileType = fileType,
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new EbookFormatSpecification(logger),
                                                              new QualityAllowedByProfileSpecification(logger),
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            return decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria)[0];
        }

        [Test]
        public void should_not_promote_or_accept_when_no_detected_quality_is_allowed()
        {
            var ebookProfile = CreateEbookProfile(Quality.EPUB);
            var author = new Author
            {
                Id = 68,
                Name = "Katee Robert",
                EbookQualityProfileId = ebookProfile.Id,
                EbookQualityProfile = new LazyLoaded<QualityProfile>(ebookProfile)
            };

            var book = new Book
            {
                Id = 901,
                Author = author,
                AuthorId = author.Id,
                Title = "Learn My Lesson",
                MediaType = BookMediaType.Ebook
            };

            author.Books = new List<Book> { book };

            var report = new TorrentInfo
            {
                Title = "Learn My Lesson",
                Author = "Katee Robert",
                Indexer = "MyAnonaMouse",
                FileType = "azw3 mobi",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new EbookFormatSpecification(logger),
                                                              new QualityAllowedByProfileSpecification(logger),
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
                                                          CatalogBookService.Create(new List<Book>()),
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                UserInvokedSearch = true,
                InteractiveSearch = true
            };

            var decision = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria)[0];

            // No detected format is allowed (EPUB-only profile, release is azw3+mobi):
            // primary must stay AZW3 (no over-promotion) and the release must be rejected.
            Assert.That(decision.Rejections, Is.Not.Empty);
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.AZW3));
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.DetectedQualities,
                Is.EquivalentTo(new[] { Quality.AZW3, Quality.MOBI }));
        }

        [Test]
        public void should_use_allowed_detected_quality_for_mam_multi_format_rss_result()
        {
            var ebookProfile = CreateEbookProfile(Quality.EPUB);
            var author = new Author
            {
                Id = 68,
                Name = "Katee Robert",
                EbookQualityProfileId = ebookProfile.Id,
                EbookQualityProfile = new LazyLoaded<QualityProfile>(ebookProfile)
            };

            var book = new Book
            {
                Id = 901,
                Author = author,
                AuthorId = author.Id,
                Title = "Learn My Lesson",
                MediaType = BookMediaType.Ebook
            };

            var parsingService = new TestParsingService
            {
                MappedAuthor = author,
                MappedBooks = new List<Book> { book }
            };

            var ftsRepository = new TestEditionFtsRepository();
            ftsRepository.Recalls.Add(new BookFtsMatch
            {
                BookId = book.Id,
                AuthorId = author.Id,
                AuthorName = author.Name,
                BookTitle = book.Title
            });

            var report = new TorrentInfo
            {
                Title = "Learn My Lesson",
                Author = "Katee Robert",
                Indexer = "MyAnonaMouse",
                FileType = "azw3 epub mobi",
                Categories = new List<int>(),
                PublishDate = DateTime.UtcNow
            };

            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new EbookFormatSpecification(logger),
                                                              new QualityAllowedByProfileSpecification(logger)
                                                          },
                                                          parsingService,
                                                          CatalogBookService.Create(parsingService.MappedBooks),
                                                          ftsRepository,
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decision = decisionMaker.GetRssDecision(new List<ReleaseInfo> { report })[0];

            Assert.That(decision.Rejections, Is.Empty);
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(decision.RemoteBook.ParsedBookInfo.Quality.DetectedQualities,
                Is.EquivalentTo(new[] { Quality.AZW3, Quality.EPUB, Quality.MOBI }));
        }

        [TestCase("Armentrout, Jennifer L. - Verlockende Angst")]
        [TestCase("Armentrout, Jennifer L. - Daemonentochter 02 - Verlockende Angst")]
        [TestCase("Daemonentochter 02 Verlockende Angst Armentrout, Jennifer L")]
        [TestCase("Armentrout,.Jennifer.L.Dämonentochter.02.Verlockende.Angst.Fantasy")]
        [TestCase("Jennifer.L.Armentrout.Daemonentochter.02.Verlockende.Angst")]
        [TestCase("Armentrout, Jennifer L. - Verlockende Angst 7")]
        [TestCase("Armentrout, Jennifer L. Verlockende Angst 7")]
        [TestCase("Armentrout,.Jennifer.L.Verlockende.Angst.7")]
        [TestCase("Verlockende Angst 7 - Armentrout, Jennifer L.")]
        [TestCase("Verlockende Angst 7 Armentrout, Jennifer L.")]
        [TestCase("Verlockende Angst 7 - Jennifer L. Armentrout")]
        [TestCase("Jennifer L. Armentrout - Verlockende Angst 7")]
        [TestCase("Ⅵ 🌙 Armentrout, Jennifer L. - Verlockende Angst 7")]
        public void complete_source_author_name_should_reach_release_resource_without_comma_rejection(string releaseTitle)
        {
            var (author, book) = CreateCommaAuthorTarget();
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = releaseTitle,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, decision.RemoteBook.SearchCriteriaMatch);
            var resource = decision.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
                Assert.That(decision.Approved, Is.True);
                Assert.That(resource.Rejections, Is.Empty);
                Assert.That(resource.HasSoftFilters, Is.False);
                Assert.That(resource.HasHardFilters, Is.False);
            });
        }

        [TestCase("Armentrout.Jennifer.L.Daemonentochter.02.Verlockende.Angst")]
        [TestCase("Jennifer.L.Armentrout.Dämonentochter.02.Verlockende.Angst")]
        public void screenshot_dot_separated_author_title_forms_should_keep_their_existing_approval(string releaseTitle)
        {
            var (author, book) = CreateCommaAuthorTarget();
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = releaseTitle,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.ToResource().Rejections, Is.Empty);
        }

        [TestCase(0, BookMatchingStrictness.Balanced, true)]
        [TestCase(1, BookMatchingStrictness.Balanced, true)]
        [TestCase(2, BookMatchingStrictness.Balanced, true)]
        [TestCase(2, BookMatchingStrictness.Strict, false)]
        public void comma_author_source_span_should_be_shared_by_rss_and_search_without_weakening_strict_mode(int path, BookMatchingStrictness strictness, bool expectedAccepted)
        {
            var (author, book) = CreateCommaAuthorTarget();
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = author.Books };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = book.Id, AuthorId = author.Id, AuthorName = author.Name, BookTitle = book.Title });
            var logger = LogManager.GetCurrentClassLogger();
            var config = ConfigServiceTestProxy.Create(strictness);
            var maker = new DownloadDecisionMaker(new IDecisionEngineSpecification[] { new ReleaseTitleMatchSpecification(logger, config) },
                parsing, CatalogBookService.Create(author.Books), fts, new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(), new NoOpReleaseNarratorMetadataEnricher(), config, logger);
            var reports = new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = "Armentrout, Jennifer L. Verlockende Angst 7",
                    Indexer = "Generic",
                    Categories = new List<int> { 7020 },
                    PublishDate = DateTime.UtcNow
                }
            };
            var decisions = path == 0 ? maker.GetRssDecision(reports) : maker.GetSearchDecision(reports, new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                AuthorCatalog = author.Books,
                InteractiveSearch = path == 2
            });
            var decision = decisions.Single();
            var resource = decision.ToResource();

            Assert.That(decision.Approved, Is.EqualTo(expectedAccepted));
            Assert.That(resource.Rejections.Any(), Is.EqualTo(!expectedAccepted));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void known_author_alias_away_from_title_should_preserve_same_media_sibling_rejection(bool includeSibling)
        {
            var author = new Author { Id = 1, Name = "J.K. Rowling", Pseudonyms = new List<string> { "Joanne Rowling" } };
            var title = "Harry Potter and the Philosopher's Stone";
            var book = new Book
            {
                Id = 1, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, Title = title,
                Editions = new List<Edition> { new Edition { Id = 1, Title = title, Monitored = true } }
            };
            author.Books = new List<Book> { book };
            if (includeSibling)
            {
                author.Books.Add(new Book
                {
                    Id = 2, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, Title = title + " Study Guide",
                    Editions = new List<Edition> { new Edition { Id = 2, Title = title + " Study Guide", Monitored = true } }
                });
            }

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Rowling, Joanne - [Hogwarts] - " + title + (includeSibling ? " Study Guide" : string.Empty),
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, decision.RemoteBook.SearchCriteriaMatch);
            var resource = decision.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.True);
                Assert.That(decision.Approved, Is.EqualTo(!includeSibling));
                Assert.That(resource.Rejections.Any(), Is.EqualTo(includeSibling));
            });
        }

        [TestCase("Armentrout, Jennifer L. - Verlockende Angst 7", "Jennifer Q. Armentrout", "Author mismatch: Jennifer Q. Armentrout")]
        [TestCase("Armentrout, Jennifer L. - Verlockende Angst 7", "Other Writer", "Author mismatch: Other Writer")]
        [TestCase("Other Writer - Verlockende Angst 7", null, "Unexplained number adjacent to matched title")]
        [TestCase("Armentrout, Jennifer L. - Verlockende", null, "Title/Author mismatch")]
        [TestCase("Armentrout, Jennifer L.", null, "Title/Author mismatch")]
        public void source_author_name_must_not_bypass_conflicting_author_or_incomplete_title(string releaseTitle, string structuredAuthor, string expectedReason)
        {
            var (author, book) = CreateCommaAuthorTarget();
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = releaseTitle,
                Author = structuredAuthor,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var resource = decision.ToResource();

            Assert.Multiple(() =>
            {
                Assert.That(decision.Rejected, Is.True);
                Assert.That(resource.Rejections, Does.Contain(expectedReason));
                Assert.That(resource.RejectionDetails.Select(rejection => rejection.Reason), Does.Contain(expectedReason));
            });
        }

        [TestCase("Armentrout, Jennifer L.")]
        [TestCase("Other Writer, Jennifer L. Armentrout")]
        [TestCase("Jennifer L. Armentrout, Other Writer")]
        public void structured_author_names_and_comma_contributors_should_keep_their_existing_proof(string structuredAuthor)
        {
            var (author, book) = CreateCommaAuthorTarget();
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Verlockende Angst 7",
                Author = structuredAuthor,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.ToResource().Rejections, Is.Empty);
        }

        [TestCase(BookMediaType.Ebook, false, "Armentrout, Jennifer L. - Verlockende Angst Erwacht")]
        [TestCase(BookMediaType.Audiobook, true, "Armentrout, Jennifer L. - Verlockende Angst Erwacht")]
        [TestCase(BookMediaType.Ebook, false, "Armentrout, Jennifer L. - Verlockende Angst 7 - Verlockende Angst Erwacht")]
        public void comma_author_proof_must_preserve_same_media_better_title_competition(BookMediaType siblingMediaType, bool expectedAccepted, string releaseTitle)
        {
            var (author, book) = CreateCommaAuthorTarget();
            author.Books.Add(new Book
            {
                Id = 2,
                AuthorId = author.Id,
                Author = author,
                MediaType = siblingMediaType,
                Title = "Verlockende Angst Erwacht",
                Editions = new List<Edition> { new Edition { Id = 2, Title = "Verlockende Angst Erwacht", Monitored = true } }
            });
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = releaseTitle,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var resource = decision.ToResource();

            Assert.That(decision.Approved, Is.EqualTo(expectedAccepted));
            if (releaseTitle.Contains(" 7 ", StringComparison.Ordinal))
            {
                Assert.That(decision.RemoteBook.SearchCriteriaMatch.Problems.Select(problem => problem.Code),
                    Does.Contain(TitleMatchProblemCode.SuspiciousAdjacentNumber).And.Contain(TitleMatchProblemCode.SiblingTitleContradiction));
            }

            if (expectedAccepted)
            {
                Assert.That(resource.Rejections, Is.Empty);
            }
            else
            {
                Assert.That(resource.Rejections, Does.Contain("Release appears to match a different book by this author"));
            }
        }

        [Test]
        public void comma_author_proof_must_not_erase_a_conflicting_series_position()
        {
            var (author, book) = CreateCommaAuthorTarget();
            book.Title = "Covenant";
            book.Editions[0].Title = book.Title;
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Armentrout, Jennifer L. - Covenant 3",
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.ToResource().Rejections, Does.Contain("Series position mismatch"));
        }

        [Test]
        public void a_name_inside_the_matched_title_must_not_supply_separate_author_proof()
        {
            var (author, book) = CreateCommaAuthorTarget();
            book.Title = author.Name;
            book.Editions[0].Title = author.Name;
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Jennifer L. Armentrout 7",
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, decision.RemoteBook.SearchCriteriaMatch);

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.False);
                Assert.That(decision.ToResource().Rejections, Does.Contain("Unexplained number adjacent to matched title"));
            });
        }

        [TestCase("John Smith", "Pat Smith, John Doe", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("John Smith", "Pat.Smith,John.Doe", false)]
        [TestCase("John Smith", "Pat_Smith,John_Doe", false)]
        [TestCase("John Smith", "Pat Smith; John Doe", false)]
        [TestCase("John Smith", "Pat Smith John Doe", false)]
        [TestCase("John Smith", "Smith.John", true)]
        [TestCase("John Smith", "Smith, John", true)]
        [TestCase("John Smith", "John Smith, Jane Doe", true)]
        [TestCase("John Smith", "Pat Doe, John Smith", true)]
        [TestCase("Лев Толстой", "Толстой, Лев", true)]
        [TestCase("Лев Толстой", "Лев Толстой", true)]
        [TestCase("And Smith", "And Smith", true)]
        [TestCase("John Smith", "John and Smith", true)]
        [TestCase("John Smith", "Pat Smith and John Doe", false)]
        [TestCase("Pat Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", true)]
        [TestCase("Ann Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", false)]
        [TestCase("Ann and Pat Smith", "Ann and Pat Smith", true)]
        public void shared_author_proof_should_use_one_complete_name_through_release_resource(string expectedAuthor, string credit, bool expectedAccepted)
        {
            var author = new Author { Id = 1, Name = expectedAuthor };
            var book = new Book
            {
                Id = 1, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, Title = "A Northern Journey",
                Editions = new List<Edition> { new Edition { Id = 1, Title = "A Northern Journey", Monitored = true } }
            };
            author.Books = new List<Book> { book };
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = credit + " - A Northern Journey",
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var match = decision.RemoteBook.SearchCriteriaMatch;
            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, match);

            Assert.Multiple(() =>
            {
                Assert.That(match.IsMatch, Is.EqualTo(expectedAccepted));
                Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(expectedAccepted));
                Assert.That(decision.Approved, Is.EqualTo(expectedAccepted));
                Assert.That(decision.ToResource().Rejections.Any(), Is.EqualTo(!expectedAccepted));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void provider_identifier_alias_must_not_supply_a_person_name(bool structured)
        {
            var (author, book) = CreateCommaAuthorTarget();
            author.Aliases = new List<string> { "hc:221618" };
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "hc:221618 - Verlockende Angst",
                Author = structured ? "hc:221618" : null,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });
            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, decision.RemoteBook.SearchCriteriaMatch);

            Assert.Multiple(() =>
            {
                Assert.That(identity.HasAuthorIdentityEvidence, Is.False);
                Assert.That(decision.Approved, Is.False);
                Assert.That(decision.ToResource().Rejections, Is.Not.Empty);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void matched_title_must_not_supply_its_own_author_even_with_structured_book(bool structuredBook, bool separateAuthorCredit)
        {
            var author = new Author { Id = 1, Name = "John Smith" };
            var title = "A Journey with John Smith";
            var book = new Book
            {
                Id = 1, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, Title = title,
                Editions = new List<Edition> { new Edition { Id = 1, Title = title, Monitored = true } }
            };
            author.Books = new List<Book> { book };
            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = (separateAuthorCredit ? "Smith, John - " : string.Empty) + title,
                Book = structuredBook ? title : null,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.Multiple(() =>
            {
                Assert.That(decision.RemoteBook.SearchCriteriaMatch.IsMatch, Is.EqualTo(separateAuthorCredit));
                Assert.That(decision.Approved, Is.EqualTo(separateAuthorCredit));
                Assert.That(decision.ToResource().Rejections.Any(), Is.EqualTo(!separateAuthorCredit));
            });
        }

        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("Robert Louis Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("Robert Louis Stevenson", "Robert Q. Stevenson", false)]
        [TestCase("J K Rowling", "Joanne Rowling", true)]
        [TestCase("JK Rowling", "J K Rowling", true)]
        [TestCase("Clive Staples Lewis", "C S Lewis", true)]
        [TestCase("Clive Staples Lewis", "CS Lewis", false)]
        [TestCase("John Smith", "James O H N Smith", false)]
        [TestCase("JOHN SMITH", "James O H N Smith", false)]
        [TestCase("John Smith", "John Smith's", true)]
        // One trailing surname s is compatible in either direction.
        [TestCase("John Smith", "John Smiths", true)]
        public void common_author_policy_should_agree_for_raw_and_structured_credits(string expectedAuthor, string credit, bool accepted)
        {
            foreach (var structured in new[] { false, true })
            {
                foreach (var numbered in new[] { false, true })
                {
                    var (author, book) = CreateCommaAuthorTarget();
                    author.Name = expectedAuthor;
                    var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
                    {
                        Title = credit + " - Verlockende Angst" + (numbered ? " 7" : string.Empty),
                        Author = structured ? credit : null,
                        Indexer = "Generic",
                        Categories = new List<int> { 7020 },
                        PublishDate = DateTime.UtcNow
                    });
                    var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, decision.RemoteBook.SearchCriteriaMatch);
                    Assert.Multiple(() =>
                    {
                        Assert.That(identity.HasAuthorIdentityEvidence, Is.EqualTo(accepted), $"author structured={structured}, numbered={numbered}");
                        Assert.That(decision.Approved, Is.EqualTo(accepted), $"decision structured={structured}, numbered={numbered}");
                        Assert.That(decision.ToResource().Rejections.Any(), Is.EqualTo(!accepted));
                    });
                }
            }
        }

        [TestCase("J K Rowling", "Joanne Rowling", "J Q Rowling", false)]
        [TestCase("J K Rowling", "Joanne Rowling", "JQ Rowling", false)]
        [TestCase("J K Rowling", "Joanne Rowling", "Joanne Q. Rowling", false)]
        [TestCase("J K Rowling", "Joanne Rowling", "Joanne Rowling", true)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", "Robert Q Stevenson", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", "Robert Stevenson", true)]
        public void shortened_known_spelling_must_not_erase_a_conflicting_part(string canonical, string alternate, string credit, bool accepted)
        {
            foreach (var structured in new[] { false, true })
            {
                var (author, book) = CreateCommaAuthorTarget();
                author.Name = canonical;
                author.Pseudonyms = new List<string> { alternate };
                var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
                {
                    Title = credit + " - Verlockende Angst", Author = structured ? credit : null,
                    Indexer = "Generic", Categories = new List<int> { 7020 }, PublishDate = DateTime.UtcNow
                });
                Assert.That(decision.Approved, Is.EqualTo(accepted));
                Assert.That(decision.ToResource().Rejections.Any(), Is.EqualTo(!accepted));
            }
        }

        [TestCase("Robert Louis Stevenson", "Robert Stevenson")]
        [TestCase("J K Rowling", "Robert Galbraith")]
        public void stored_alternate_names_should_supply_evidence_for_different_full_names(string canonical, string alternate)
        {
            foreach (var structured in new[] { false, true })
            {
                var (author, book) = CreateCommaAuthorTarget();
                author.Name = canonical;
                author.Pseudonyms = new List<string> { alternate };
                var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
                {
                    Title = alternate + " - Verlockende Angst", Author = structured ? alternate : null,
                    Indexer = "Generic", Categories = new List<int> { 7020 }, PublishDate = DateTime.UtcNow
                });
                Assert.That(decision.Approved, Is.True);
                Assert.That(decision.ToResource().Rejections, Is.Empty);
            }
        }

        private static (Author Author, Book Book) CreateCommaAuthorTarget()
        {
            var author = new Author { Id = 1, Name = "Jennifer L. Armentrout" };
            var book = new Book
            {
                Id = 1,
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Ebook,
                Title = "Verlockende Angst",
                SeriesName = "Covenant",
                SeriesPosition = "2",
                Editions = new List<Edition> { new Edition { Id = 1, Title = "Verlockende Angst", Language = "deu", Monitored = true } }
            };
            author.Books = new List<Book> { book };
            return (author, book);
        }

        [Test]
        public void should_reject_music_artist_release_without_expected_author_evidence()
        {
            var author = new Author { Name = "George R.R. Martin" };
            var book = new Book
            {
                Author = author,
                Title = "Fire & Blood",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Fire & Blood", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Viserion-Fire and Blood-EP-24BIT-WEB-FLAC-2026-ENTiTLED",
                Indexer = "NinjaCentral (Prowlarr)",
                Categories = new List<int> { 3010 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
            Assert.That(decision.RemoteBook.ParsedBookInfo.AuthorName, Is.EqualTo("Viserion"));
        }

        [Test]
        public void should_reject_same_long_title_from_a_different_author()
        {
            var author = new Author { Name = "George R.R. Martin" };
            var book = new Book
            {
                Author = author,
                Title = "Fire & Blood",
                SeriesName = "A Targaryen History",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Fire & Blood", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "T.R. Fehrenbach - Fire and Blood: A History of Mexico 2014 Retail EPUB",
                Author = string.Empty,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, decision.RemoteBook.SearchCriteriaMatch);
            Assert.Multiple(() =>
            {
                Assert.That(decision.RemoteBook.ParsedBookInfo.AuthorName, Is.Empty);
                Assert.That(decision.RemoteBook.SearchCriteriaMatch.IsMatch, Is.False, "the title scorer must not manufacture expected-author evidence");
                Assert.That(identity.HasAuthorIdentityEvidence, Is.False, "the identity layer must not mistake unrelated metadata for author evidence");
                Assert.That(decision.Rejected, Is.True);
            });
        }

        [Test]
        public void should_require_author_evidence_regardless_of_title_length()
        {
            var author = new Author { Name = "Mitch Albom" };
            var book = new Book
            {
                Author = author,
                Title = "The Five People You Meet in Heaven",
                Editions = new List<Edition>
                {
                    new Edition { Title = "The Five People You Meet in Heaven", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "The Five People You Meet in Heaven (2004)",
                Author = "",
                Indexer = "NZBgeek (Prowlarr)",
                Categories = new List<int> { 3010 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
        }

        [Test]
        public void should_reject_a_different_author_before_a_long_title_and_year()
        {
            var author = new Author { Name = "Louise Penny" };
            var book = new Book
            {
                Author = author,
                Title = "All the Devils Are Here",
                Editions = new List<Edition>
                {
                    new Edition { Title = "All the Devils Are Here", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Bethany McLean - All the Devils Are Here 2010 Retail EPUB",
                Author = string.Empty,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
        }

        [Test]
        public void should_accept_swedish_release_title_with_scandinavian_transliteration()
        {
            var author = new Author { Name = "Jonna Björnstjerna" };
            var book = new Book
            {
                Author = author,
                Title = "Sagan om den underbara familjen Kanin och mumiens återkomst",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Sagan om den underbara familjen Kanin och mumiens återkomst", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Jonna.Bjornstjerna.Sagan.om.den.underbara.familjen.Kanin.och.mumiens.aaterkomst.2021",
                Author = "Jonna Bjornstjerna",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_prefix_noise_before_author_and_title()
        {
            var author = new Author { Name = "Sally Rooney" };
            var book = new Book
            {
                Author = author,
                Title = "Normal People",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Normal People", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "AudioBookBay - Sally Rooney - Normal People",
                Indexer = "Generic",
                Categories = new List<int> { 3010 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_known_narrator_prefix_before_title()
        {
            var author = new Author { Name = "Stephen King" };
            var book = new Book
            {
                Author = author,
                Title = "It",
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = "It",
                        Monitored = true,
                        NarratorNames = new List<string> { "Will Patton" }
                    }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Will Patton - It - Stephen King",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_format_prefix_before_author_and_title()
        {
            var author = new Author { Name = "Sally Rooney" };
            var book = new Book
            {
                Author = author,
                Title = "Normal People",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Normal People", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Audiobook - Sally Rooney - Normal People",
                Indexer = "Generic",
                Categories = new List<int> { 3010 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_format_token_and_author_in_same_prefix_segment()
        {
            var author = new Author { Name = "Andy Weir" };
            var book = new Book
            {
                Author = author,
                Title = "Project Hail Mary",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Project Hail Mary", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "[M4B] Andy Weir-Project Hail Mary",
                Indexer = "NZBgeek (Prowlarr)",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_author_after_title_at_right_boundary()
        {
            var author = new Author { Name = "Andy Weir" };
            var book = new Book
            {
                Author = author,
                Title = "Project Hail Mary",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Project Hail Mary", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Project Hail Mary - Andy Weir - 2024",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_by_author_suffix_after_title()
        {
            var author = new Author { Name = "Andy Weir" };
            var book = new Book
            {
                Author = author,
                Title = "Project Hail Mary",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Project Hail Mary", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Project.Hail.Mary.by.Andy.Weir",
                Indexer = "NZBgeek (Prowlarr)",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_multi_author_left_boundary_when_expected_author_is_first_contributor()
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Author = author,
                Title = "The Gathering Storm",
                Editions = new List<Edition>
                {
                    new Edition { Title = "The Gathering Storm", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Brandon Sanderson & Robert Jordan - The Gathering Storm",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_multi_author_right_boundary_when_expected_author_is_second_contributor()
        {
            var author = new Author { Name = "Brandon Sanderson" };
            var book = new Book
            {
                Author = author,
                Title = "The Gathering Storm",
                Editions = new List<Edition>
                {
                    new Edition { Title = "The Gathering Storm", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "The Gathering Storm - Robert Jordan & Brandon Sanderson",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_multi_author_left_boundary_with_and_separator()
        {
            var author = new Author { Name = "Stephen King" };
            var book = new Book
            {
                Author = author,
                Title = "In the Tall Grass",
                Editions = new List<Edition>
                {
                    new Edition { Title = "In the Tall Grass", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Stephen King and Joe Hill - In the Tall Grass",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_multi_author_left_boundary_with_comma_separator()
        {
            var author = new Author { Name = "Stephen King" };
            var book = new Book
            {
                Author = author,
                Title = "In the Tall Grass",
                Editions = new List<Edition>
                {
                    new Edition { Title = "In the Tall Grass", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Stephen King, Joe Hill - In the Tall Grass (2012) MP3",
                Indexer = "DrunkenSlug (Prowlarr)",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_accept_series_metadata_between_author_and_title()
        {
            var author = new Author { Name = "Frank Herbert" };
            var book = new Book
            {
                Author = author,
                Title = "Dune Messiah",
                SeriesName = "Dune",
                SeriesPosition = "2",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Dune Messiah", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Frank Herbert's 'Dune', Bk 2 - Dune Messiah (NMR 56 kbps) \"Dune Messiah.vol01+02.PAR2\" 03/86",
                Indexer = "DrunkenSlug (Prowlarr)",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            if (decision.Rejections.Any())
            {
                Assert.Fail($"Rejections={string.Join("|", decision.Rejections.Select(r => r.Reason))} Match={decision.RemoteBook.SearchCriteriaMatch?.IsMatch} Problem={decision.RemoteBook.SearchCriteriaMatch?.ProblemCode} Variant={decision.RemoteBook.SearchCriteriaMatch?.MatchedVariant} Span={decision.RemoteBook.SearchCriteriaMatch?.MatchedStart}-{decision.RemoteBook.SearchCriteriaMatch?.MatchedEnd} Leftovers={string.Join(",", decision.RemoteBook.SearchCriteriaMatch?.MeaningfulLeftovers ?? new List<string>())}");
            }
        }

        [TestCase("Louise Penny - [Chief Inspector Gamache 16] - All the Devils Are Here (retail)")]
        [TestCase("Louise.Penny.-.[Chief.Inspector.Gamache.16].-.All.the.Devils.Are.Here.(UK).")]
        [TestCase("Louise Penny - [Gamache 16] - All the Devils Are Here")]
        [TestCase("Louise Penny - [Gamache 15] - All the Devils Are Here")]
        [TestCase("Louise Penny - [Chief Inspector Armand Gamahce 16] - All the Devils Are Here")]
        [TestCase("Louise Penny - [Inspector Rebus 16] - All the Devils Are Here")]
        public void should_accept_exact_author_and_edition_title_without_punishing_extra_metadata(string releaseTitle)
        {
            var author = new Author { Name = "Louise Penny" };
            var book = new Book
            {
                Author = author,
                Title = "All the Devils are Here",
                SeriesName = "Chief Inspector Armand Gamache",
                SeriesPosition = "16",
                Editions = new List<Edition>
                {
                    new Edition { Title = "All the Devils are Here", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = releaseTitle,
                Indexer = "abNZB (Prowlarr)",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_reject_shortened_series_as_the_only_authorless_identity_evidence()
        {
            var author = new Author { Name = "Louise Penny" };
            var book = new Book
            {
                Author = author,
                Title = "All the Devils are Here",
                SeriesName = "Chief Inspector Armand Gamache",
                SeriesPosition = "16",
                Editions = new List<Edition>
                {
                    new Edition { Title = "All the Devils are Here", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "[Chief Inspector Gamache 16] - All the Devils Are Here",
                Author = string.Empty,
                Indexer = "abNZB (Prowlarr)",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Unexplained number adjacent to matched title"));
        }

        [Test]
        public void should_not_treat_one_middle_series_word_as_authorless_identity_proof()
        {
            var author = new Author { Name = "Louise Penny" };
            var book = new Book
            {
                Author = author,
                Title = "All the Devils are Here",
                SeriesName = "Chief Inspector Armand Gamache",
                SeriesPosition = "16",
                Editions = new List<Edition>
                {
                    new Edition { Title = "All the Devils are Here", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "[Inspector 16] - All the Devils Are Here",
                Author = string.Empty,
                Indexer = "Generic",
                Categories = new List<int> { 7020 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Unexplained number adjacent to matched title"));
        }

        /// <summary>
        /// An ebook profile whose ranking is exactly the order given, worst first — i.e. whatever the
        /// user dragged into place. Everything listed is allowed.
        /// </summary>
        private static QualityProfile CreateRankedEbookProfile(params Quality[] worstToBest)
        {
            return new QualityProfile
            {
                Id = 1,
                Name = "User Ranked",
                ProfileType = ProfileType.Ebook,
                Cutoff = worstToBest.First().Id,
                Items = worstToBest
                    .Select(quality => new QualityProfileQualityItem { Quality = quality, Allowed = true })
                    .ToList()
            };
        }

        private static QualityProfile CreateEbookProfile(params Quality[] allowedQualities)
        {
            var allowedIds = allowedQualities.Select(q => q.Id).ToHashSet();

            return new QualityProfile
            {
                Id = 1,
                Name = "EPUB Only",
                ProfileType = ProfileType.Ebook,
                Cutoff = Quality.EPUB.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    CreateQualityItem(Quality.Unknown, allowedIds),
                    CreateQualityItem(Quality.PDF, allowedIds),
                    CreateQualityItem(Quality.MOBI, allowedIds),
                    CreateQualityItem(Quality.EPUB, allowedIds),
                    CreateQualityItem(Quality.AZW3, allowedIds)
                }
            };
        }

        private static QualityProfileQualityItem CreateQualityItem(Quality quality, HashSet<int> allowedIds)
        {
            return new QualityProfileQualityItem
            {
                Quality = quality,
                Allowed = allowedIds.Contains(quality.Id)
            };
        }

        [TestCase(false)]
        [TestCase(true)]
        public void series_boundary_requires_author_evidence(bool structuredAuthor)
        {
            var author = new Author { Name = "J.K. Rowling" };
            var book = new Book
            {
                Author = author,
                Title = "Harry Potter and the Goblet of Fire",
                SeriesName = "Harry Potter",
                SeriesPosition = "4",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Harry Potter and the Goblet of Fire", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Harry Potter 4 - Harry Potter and the Goblet of Fire",
                Author = structuredAuthor ? author.Name : null,
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Approved, Is.EqualTo(structuredAuthor));
        }

        [Test]
        public void should_reject_music_artist_prefix_without_expected_author_evidence()
        {
            var author = new Author { Name = "George R.R. Martin" };
            var book = new Book
            {
                Author = author,
                Title = "Fire & Blood",
                SeriesName = "A Targaryen History",
                SeriesPosition = "1",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Fire & Blood", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Viserion-Fire and Blood-EP-WEB-FLAC-2026-ENTiTLED",
                Author = string.Empty,
                Indexer = "Generic",
                Categories = new List<int> { 3010 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
        }

        [Test]
        public void should_reject_music_artist_suffix_without_expected_author_evidence()
        {
            var author = new Author { Name = "Sally Rooney" };
            var book = new Book
            {
                Author = author,
                Title = "Normal People",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Normal People", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Normal People - Paragon - WEB - 2014",
                Author = string.Empty,
                Indexer = "Generic",
                Categories = new List<int> { 3010 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
        }

        [Test]
        public void should_accept_exact_author_and_title_without_punishing_extra_metadata()
        {
            var author = new Author { Name = "Andy Weir" };
            var book = new Book
            {
                Author = author,
                Title = "Project Hail Mary",
                Editions = new List<Edition>
                {
                    new Edition { Title = "Project Hail Mary", Monitored = true }
                }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Andy Weir Collection-Project Hail Mary",
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
        }

        [Test]
        public void should_reject_a_displayed_sibling_that_contains_the_target_title()
        {
            var author = new Author { Name = "Stephen King" };
            var book = new Book
            {
                Author = author,
                Title = "It",
                Editions = new List<Edition>
                {
                    new Edition { Title = "It", Monitored = true }
                }
            };

            author.Books = new List<Book>
            {
                book,
                new Book { Id = 2, Author = author, Title = "If It Bleeds", Editions = new List<Edition> { new Edition { Title = "If It Bleeds", Monitored = true } } }
            };

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Stephen King - If It Bleeds-AUDiOBOOK-WEB-EN-2020-OLDSWE iNT-xpost",
                Author = "Stephen King",
                Indexer = "Nzb.su (Prowlarr)",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Release appears to match a different book by this author"));
        }

        private static List<DownloadDecision> RunRssWithoutFtsRecall()
        {
            var logger = LogManager.GetLogger(nameof(DownloadDecisionMakerParsingFallbackFixture));
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification>(),
                new TestParsingService(),
                CatalogBookService.Create(new List<Book>()),
                new TestEditionFtsRepository(),
                new NoOpCustomFormatCalculationService(),
                new NoOpRemoteBookAggregationService(),
                new NoOpReleaseNarratorMetadataEnricher(),
                (IConfigService)null,
                logger);

            return decisionMaker.GetRssDecision(new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = "Unrelated Release EPUB",
                    Indexer = "Generic",
                    Categories = new List<int> { 3010 },
                    PublishDate = DateTime.UtcNow
                }
            });
        }

        private static MemoryTarget ConfigureLogging(LogLevel minimumLevel)
        {
            var memory = new MemoryTarget("release-evaluation-memory")
            {
                Layout = "${level}|${message}"
            };
            var configuration = new LoggingConfiguration();
            configuration.AddRule(minimumLevel, LogLevel.Fatal, memory);
            LogManager.GlobalThreshold = minimumLevel;
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();
            return memory;
        }

        [TestCase(true, true, null)]
        [TestCase(true, false, null)]
        [TestCase(false, true, null)]
        [TestCase(false, false, null)]
        [TestCase(true, true, "Size")]
        [TestCase(true, true, "Seeders")]
        [TestCase(true, true, "Blocklist")]
        [TestCase(true, true, "Queue")]
        [TestCase(true, true, "History")]
        [TestCase(true, true, "General")]
        public void narrator_enrichment_should_require_identity_and_only_allow_rescuable_score_rejections(
            bool interactiveSearch,
            bool anyVisible, string otherRejection)
        {
            var format = new CustomFormat { Id = 1, Name = "Preferred narrator" };
            var profile = new QualityProfile
            {
                Id = 1,
                ProfileType = ProfileType.Audiobook,
                Cutoff = Quality.MP3.Id,
                MinFormatScore = 10,
                FormatItems = new List<ProfileFormatItem> { new ProfileFormatItem { Format = format, Score = 10 } },
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.FLAC, Allowed = false }
                }
            };
            var author = new Author { Id = 1, Name = "Frank Herbert", AudiobookMonitored = true, AudiobookQualityProfileId = 1, AudiobookQualityProfile = profile };
            Book MakeBook(int id, string title, bool monitored) => new Book
            {
                Id = id, AuthorId = author.Id, Author = author, Title = title, MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = monitored, AnyEditionOk = true,
                Editions = new List<Edition> { new Edition { Id = id, BookId = id, Title = title, Monitored = true } }
            };
            var target = MakeBook(1, "Dune", true);
            var catalog = new List<Book> { target, MakeBook(3, "Children of Dune", false) };
            var parsing = new TestParsingService { MappedAuthor = author, MappedBooks = catalog };
            var fts = new TestEditionFtsRepository();
            fts.Recalls.Add(new BookFtsMatch { BookId = target.Id, AuthorId = author.Id, BookTitle = target.Title });
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced);
            var logger = LogManager.GetCurrentClassLogger();
            var enricher = new RecordingReleaseNarratorMetadataEnricher();
            var specifications = new List<IDecisionEngineSpecification>
            {
                new ReleaseTitleMatchSpecification(logger, config),
                new MultiBookReleaseSpecification(logger),
                new QualityAllowedByProfileSpecification(logger),
                new CustomFormatAllowedbyProfileSpecification()
            };
            if (otherRejection != null)
            {
                specifications.Add(new FixedRejectionSpecification(otherRejection));
            }

            var formats = new NoOpCustomFormatCalculationService
            {
                ParseRelease = remote => remote.Release.Narrator == "Scott Brick" ? new List<CustomFormat> { format } : new()
            };
            var maker = new DownloadDecisionMaker(specifications, parsing, CatalogBookService.Create(catalog), fts,
                formats, new NoOpRemoteBookAggregationService(), enricher, config, logger);

            var titles = anyVisible
                ? new[] { "Frank Herbert - Dune MP3", "Frank Herbert - Children of Dune MP3", "Frank Herbert - Dune Trilogy MP3" }
                : new[] { "Frank Herbert - Children of Dune MP3", "Frank Herbert - Dune Trilogy MP3" };
            titles = titles.Concat(new[] { "Brian Herbert - Dune MP3", "Frank Herbert - Dune FLAC" }).ToArray();
            var reports = titles.Select((title, index) => new ReleaseInfo
            {
                Guid = index.ToString(), Title = title, Author = null, Indexer = "Generic",
                Categories = new List<int> { 3030 }, PublishDate = DateTime.UtcNow
            }).ToList();

            var decisions = maker.GetSearchDecision(reports, new BookSearchCriteria
            {
                Author = author, Books = new List<Book> { target }, AuthorCatalog = catalog, InteractiveSearch = interactiveSearch
            });

            Assert.That(decisions.Count, Is.EqualTo(titles.Length));

            var identityRejection = decisions.Single(decision =>
                decision.RemoteBook.Release.Title == "Frank Herbert - Children of Dune MP3");
            var packRejection = decisions.Single(decision =>
                decision.RemoteBook.Release.Title == "Frank Herbert - Dune Trilogy MP3");
            Assert.That(InteractiveSearchVisibility.IsHidden(identityRejection), Is.True);
            Assert.That(identityRejection.Rejections.Select(rejection => rejection.Category), Does.Contain("Matching"));
            Assert.That(InteractiveSearchVisibility.IsHidden(packRejection), Is.True);
            Assert.That(packRejection.Rejections.Select(rejection => rejection.Category), Does.Contain("Pack"));

            Assert.That(decisions.Single(decision => decision.RemoteBook.Release.Title == "Brian Herbert - Dune MP3").Rejected, Is.True);
            var qualityRejected = decisions.Single(decision => decision.RemoteBook.Release.Title == "Frank Herbert - Dune FLAC");
            Assert.That(qualityRejected.Rejections.Select(rejection => rejection.Category), Does.Contain("Quality"));
            Assert.That(qualityRejected.RemoteBook.Release.Narrator, Is.Null);

            if (anyVisible && otherRejection == null)
            {
                Assert.That(enricher.OfferedTitles, Is.EqualTo(new[] { "Frank Herbert - Dune MP3" }));
                Assert.That(enricher.CallCount, Is.EqualTo(1));
                var accepted = decisions.Single(decision => decision.RemoteBook.Release.Title == "Frank Herbert - Dune MP3");
                Assert.That(accepted.Approved, Is.True);
                Assert.That(accepted.RemoteBook.CustomFormatScore, Is.EqualTo(10));
            }
            else
            {
                Assert.That(enricher.OfferedTitles, Is.Empty);
                Assert.That(enricher.CallCount, Is.Zero);
            }
        }

    }
}
