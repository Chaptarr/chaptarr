using System;
using System.Collections.Generic;
using System.Linq;
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

            public List<Book> GetBooks(ParsedBookInfo parsedBookInfo, Author author, SearchCriteriaBase searchCriteria = null)
            {
                return searchCriteria?.Books ?? new List<Book>();
            }

            public Book GetLocalBook(string filename, Author author)
            {
                return null;
            }
        }

        private sealed class TestEditionFtsRepository : IEditionFtsRepository, IStagedEditionFtsRepository
        {
            public List<BookFtsMatch> Recalls { get; } = new();
            public List<(BookMediaType MediaType, bool MonitoredOnly, List<string> Tokens)> Requests { get; } = new();

            public bool FtsTableExists() => true;

            public void RebuildIndex()
            {
            }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                throw new AssertionException("RSS must use staged book recall.");
            }

            public List<BookFtsMatch> RecallBooks(
                int? authorId,
                IEnumerable<string> tokens,
                BookMediaType mediaType,
                Action<EditionFtsTraceEvent> trace = null,
                int limit = 20,
                bool monitoredOnly = false)
            {
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
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
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
            public void EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
            {
            }
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

        private static DownloadDecision RunSingleSearchDecision(Author author, Book book, ReleaseInfo report)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var decisionMaker = new DownloadDecisionMaker(new List<IDecisionEngineSpecification>
                                                          {
                                                              new ReleaseTitleMatchSpecification(logger)
                                                          },
                                                          new TestParsingService(),
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
                    Author = "Fiona Cole",
                    Indexer = "Generic",
                    Categories = new List<int> { 3010 },
                    PublishDate = DateTime.UtcNow
                });
                LogManager.Flush();

                Assert.That(memory.Logs, Has.Some.Contains("Debug|Interactive search (author='Fiona Cole', book='Voyeur') decision summary: reports=1, mapped=1, unmatched=0, accepted=0, permanentlyRejected=1, temporarilyRejected=0; topRejections=[Title/Author mismatch (1)]"));
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
                AudiobookMonitorExisting = 2
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
        public void should_hide_short_title_false_positive_during_interactive_search()
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
                Author = "Fiona Cole",
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
                                                          new TestEditionFtsRepository(),
                                                          new NoOpCustomFormatCalculationService(),
                                                          new NoOpRemoteBookAggregationService(),
                                                          new NoOpReleaseNarratorMetadataEnricher(),
                                                          (IConfigService)null,
                                                          logger);

            var decisions = decisionMaker.GetSearchDecision(new List<ReleaseInfo> { report }, criteria);

            Assert.That(decisions, Has.Count.EqualTo(1));
            Assert.That(decisions[0].Rejected, Is.True);
            Assert.That(decisions[0].Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
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

            var identity = ReleaseIdentityEvidence.Analyze(decision.RemoteBook.Release, author, book, decision.RemoteBook.SearchCriteriaMatch);
            Assert.Multiple(() =>
            {
                Assert.That(decision.RemoteBook.ParsedBookInfo.AuthorName, Is.Empty);
                Assert.That(decision.RemoteBook.SearchCriteriaMatch.IsMatch, Is.False, "the title scorer must not manufacture expected-author evidence");
                Assert.That(identity.HasPositiveIdentityEvidence, Is.False, "the identity layer must not mistake unrelated metadata for author/series evidence");
                Assert.That(decision.Rejected, Is.True);
            });
        }

        [Test]
        public void should_accept_authorless_long_title_release()
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

            Assert.That(decision.Rejections, Is.Empty);
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
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
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
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
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

        [Test]
        public void should_accept_series_boundary_identity_without_structured_author()
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
                Author = string.Empty,
                Indexer = "Generic",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejections, Is.Empty);
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
        public void should_soft_reject_embedded_one_word_title_false_positive()
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

            var decision = RunSingleSearchDecision(author, book, new ReleaseInfo
            {
                Title = "Stephen King - If It Bleeds-AUDiOBOOK-WEB-EN-2020-OLDSWE iNT-xpost",
                Author = "Stephen King",
                Indexer = "Nzb.su (Prowlarr)",
                Categories = new List<int> { 3030 },
                PublishDate = DateTime.UtcNow
            });

            Assert.That(decision.Rejected, Is.True);
            Assert.That(decision.Rejections.Select(r => r.Reason), Has.Some.EqualTo("Title/Author mismatch"));
        }

        private static List<DownloadDecision> RunRssWithoutFtsRecall()
        {
            var logger = LogManager.GetLogger(nameof(DownloadDecisionMakerParsingFallbackFixture));
            var decisionMaker = new DownloadDecisionMaker(
                new List<IDecisionEngineSpecification>(),
                new TestParsingService(),
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
    }
}
