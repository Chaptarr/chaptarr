using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.MatchBench;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    [NonParallelizable]
    public class TitleNumericEquivalenceReleaseDecisionFixture
    {
        private static readonly BookMatchingStrictness[] MatchingStrictnesses =
        {
            BookMatchingStrictness.Aggressive,
            BookMatchingStrictness.Balanced,
            BookMatchingStrictness.Strict
        };

        private sealed class NoOpCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author author) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author author) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author author) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        private sealed class NoOpRemoteBookAggregationService : IRemoteBookAggregationService
        {
            public RemoteBook Augment(RemoteBook remoteBook) => remoteBook;
        }

        private sealed class NoOpReleaseNarratorMetadataEnricher : IReleaseNarratorMetadataEnricher
        {
            public IReadOnlyCollection<ReleaseInfo> EnrichReleaseNarratorMetadata(List<ReleaseInfo> releases, SearchCriteriaBase searchCriteria)
            {
                return Array.Empty<ReleaseInfo>();
            }
        }

        private sealed class TestDelayProfileService : IDelayProfileService
        {
            public DelayProfile Add(DelayProfile profile, int? order = null) => throw new NotSupportedException();
            public DelayProfile Update(DelayProfile profile) => throw new NotSupportedException();
            public void Delete(int id) => throw new NotSupportedException();
            public void Delete(IEnumerable<int> ids) => throw new NotSupportedException();
            public List<DelayProfile> All() => throw new NotSupportedException();
            public DelayProfile Get(int id) => throw new NotSupportedException();
            public List<DelayProfile> AllForTag(int tagId) => throw new NotSupportedException();
            public List<DelayProfile> AllForTags(HashSet<int> tagIds) => throw new NotSupportedException();
            public List<DelayProfile> Reorder(int id, int? afterId) => throw new NotSupportedException();

            public DelayProfile BestForTags(HashSet<int> tagIds)
            {
                return new DelayProfile { PreferredProtocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent };
            }
        }

        [Test]
        public void english_cardinal_words_digits_and_roman_forms_match_complete_book_titles_in_every_strictness_mode()
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 501, Name = "Ernest Cline" };
                var target = MakeBook(5001, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5001");
                var sibling = MakeBook(5002, author, "Ready Player One", "English", BookMediaType.Ebook, "hc:5002");
                var catalog = AttachCatalog(author, target, sibling);

                foreach (var spelling in new[] { "Ready Player Two", "Ready Player 2", "Ready Player II", "Ready Player Ⅱ" })
                {
                    var decision = Decide(author, target, catalog, $"Ernest Cline - {spelling} (epub)", "Ernest Cline", strictness);
                    Assert.That(decision.Accepted, Is.True, $"{strictness}: {spelling}");
                }
            }
        }

        [Test]
        public void english_one_and_digit_one_match_but_unlisted_words_and_ordinals_do_not()
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 502, Name = "Ernest Cline" };
                var target = MakeBook(5011, author, "Ready Player One", "English", BookMediaType.Ebook, "hc:5011");
                var catalog = AttachCatalog(author, target);

                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player One (epub)", "Ernest Cline", strictness).Accepted,
                    Is.True, $"{strictness}: literal one");
                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player 1 (epub)", "Ernest Cline", strictness).Accepted,
                    Is.True, $"{strictness}: digit one");
                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player First (epub)", "Ernest Cline", strictness).Accepted,
                    Is.False, $"{strictness}: ordinal word");
                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player 1st (epub)", "Ernest Cline", strictness).Accepted,
                    Is.False, $"{strictness}: ordinal suffix");
                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player (epub)", "Ernest Cline", strictness).Accepted,
                    Is.False, $"{strictness}: missing title number");

                var twoTarget = MakeBook(5111, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5111");
                var twoCatalog = AttachCatalog(author, twoTarget);
                Assert.That(Decide(author, twoTarget, twoCatalog, "Ernest Cline - Ready Player Second (epub)", "Ernest Cline", strictness).Accepted,
                    Is.False, $"{strictness}: ordinal word");
                Assert.That(Decide(author, twoTarget, twoCatalog, "Ernest Cline - Ready Player 2nd (epub)", "Ernest Cline", strictness).Accepted,
                    Is.False, $"{strictness}: ordinal suffix");
            }
        }

        [Test]
        public void compound_english_cardinal_matches_its_number_but_not_two_separate_numbers()
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 512, Name = "Ernest Cline" };
                var target = MakeBook(5112, author, "Ready Player Twenty One", "English", BookMediaType.Ebook, "hc:5112");
                var catalog = AttachCatalog(author, target);

                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player 21 (epub)", "Ernest Cline", strictness).Accepted,
                    Is.True, $"{strictness}: compound cardinal");
                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player 20 1 (epub)", "Ernest Cline", strictness).Accepted,
                    Is.False, $"{strictness}: two adjacent numbers");
            }
        }

        [TestCase(null)]
        [TestCase("unknown")]
        [TestCase("French")]
        public void number_words_are_ignored_without_a_listed_edition_language(string language)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 503, Name = "Ernest Cline" };
                var target = MakeBook(5021, author, "Ready Player Two", language, BookMediaType.Ebook, "hc:5021");
                var catalog = AttachCatalog(author, target);

                var word = Decide(author, target, catalog, "Ernest Cline - Ready Player Two (epub)", "Ernest Cline", strictness);
                var digit = Decide(author, target, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline", strictness);

                Assert.Multiple(() =>
                {
                    Assert.That(word.Accepted, Is.True, $"{strictness}, {language}: literal word");
                    Assert.That(digit.Accepted, Is.False, $"{strictness}, {language}: unlisted word alias");
                });
            }
        }

        [TestCase(null)]
        [TestCase("und")]
        public void english_sibling_cardinal_blocks_unknown_language_digit_alias_but_not_its_literal(string unknownLanguage)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 514, Name = "Ernest Cline" };
                var digitTarget = MakeBook(5131, author, "Ready Player 2", unknownLanguage, BookMediaType.Ebook, "hc:5131");
                var englishSibling = MakeBook(5132, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5132");
                var catalog = AttachCatalog(author, digitTarget, englishSibling);

                var romanAlias = Decide(author, digitTarget, catalog, "Ernest Cline - Ready Player II (epub)", "Ernest Cline", strictness);
                var literalTitle = Decide(author, digitTarget, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline", strictness);

                Assert.Multiple(() =>
                {
                    Assert.That(romanAlias.Accepted, Is.False, $"{strictness}, {unknownLanguage}: sibling cardinal blocks Roman alias");
                    Assert.That(literalTitle.Accepted, Is.True, $"{strictness}, {unknownLanguage}: literal digit title");
                });
            }
        }

        [TestCase(null)]
        [TestCase("und")]
        public void english_word_target_and_unknown_language_digit_sibling_keep_literal_priority(string unknownLanguage)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 515, Name = "Ernest Cline" };
                var wordTarget = MakeBook(5141, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5141");
                var unknownSibling = MakeBook(5142, author, "Ready Player 2", unknownLanguage, BookMediaType.Ebook, "hc:5142");
                var catalog = AttachCatalog(author, wordTarget, unknownSibling);

                var romanAlias = Decide(author, wordTarget, catalog, "Ernest Cline - Ready Player II (epub)", "Ernest Cline", strictness);
                var literalTitle = Decide(author, wordTarget, catalog, "Ernest Cline - Ready Player Two (epub)", "Ernest Cline", strictness);

                Assert.Multiple(() =>
                {
                    Assert.That(romanAlias.Accepted, Is.False, $"{strictness}, {unknownLanguage}: sibling digit blocks Roman alias");
                    Assert.That(literalTitle.Accepted, Is.True, $"{strictness}, {unknownLanguage}: literal word title");
                });
            }
        }

        [TestCase(null)]
        [TestCase("und")]
        public void unknown_language_word_title_alone_does_not_gain_numeric_or_roman_aliases(string language)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 516, Name = "Ernest Cline" };
                var wordTarget = MakeBook(5151, author, "Ready Player Two", language, BookMediaType.Ebook, "hc:5151");
                var catalog = AttachCatalog(author, wordTarget);

                Assert.Multiple(() =>
                {
                    Assert.That(Decide(author, wordTarget, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline", strictness).Accepted,
                        Is.False, $"{strictness}, {language}: unknown-language digit alias");
                    Assert.That(Decide(author, wordTarget, catalog, "Ernest Cline - Ready Player II (epub)", "Ernest Cline", strictness).Accepted,
                        Is.False, $"{strictness}, {language}: unknown-language Roman alias");
                });
            }
        }

        [TestCase(null)]
        [TestCase("unknown")]
        [TestCase("French")]
        public void digit_and_roman_equivalence_does_not_depend_on_language(string language)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 504, Name = "Ernest Cline" };
                var target = MakeBook(5031, author, "Ready Player 2", language, BookMediaType.Ebook, "hc:5031");
                var catalog = AttachCatalog(author, target);

                Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player II (epub)", "Ernest Cline", strictness).Accepted,
                    Is.True, $"{strictness}, {language}");
            }
        }

        [Test]
        public void literal_roman_looking_word_remains_valid_title_content()
        {
            var author = new Author { Id = 505, Name = "Ernest Cline" };
            var target = MakeBook(5041, author, "Vi", "English", BookMediaType.Ebook, "hc:5041");
            var catalog = AttachCatalog(author, target);

            Assert.That(Decide(author, target, catalog, "Ernest Cline - Vi (epub)", "Ernest Cline").Accepted, Is.True);
        }

        [TestCase("I, Robot", "1 Robot")]
        [TestCase("The Mix", "The 1009")]
        [TestCase("Vi", "6")]
        [TestCase("Ready Player II", "Ready Player 2")]
        [TestCase("Ready Player Ⅱ", "Ready Player Two")]
        public void unmarked_roman_title_fallback_preserves_literal_and_numeric_forms(string title, string observed)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 520, Name = "Test Author" };
                var target = MakeBook(5201, author, title, "English", BookMediaType.Ebook, "hc:5201");
                var catalog = AttachCatalog(author, target);

                Assert.Multiple(() =>
                {
                    Assert.That(Decide(author, target, catalog, $"Test Author - {title} (epub)", "Test Author", strictness).Accepted,
                        Is.True, $"{strictness}: literal title");
                    Assert.That(Decide(author, target, catalog, $"Test Author - {observed} (epub)", "Test Author", strictness).Accepted,
                        Is.True, $"{strictness}: numeric fallback");
                    Assert.That(target.Editions.Single().Title, Is.EqualTo(title));
                });
            }
        }

        [TestCase("Collected Stories Volume II", "Collected Stories Volume 2")]
        [TestCase("Collected Stories Part II", "Collected Stories Part Two")]
        [TestCase("Collected Stories Tome Ⅱ", "Collected Stories Tome 2")]
        public void marked_roman_title_numbers_keep_their_numeric_equivalence(string title, string observed)
        {
            foreach (var strictness in MatchingStrictnesses)
            {
                var author = new Author { Id = 521, Name = "Test Author" };
                var target = MakeBook(5211, author, title, "English", BookMediaType.Ebook, "hc:5211");
                var catalog = AttachCatalog(author, target);

                Assert.That(Decide(author, target, catalog, $"Test Author - {observed} (epub)", "Test Author", strictness).Accepted,
                    Is.True, $"{strictness}: marked title number");
            }
        }

        [TestCase("Ready Player Two", "Ready Player II", "Ready Player 2")]
        [TestCase("Ready Player 2", "Ready Player II", "Ready Player Two")]
        public void an_unmarked_roman_sibling_still_blocks_another_books_numeric_alias(string title, string siblingTitle, string observed)
        {
            var author = new Author { Id = 522, Name = "Test Author" };
            var target = MakeBook(5221, author, title, "English", BookMediaType.Ebook, "hc:5221");
            var sibling = MakeBook(5222, author, siblingTitle, "English", BookMediaType.Ebook, "hc:5222");
            var catalog = AttachCatalog(author, target, sibling);

            Assert.Multiple(() =>
            {
                Assert.That(Decide(author, target, catalog, $"Test Author - {observed} (epub)", "Test Author").Accepted, Is.False);
                Assert.That(Decide(author, target, catalog, $"Test Author - {title} (epub)", "Test Author").Accepted, Is.True);
            });
        }

        [Test]
        public void distinct_same_media_books_keep_their_own_number_spelling_priority()
        {
            var author = new Author { Id = 506, Name = "Ernest Cline" };
            var wordTitle = MakeBook(5051, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5051");
            var digitTitle = MakeBook(5052, author, "Ready Player 2", "English", BookMediaType.Ebook, "hc:5052");
            var catalog = AttachCatalog(author, wordTitle, digitTitle);

            var searchingWordTitleLiterally = Decide(author, wordTitle, catalog, "Ernest Cline - Ready Player Two (epub)", "Ernest Cline");
            var searchingWordTitleByDigit = Decide(author, wordTitle, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline");
            var searchingDigitTitle = Decide(author, digitTitle, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline");

            Assert.Multiple(() =>
            {
                Assert.That(searchingWordTitleLiterally.Accepted, Is.True);
                Assert.That(searchingWordTitleByDigit.Accepted, Is.False);
                Assert.That(searchingDigitTitle.Accepted, Is.True);
            });
        }

        [Test]
        public void same_work_provider_identity_does_not_turn_numeric_spelling_into_a_competing_book()
        {
            var author = new Author { Id = 507, Name = "Ernest Cline" };
            var target = MakeBook(5061, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5060");
            var sameWork = MakeBook(5062, author, "Ready Player 2", "English", BookMediaType.Ebook, "hc:5060");
            var catalog = AttachCatalog(author, target, sameWork);

            Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline").Accepted, Is.True);
        }

        [Test]
        public void cross_media_numeric_spelling_sibling_does_not_veto_the_title_match()
        {
            var author = new Author { Id = 508, Name = "Ernest Cline" };
            var target = MakeBook(5071, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5071");
            var otherSide = MakeBook(5072, author, "Ready Player 2", "English", BookMediaType.Audiobook, "hc:5072");
            var catalog = AttachCatalog(author, target, otherSide);

            Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player 2 (epub)", "Ernest Cline").Accepted, Is.True);
        }

        [Test]
        public void identical_full_titles_do_not_compete_as_distinct_books()
        {
            var author = new Author { Id = 509, Name = "Ernest Cline" };
            var target = MakeBook(5081, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5081");
            var duplicate = MakeBook(5082, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5082");
            var catalog = AttachCatalog(author, target, duplicate);

            Assert.That(Decide(author, target, catalog, "Ernest Cline - Ready Player Two (epub)", "Ernest Cline").Accepted, Is.True);
        }

        [Test]
        public void title_numeric_fallback_must_not_let_a_short_shared_title_beat_the_complete_sibling()
        {
            var author = new Author { Id = 513, Name = "Frank Herbert" };
            var dune = MakeBook(5121, author, "Dune", "English", BookMediaType.Ebook, "hc:312460");
            var duneTwo = MakeBook(5122, author, "Dune Two", "English", BookMediaType.Ebook, "hc:427393");
            var catalog = AttachCatalog(author, dune, duneTwo);

            var bareTitleSearch = Decide(author, dune, catalog, "Frank Herbert - Dune II (epub)", "Frank Herbert");
            var numberedTitleSearch = Decide(author, duneTwo, catalog, "Frank Herbert - Dune II (epub)", "Frank Herbert");

            Assert.Multiple(() =>
            {
                Assert.That(bareTitleSearch.Accepted, Is.False);
                Assert.That(numberedTitleSearch.Accepted, Is.True);
            });
        }

        [Test]
        public void number_words_in_an_author_name_do_not_supply_author_identity()
        {
            var author = new Author { Id = 510, Name = "Two Rivers" };
            var target = MakeBook(5091, author, "Moonlight", "English", BookMediaType.Ebook, "hc:5091");
            var catalog = AttachCatalog(author, target);

            Assert.That(Decide(author, target, catalog, "2 Rivers - Moonlight (epub)", null).Accepted, Is.False);
        }

        [Test]
        public void number_words_in_narrator_names_do_not_alias_numeric_release_names()
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Narrator Two" },
                Narrator = "Narrator 2"
            });

            Assert.That(result.Any, Is.False);
        }

        [TestCase("mambench")]
        [TestCase("releasereplay")]
        public void production_search_replay_should_allow_numeric_fallback_without_rewriting_a_stored_title(string inputMode)
        {
            var author = new Author { Id = 523, Name = "Ernest Cline" };
            var target = MakeBook(5231, author, "The Mix", "English", BookMediaType.Ebook, "hc:5231");
            var catalog = AttachCatalog(author, target);
            var searchService = MakeReleaseSearchService();
            var criteria = searchService.CreateBookSearchCriteria(author, target, catalog, userInvokedSearch: true, interactiveSearch: true);
            var replay = Program.RunProductionSearchDecision(
                searchService, MakePrioritizer(), new Program.OfflineReplayHttpDispatcher(),
                new[]
                {
                    MakeRelease(inputMode, "literal", "Ernest Cline - The Mix (epub)"),
                    MakeRelease(inputMode, "number-alias", "Ernest Cline - The 1009 (epub)")
                },
                criteria, usedSyntheticInput: true, hasCompleteSourceData: true);

            Assert.Multiple(() =>
            {
                Assert.That(replay.BlockedExternalRequestCount, Is.Zero);
                Assert.That(replay.Decisions.Single(result => result.RemoteBook.Release.Guid == "literal").Approved, Is.True);
                Assert.That(replay.Decisions.Single(result => result.RemoteBook.Release.Guid == "number-alias").Approved, Is.True);
                Assert.That(target.Editions.Single().Title, Is.EqualTo("The Mix"));
            });
        }

        [TestCase("mambench")]
        [TestCase("releasereplay")]
        public void production_search_replay_should_map_title_number_spellings_to_the_requested_book(string inputMode)
        {
            var author = new Author { Id = 511, Name = "Ernest Cline" };
            var target = MakeBook(5101, author, "Ready Player Two", "English", BookMediaType.Ebook, "hc:5101");
            var sibling = MakeBook(5102, author, "Ready Player One", "English", BookMediaType.Ebook, "hc:5102");
            var catalog = AttachCatalog(author, target, sibling);
            var networkGuard = new Program.OfflineReplayHttpDispatcher();
            var searchService = MakeReleaseSearchService();
            var criteria = searchService.CreateBookSearchCriteria(author, target, catalog, userInvokedSearch: true, interactiveSearch: true);
            var reports = new[]
            {
                MakeRelease(inputMode, "word", "Ernest Cline - Ready Player Two (epub)"),
                MakeRelease(inputMode, "digit", "Ernest Cline - Ready Player 2 (epub)"),
                MakeRelease(inputMode, "roman", "Ernest Cline - Ready Player II (epub)"),
                MakeRelease(inputMode, "unicode-roman", "Ernest Cline - Ready Player Ⅱ (epub)"),
                MakeRelease(inputMode, "sibling", "Ernest Cline - Ready Player One (epub)")
            };

            var replay = Program.RunProductionSearchDecision(
                searchService, MakePrioritizer(), networkGuard, reports, criteria,
                usedSyntheticInput: false, hasCompleteSourceData: true);

            Assert.That(replay.Complete, Is.True, inputMode);
            Assert.That(replay.BlockedExternalRequestCount, Is.Zero, inputMode);

            foreach (var guid in new[] { "word", "digit", "roman", "unicode-roman" })
            {
                var decision = replay.Decisions.Single(result => result.RemoteBook.Release.Guid == guid);
                Assert.Multiple(() =>
                {
                    Assert.That(decision.Approved, Is.True, $"{inputMode}: {guid}");
                    Assert.That(decision.RemoteBook.SearchCriteriaMatch?.Book, Is.SameAs(target), $"{inputMode}: {guid}");
                    Assert.That(decision.RemoteBook.SearchCriteriaMatch?.IsMatch, Is.True, $"{inputMode}: {guid}");
                });
            }

            var differentNumberDecision = replay.Decisions.Single(result => result.RemoteBook.Release.Guid == "sibling");
            Assert.Multiple(() =>
            {
                Assert.That(differentNumberDecision.Approved, Is.False, inputMode);
                Assert.That(differentNumberDecision.RemoteBook.SearchCriteriaMatch?.Book, Is.Not.SameAs(target));
            });
        }

        private static Decision Decide(Author author,
                                       Book target,
                                       List<Book> catalog,
                                       string releaseTitle,
                                       string structuredAuthor,
                                       BookMatchingStrictness strictness = BookMatchingStrictness.Balanced)
        {
            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { target },
                AuthorCatalog = catalog,
                InteractiveSearch = true
            };
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = releaseTitle, Author = structuredAuthor }
            };
            var specification = new ReleaseTitleMatchSpecification(
                LogManager.GetCurrentClassLogger(), ConfigServiceTestProxy.Create(strictness));

            return specification.IsSatisfiedBy(remoteBook, criteria);
        }

        private static Book MakeBook(int localId,
                                     Author author,
                                     string title,
                                     string language,
                                     BookMediaType mediaType,
                                     string providerWorkId,
                                     string narrator = null)
        {
            return new Book
            {
                Id = localId,
                AuthorId = author.Id,
                Author = author,
                Title = title,
                HardcoverBookId = providerWorkId,
                MediaType = mediaType,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = localId,
                        BookId = localId,
                        Title = title,
                        Language = language,
                        Narrator = narrator,
                        Monitored = true,
                        ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : null
                    }
                }
            };
        }

        private static List<Book> AttachCatalog(Author author, params Book[] books)
        {
            var catalog = books.ToList();
            author.Books = catalog;
            return catalog;
        }

        private static ReleaseSearchService MakeReleaseSearchService()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced);
            var decisionMaker = new DownloadDecisionMaker(
                new IDecisionEngineSpecification[]
                {
                    new ReleaseTitleMatchSpecification(logger, config),
                    new MultiBookReleaseSpecification(logger)
                },
                parsingService: null,
                bookService: null,
                editionFtsRepository: null,
                formatService: new NoOpCustomFormatCalculationService(),
                aggregationService: new NoOpRemoteBookAggregationService(),
                releaseNarratorMetadataEnricher: new NoOpReleaseNarratorMetadataEnricher(),
                configService: config,
                logger: logger);

            return new ReleaseSearchService(null, null, null, null, decisionMaker, logger);
        }

        private static ReleaseInfo MakeRelease(string inputMode, string guid, string title)
        {
            var mamReport = string.Equals(inputMode, "mambench", StringComparison.Ordinal);
            ReleaseInfo report = mamReport ? new TorrentInfo { FileType = "epub" } : new ReleaseInfo();
            report.Guid = guid;
            report.Title = title;
            report.Author = "Ernest Cline";
            report.Indexer = mamReport ? "MyAnonaMouse" : "OfflineReplay";
            report.IndexerPriority = 0;
            report.DownloadProtocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent;
            report.Categories = new List<int> { 7020 };
            report.PublishDate = DateTime.UtcNow;
            report.Size = 1_000_000;
            return report;
        }

        private static IPrioritizeDownloadDecision MakePrioritizer()
        {
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced);
            return new DownloadDecisionPriorizationService(
                config,
                new TestDelayProfileService(),
                qualityProfileService: null,
                logger: LogManager.GetCurrentClassLogger());
        }
    }
}
