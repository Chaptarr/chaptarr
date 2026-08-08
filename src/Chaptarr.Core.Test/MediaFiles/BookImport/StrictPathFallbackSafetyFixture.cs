using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class StrictPathFallbackSafetyFixture
    {
        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000)
            {
                return new List<MatchingLogEntry>();
            }

            public void ClearLogs()
            {
            }
        }

        private sealed class RecordingV5MatchingService : IV5MatchingService
        {
            public List<Dictionary<string, List<string>>> Requests { get; } = new();
            public List<string> FilePaths { get; } = new();

            public void ProcessSeriesLinks(List<Book> books) { }

            public List<V5MatchedAuthor> SearchV5Matching(string query, IDictionary<string, List<string>> tags, string mediaType, string filePath)
            {
                FilePaths.Add(filePath);
                Requests.Add((tags ?? new Dictionary<string, List<string>>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value?.ToList() ?? new List<string>(), StringComparer.OrdinalIgnoreCase));
                return new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:brian-herbert", name = "Brian Herbert" }
                };
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Author _author;

            public StubAuthorService(Author author)
            {
                _author = author;
            }

            public Author GetAuthor(int authorId) => _author;

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class PathSensitiveEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<(int? AuthorId, List<string> Tokens)> Calls = new List<(int? AuthorId, List<string> Tokens)>();

            public bool FtsTableExists() => true;
            public void RebuildIndex()
            {
            }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                return Evaluate(authorId, tokens);
            }

            private List<EditionFtsMatch> Evaluate(int? authorId, IEnumerable<string> tokens)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.ToLowerInvariant())
                    .ToList();

                Calls.Add((authorId, tokenList));

                // Simulate the exact bad case we care about: path-derived tokens can recover the wrong author/book.
                // Embedded-title-only queries should stay unmatched.
                if (tokenList.Contains("brian") && tokenList.Contains("herbert") && tokenList.Contains("whipping"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 675,
                            BookId = 224,
                            EditionTitle = "Whipping Star",
                            BookTitle = "Whipping Star",
                            AuthorId = 6,
                            AuthorName = "Frank Herbert"
                        }
                    };
                }

                return new List<EditionFtsMatch>();
            }
        }

        private sealed class CompetingAuthorEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<List<string>> Calls = new();

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => Evaluate(tokens);

            private List<EditionFtsMatch> Evaluate(IEnumerable<string> tokens)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Select(token => token.ToLowerInvariant())
                    .ToList();
                Calls.Add(tokenList);

                if (!tokenList.Contains("whipping") || !tokenList.Contains("star"))
                {
                    return new List<EditionFtsMatch>();
                }

                return new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 675,
                        BookId = 224,
                        EditionTitle = "Whipping Star",
                        BookTitle = "Whipping Star",
                        AuthorId = 6,
                        AuthorName = "Frank Herbert",
                        MatchScore = 12
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 676,
                        BookId = 225,
                        EditionTitle = "Dreamer of Dune",
                        BookTitle = "Dreamer of Dune",
                        AuthorId = 31,
                        AuthorName = "Brian Herbert",
                        MatchScore = 8
                    }
                };
            }
        }

        private sealed class NarratorConflictEditionFtsRepository : IEditionFtsRepository
        {
            public int CallCount { get; private set; }
            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => Evaluate(tokens);

            private List<EditionFtsMatch> Evaluate(IEnumerable<string> tokens)
            {
                CallCount++;
                if (!(tokens ?? Array.Empty<string>()).Any(token => string.Equals(token, "hobbit", StringComparison.OrdinalIgnoreCase)))
                {
                    return new List<EditionFtsMatch>();
                }

                return new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 1001,
                        BookId = 500,
                        EditionTitle = "The Serkis Recording",
                        BookTitle = "The Serkis Recording",
                        AuthorId = 50,
                        AuthorName = "J.R.R. Tolkien",
                        NarratorNames = "Andy Serkis",
                        ReadingFormatId = 2,
                        DurationSeconds = 3600,
                        MatchScore = 12
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 1002,
                        BookId = 500,
                        EditionTitle = "The Hobbit",
                        BookTitle = "The Hobbit",
                        AuthorId = 50,
                        AuthorName = "J.R.R. Tolkien",
                        NarratorNames = "Rob Inglis",
                        ReadingFormatId = 2,
                        DurationSeconds = 3600,
                        MatchScore = 11
                    }
                };
            }
        }

        [Test]
        public async Task author_confirmed_rematch_should_not_recover_via_path_tags()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new PathSensitiveEditionFtsRepository();
            var author = new Author
            {
                Id = 31,
                Name = "Brian Herbert",
                Path = "/audiobooks/Brian Herbert"
            };

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Whipping Star" } }
                }
            };
            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = true,
                DisablePathFallback = true,
                PerFileMatching = true
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: author.Id, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(fts.Calls.Any(call => call.Tokens.Contains("brian") || call.Tokens.Contains("herbert")), Is.False);
            Assert.That(fts.Calls.Any(call => call.Tokens.Contains("whipping")), Is.True);
        }

        [Test]
        public async Task unscoped_strict_context_should_not_recover_via_path_tags()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new PathSensitiveEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(new Author { Id = 31, Name = "Brian Herbert", Path = "/audiobooks/Brian Herbert" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Whipping Star" } }
                }
            };
            var secondFile = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Brian Herbert/Whipping Star/Whipping Star Part 2.m4b",
                AllTags = file.AllTags
            };

            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = true
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file, secondFile }, restrictToAuthorId: null, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(2));
            Assert.That(fts.Calls.Any(call => call.Tokens.Contains("brian") || call.Tokens.Contains("herbert")), Is.False);
            Assert.That(fts.Calls.Any(call => call.Tokens.Contains("whipping")), Is.True);
        }

        [Test]
        public async Task tagless_group_should_not_inject_path_tags_when_context_disables_fallback()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new PathSensitiveEditionFtsRepository();
            var author = new Author { Id = 31, Name = "Brian Herbert", Path = "/audiobooks/Brian Herbert" };
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, author.Id, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(fts.Calls, Is.Empty);
        }

        [Test]
        public async Task competing_embedded_author_should_block_path_author_override()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new CompetingAuthorEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Whipping Star/Whipping Star.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Whipping Star" } },
                    { "ARTIST", new List<string> { "Brian Herbert" } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(fts.Calls, Has.Count.EqualTo(1), "A contradictory embedded author must prevent the path retry.");
        }

        [Test]
        public async Task sibling_narrator_evidence_should_block_narrator_blind_path_retry()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new NarratorConflictEditionFtsRepository();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(new Author { Id = 50, Name = "J.R.R. Tolkien", Path = "/audiobooks/J.R.R. Tolkien" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.R.R. Tolkien/The Hobbit/The Hobbit.m4b",
                DurationSeconds = 3600,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "The Hobbit" } },
                    { "AUTHOR", new List<string> { "J.R.R. Tolkien" } },
                    { "NARRATOR", new List<string> { "Andy Serkis" } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(fts.CallCount, Is.EqualTo(1), "A sibling narrator conflict must prevent the narrator-blind path retry.");
        }

        [Test]
        public async Task local_narrator_contradiction_should_also_block_the_v5_path_retry()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new NarratorConflictEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(new Author { Id = 50, Name = "J.R.R. Tolkien", Path = "/audiobooks/J.R.R. Tolkien" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/J.R.R. Tolkien/The Hobbit/The Hobbit.m4b",
                DurationSeconds = 3600,
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TITLE"] = new List<string> { "The Hobbit" },
                    ["AUTHOR"] = new List<string> { "J.R.R. Tolkien" },
                    ["NARRATOR"] = new List<string> { "Andy Serkis" }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(fts.CallCount, Is.EqualTo(1));
            Assert.That(v5.Requests, Has.Count.EqualTo(1), "The embedded V5 attempt may run, but local contradiction must suppress its path retry.");
            Assert.That(v5.Requests[0].ContainsKey("ALBUM"), Is.False, "No path-derived book-folder value may be sent.");
            Assert.That(v5.FilePaths[0], Is.Null, "A suppressed path fallback must also suppress the basename hint sent to V5.");
        }

        private sealed class SameAuthorDuplicateTitleEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<(int? AuthorId, List<string> Tokens)> Calls = new List<(int? AuthorId, List<string> Tokens)>();

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => Evaluate(authorId, tokens);

            private List<EditionFtsMatch> Evaluate(int? authorId, IEnumerable<string> tokens)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Select(token => token.ToLowerInvariant())
                    .ToList();
                Calls.Add((authorId, tokenList));

                if (!tokenList.Any(token => token.StartsWith("rebel", StringComparison.OrdinalIgnoreCase)))
                {
                    return new List<EditionFtsMatch>();
                }

                var candidates = new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 801,
                        BookId = 401,
                        EditionTitle = "Rebels",
                        BookTitle = "Rebels",
                        AuthorId = 70,
                        AuthorName = "Callie Hart",
                        MatchScore = 11
                    },
                    new EditionFtsMatch
                    {
                        EditionId = 802,
                        BookId = 402,
                        EditionTitle = "Rebels",
                        BookTitle = "Rebels",
                        AuthorId = 70,
                        AuthorName = "Callie Hart",
                        MatchScore = 10
                    }
                };

                return authorId.HasValue
                    ? candidates.Where(candidate => candidate.AuthorId == authorId.Value).ToList()
                    : candidates;
            }
        }

        private sealed class SingleAuthorTitleEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<(int? AuthorId, List<string> Tokens)> Calls = new List<(int? AuthorId, List<string> Tokens)>();

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }
            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20) => Evaluate(authorId, tokens);

            private List<EditionFtsMatch> Evaluate(int? authorId, IEnumerable<string> tokens)
            {
                var tokenList = (tokens ?? Array.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .Select(token => token.ToLowerInvariant())
                    .ToList();
                Calls.Add((authorId, tokenList));

                if (!tokenList.Contains("whipping") || !tokenList.Contains("star"))
                {
                    return new List<EditionFtsMatch>();
                }

                return new List<EditionFtsMatch>
                {
                    new EditionFtsMatch
                    {
                        EditionId = 675,
                        BookId = 224,
                        EditionTitle = "Whipping Star",
                        BookTitle = "Whipping Star",
                        AuthorId = 6,
                        AuthorName = "Frank Herbert",
                        MatchScore = 12
                    }
                };
            }
        }

        [Test]
        public async Task near_exact_title_ambiguity_should_stay_insufficient_and_allow_exact_path_title_retry()
        {
            // Ruling 2026-07-13: near-exact (typo/plural tier) ambiguity across different works is shared
            // weak evidence, NOT positive competing proof. The path retry must stay available so an exact
            // folder title can restore eligibility; ordinary deterministic ordering still chooses among
            // otherwise-identical duplicate works. Reachable shape: SAME-author works with the same title
            // and a typo-tier tag - the per-candidate author gate rejects cross-author shapes earlier as
            // insufficient, so they were never the blocked class.
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new SameAuthorDuplicateTitleEditionFtsRepository();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(new Author { Id = 70, Name = "Callie Hart", Path = "/audiobooks/Callie Hart" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Callie Hart/Rebels/Rebel.epub",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Rebel" } },
                    { "AUTHOR", new List<string> { "Callie Hart" } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1), "Ambiguous duplicate-title evidence must stay recoverable through exact folder evidence.");
            Assert.That(result.MatchedFiles.Single().EditionId, Is.EqualTo(801), "Once exact path-title evidence restores eligibility, the existing deterministic candidate ordering must choose edition 801.");
            Assert.That(fts.Calls, Has.Count.GreaterThanOrEqualTo(2), "The path retry must run after the ambiguous embedded attempt.");
        }

        [Test]
        public async Task unbridgeable_author_value_without_rival_support_should_stay_insufficient()
        {
            // Contract pin: an author-ish value the containment validator cannot bridge ("F.H." for
            // Frank Herbert) names no rival, so it is insufficient evidence - never a contradiction -
            // and the folder may still confirm the author.
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new SingleAuthorTitleEditionFtsRepository();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Whipping Star/Whipping Star.epub",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "TITLE", new List<string> { "Whipping Star" } },
                    { "ARTIST", new List<string> { "F.H." } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, null, new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                PerFileMatching = true
            });

            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1), "A containment-missed author value with no rival support must not block path recovery.");
            Assert.That(result.MatchedFiles.Single().EditionId, Is.EqualTo(675));
            Assert.That(fts.Calls, Has.Count.GreaterThanOrEqualTo(2), "The path retry must run after the insufficient embedded attempt.");
        }

    }
}
