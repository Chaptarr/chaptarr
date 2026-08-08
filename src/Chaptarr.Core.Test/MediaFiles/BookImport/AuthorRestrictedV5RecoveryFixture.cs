using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    public class AuthorRestrictedV5RecoveryFixture
    {
        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null) { }
            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null) { }
            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null) { }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null) { }
            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000) => new List<MatchingLogEntry>();
            public void ClearLogs() { }
        }

        private sealed class RecordingV5MatchingService : IV5MatchingService
        {
            public readonly List<(string Query, Dictionary<string, List<string>> Tags, string MediaType, string FilePath)> Requests = new();
            public Func<string, IDictionary<string, List<string>>, string, string, List<V5MatchedAuthor>> OnSearch { get; set; }

            public void ProcessSeriesLinks(List<Book> books) { }

            public List<V5MatchedAuthor> SearchV5Matching(string query, IDictionary<string, List<string>> tags, string mediaType, string filePath)
            {
                var captured = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (tags != null)
                {
                    foreach (var kv in tags)
                    {
                        captured[kv.Key] = kv.Value?.ToList() ?? new List<string>();
                    }
                }

                Requests.Add((query, captured, mediaType, filePath));
                return OnSearch?.Invoke(query, tags, mediaType, filePath) ?? new List<V5MatchedAuthor>();
            }
        }

        private sealed class RecordingEditionFtsRepository : IEditionFtsRepository
        {
            public readonly List<(int? AuthorId, List<string> Tokens)> Calls = new();

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }

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

                if (authorId == 6 && tokenList.Contains("whipping") && tokenList.Contains("star"))
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
                            AuthorName = "Frank Herbert",
                            NarratorNames = "Scott Brick",
                            DurationSeconds = 36000
                        }
                    };
                }

                return new List<EditionFtsMatch>();
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            private readonly Dictionary<int, Author> _authorsById;
            private readonly Dictionary<string, Author> _authorsByProvider;

            public StubAuthorService(params (Author Author, string Provider, string ProviderId)[] authors)
            {
                _authorsById = authors.ToDictionary(x => x.Author.Id, x => x.Author);
                _authorsByProvider = authors.ToDictionary(
                    x => $"{x.Provider}:{x.ProviderId}",
                    x => x.Author,
                    StringComparer.OrdinalIgnoreCase);
            }

            public Author GetAuthor(int authorId) => _authorsById.TryGetValue(authorId, out var author) ? author : null;
            public Author FindByProviderId(string provider, string providerId) => _authorsByProvider.TryGetValue($"{provider}:{providerId}", out var author) ? author : null;

            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
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

        [Test]
        public async Task author_restricted_mode_should_recover_via_v5_suggested_existing_author_when_import_allowed()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, tags, _, _) =>
                {
                    var artistValues = tags.TryGetValue("ARTIST", out var artist) ? artist : new List<string>();
                    if (artistValues.Any(v => string.Equals(v, "Frank Herbert", StringComparison.OrdinalIgnoreCase)))
                    {
                        return new List<V5MatchedAuthor>
                        {
                            new V5MatchedAuthor
                            {
                                id = "hc:frank-herbert",
                                name = "Frank Herbert",
                                edition_hardcover_id = "hc-ed-675"
                            }
                        };
                    }

                    return new List<V5MatchedAuthor>();
                }
            };
            var fts = new RecordingEditionFtsRepository();
            var authorService = new StubAuthorService(
                (new Author { Id = 31, Name = "Brian Herbert", Path = "/audiobooks/Brian Herbert" }, "hc", "brian-herbert"),
                (new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" }, "hc", "frank-herbert"));

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: authorService,
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
                DurationSeconds = 36010,
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Whipping Star" } },
                    { "ARTIST", new List<string> { "Frank Herbert" } }
                }
            };

            var context = new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = true,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = false
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: 31, context);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.MatchedFiles[0].AuthorId, Is.EqualTo(6));
            Assert.That(result.MatchedFiles[0].AuthorName, Is.EqualTo("Frank Herbert"));
            Assert.That(result.MatchedFiles[0].BookTitle, Is.EqualTo("Whipping Star"));
            Assert.That(fts.Calls.Select(call => call.AuthorId), Is.EqualTo(new int?[] { 31, 6 }));
            Assert.That(v5.Requests, Has.Count.EqualTo(1));
            Assert.That(v5.Requests[0].Tags.Keys, Does.Not.Contain("AUTHOR"));
        }

        [Test]
        public async Task author_restricted_v5_recovery_should_use_embedded_tags_only_when_path_fallback_is_disabled()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, tags, _, _) =>
                {
                    var flattened = (tags ?? new Dictionary<string, List<string>>())
                        .SelectMany(kv => kv.Value ?? new List<string>())
                        .Select(v => v.ToLowerInvariant())
                        .ToList();

                    return flattened.Contains("brian") || flattened.Contains("herbert")
                        ? new List<V5MatchedAuthor>
                        {
                            new V5MatchedAuthor
                            {
                                id = "hc:brian-herbert",
                                name = "Brian Herbert"
                            }
                        }
                        : new List<V5MatchedAuthor>();
                }
            };

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService((new Author { Id = 31, Name = "Brian Herbert", Path = "/audiobooks/Brian Herbert" }, "hc", "brian-herbert")),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new RecordingEditionFtsRepository(),
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
                AllowV5Identification = true,
                AllowAuthorImport = false,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = false
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: 31, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.UnmatchedFiles[0].PotentialAuthors, Is.Empty);
            Assert.That(v5.Requests, Has.Count.EqualTo(1));
            Assert.That(v5.Requests[0].Tags.Keys, Is.EquivalentTo(new[] { "TITLE" }));
            Assert.That(v5.Requests[0].Tags["TITLE"], Is.EquivalentTo(new[] { "Whipping Star" }));
        }

        [Test]
        public async Task author_restricted_v5_recovery_should_require_suggested_author_in_embedded_tags()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor
                    {
                        id = "hc:brian-herbert",
                        name = "Brian Herbert"
                    }
                }
            };

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService((new Author { Id = 6, Name = "Frank Herbert", Path = "/audiobooks/Frank Herbert" }, "hc", "frank-herbert")),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new RecordingEditionFtsRepository(),
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var file = new DiscoveredFileWithMetadata
            {
                Path = "/audiobooks/Frank Herbert/Dreamer of Dune/Dreamer.m4b",
                AllTags = new Dictionary<string, List<string>>
                {
                    { "TITLE", new List<string> { "Dreamer of Dune" } },
                    { "ARTIST", new List<string> { "Frank Herbert" } }
                }
            };

            var context = new MatchingContext
            {
                AllowV5Identification = true,
                AllowAuthorImport = true,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = false
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: 6, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(result.UnmatchedFiles[0].PotentialAuthors, Is.Empty);
            Assert.That(result.UnmatchedFiles[0].Reason, Is.EqualTo("NO_MATCH_HOLY_GRAIL (authorId=6)"));
            Assert.That(v5.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task author_restricted_mode_should_not_call_v5_when_disabled()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor
                    {
                        id = "hc:frank-herbert",
                        name = "Frank Herbert"
                    }
                }
            };

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService((new Author { Id = 31, Name = "Brian Herbert", Path = "/audiobooks/Brian Herbert" }, "hc", "brian-herbert")),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new RecordingEditionFtsRepository(),
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
                    { "TITLE", new List<string> { "Whipping Star" } },
                    { "ARTIST", new List<string> { "Frank Herbert" } }
                }
            };

            var context = new MatchingContext
            {
                AllowV5Identification = false,
                AllowAuthorImport = true,
                DeferUnmatchedToAuthorReady = false,
                AllowUnscopedFallback = false,
                DisablePathFallback = true,
                PerFileMatching = false
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: 31, context);

            Assert.That(result.MatchedFiles, Is.Empty);
            Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
            Assert.That(v5.Requests, Is.Empty);
        }

        [Test]
        public void v5_suggestion_should_retry_with_path_when_embedded_author_evidence_is_insufficient()
        {
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:brian-herbert", name = "Brian Herbert" }
                }
            };
            var sut = CreateV5SuggestionService(v5);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Whipping Star" }
            };

            var suggestion = InvokeV5SuggestionWithPathFallback(
                sut,
                tags,
                "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                allowPathFallback: true);

            Assert.That(suggestion, Is.Not.Null);
            Assert.That(v5.Requests, Has.Count.EqualTo(2));
            Assert.That(v5.Requests[0].Tags.Keys, Is.EquivalentTo(new[] { "TITLE" }));
            Assert.That(v5.Requests[1].Tags["AUTHOR"], Is.EquivalentTo(new[] { "Brian Herbert" }));
        }

        [Test]
        public void v5_suggestion_should_not_retry_with_path_when_the_caller_disables_it()
        {
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:brian-herbert", name = "Brian Herbert" }
                }
            };
            var sut = CreateV5SuggestionService(v5);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Whipping Star" }
            };

            var suggestion = InvokeV5SuggestionWithPathFallback(
                sut,
                tags,
                "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                allowPathFallback: false);

            Assert.That(suggestion, Is.Null);
            Assert.That(v5.Requests, Has.Count.EqualTo(1));
            Assert.That(v5.Requests[0].Tags.Keys, Is.EquivalentTo(new[] { "TITLE" }));
        }

        [Test]
        public void v5_suggestion_should_not_retry_when_a_surviving_value_proves_a_competing_author()
        {
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:brian-herbert", name = "Brian Herbert" },
                    new V5MatchedAuthor { id = "hc:frank-herbert", name = "Frank Herbert" }
                }
            };
            var sut = CreateV5SuggestionService(v5);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Whipping Star" },
                ["ODD_FIELD"] = new List<string> { "Frank Herbert" }
            };

            var suggestion = InvokeV5SuggestionWithPathFallback(
                sut,
                tags,
                "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                allowPathFallback: true);

            Assert.That(suggestion, Is.Null);
            Assert.That(v5.Requests, Has.Count.EqualTo(1), "A path must not overwrite positive embedded evidence for another returned author.");
        }

        [Test]
        public void v5_suggestion_should_use_path_author_evidence_instead_of_a_comment()
        {
            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:brian-herbert", name = "Brian Herbert" }
                }
            };
            var sut = CreateV5SuggestionService(v5);
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Whipping Star" },
                ["COMMENT"] = new List<string> { "For readers of Brian Herbert" }
            };

            var suggestion = InvokeV5SuggestionWithPathFallback(
                sut,
                tags,
                "/audiobooks/Brian Herbert/Whipping Star/Whipping Star.m4b",
                allowPathFallback: true);

            Assert.That(suggestion, Is.Not.Null);
            Assert.That(v5.Requests, Has.Count.EqualTo(2), "The excluded comment must not satisfy the embedded-author check.");
            Assert.That(v5.Requests[0].Tags.Keys, Is.EquivalentTo(new[] { "TITLE", "COMMENT" }));
            Assert.That(v5.Requests[1].Tags["AUTHOR"], Is.EquivalentTo(new[] { "Brian Herbert" }));
        }

        [Test]
        public void author_ready_matching_context_should_enable_restricted_v5_recovery_and_keep_fail_closed_guards()
        {
            var method = typeof(IngestQueueOnAuthorReadyHandler).GetMethod("CreateAuthorReadyMatchingContext", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var context = (MatchingContext)method.Invoke(null, null);

            Assert.Multiple(() =>
            {
                Assert.That(context.AllowV5Identification, Is.True);
                Assert.That(context.AllowAuthorImport, Is.True);
                Assert.That(context.DeferUnmatchedToAuthorReady, Is.False);
                Assert.That(context.AllowUnscopedFallback, Is.False);
                Assert.That(context.DisablePathFallback, Is.True);
                Assert.That(context.PerFileMatching, Is.False);
            });
        }

        private static FileMatchingService CreateV5SuggestionService(RecordingV5MatchingService v5)
        {
            var logger = LogManager.GetCurrentClassLogger();
            return new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(strictness: BookMatchingStrictness.Balanced, usePathAsTagsFallback: true),
                authorService: new StubAuthorService(),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new RecordingEditionFtsRepository(),
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
        }

        private static object InvokeV5SuggestionWithPathFallback(
            FileMatchingService service,
            Dictionary<string, List<string>> tags,
            string path,
            bool allowPathFallback)
        {
            var method = typeof(FileMatchingService).GetMethod(
                "TryV5SuggestionWithPathFallback",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            return method.Invoke(service, new object[]
            {
                tags,
                BookMediaType.Audiobook,
                path,
                allowPathFallback
            });
        }
    }
}
