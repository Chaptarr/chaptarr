using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Authors;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class PerFileMatchingIdentitySplitFixture
    {
        private sealed class NullMatchingUploadLogger : IMatchingUploadLogger
        {
            public List<(string FilePath, MatchResult Result)> FinalDecisions { get; } = new();

            public void LogMatchAttempt(string filePath, Dictionary<string, List<string>> extractedTags, MatchResult result, int? commandId = null, string correlationId = null)
            {
            }

            public void LogV5Request(string query, Dictionary<string, List<string>> tags, string mediaType, string response, string filePath = null, int? commandId = null, string correlationId = null)
            {
            }

            public void LogFinalDecision(string filePath, MatchResult matchResult, Dictionary<string, List<string>> extractedTags = null, int? commandId = null, string correlationId = null)
            {
                FinalDecisions.Add((filePath, matchResult));
            }

            public void LogFinalDecision(string filePath, string decision, string reason, Dictionary<string, List<string>> extractedTags = null, string authorMatched = null, string bookMatched = null, string editionMatched = null, List<CandidateRejection> rejections = null, int? commandId = null, string correlationId = null)
            {
                FinalDecisions.Add((filePath, new MatchResult { Decision = decision, Reason = reason }));
            }

            public List<MatchingLogEntry> GetRecentLogs(int maxEntries = 1000)
            {
                return new List<MatchingLogEntry>();
            }

            public void ClearLogs()
            {
            }
        }

        private sealed class BranchingEditionFtsRepository : IEditionFtsRepository
        {
            public int Calls { get; private set; }

            public bool FtsTableExists() => true;

            public void RebuildIndex()
            {
            }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                Calls++;

                var tokenList = (tokens ?? Enumerable.Empty<string>())
                    .Select(t => t?.ToLowerInvariant())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (tokenList.Any(t => t == "horizon"))
                {
                    return new List<EditionFtsMatch>();
                }


                if (tokenList.Any(t => t == "wild"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 7,
                            ForeignEditionId = "az:B0719KKW5W-audiobook",
                            BookId = 701,
                            EditionTitle = "Wild Cards VII",
                            BookTitle = "Wild Cards VII",
                            AuthorId = 344,
                            AuthorName = "George R.R. Martin",
                            MatchScore = 2.0,
                            DurationSeconds = 4000,
                            ReadingFormatId = 2
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 8,
                            ForeignEditionId = "gr:59040604-audiobook",
                            BookId = 701,
                            EditionTitle = "Wild Cards VII",
                            BookTitle = "Wild Cards VII",
                            AuthorId = 344,
                            AuthorName = "George R.R. Martin",
                            MatchScore = 1.0,
                            DurationSeconds = 20000,
                            ReadingFormatId = 2
                        }
                    };
                }
                if (tokenList.Any(t => t == "murder"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 21956,
                            ForeignEditionId = "gr:48571969-audiobook",
                            BookId = 9137,
                            EditionTitle = "A Murder of Magpies",
                            BookTitle = "A Murder of Magpies",
                            AuthorId = 258,
                            AuthorName = "Mark Edwards",
                            NarratorNames = "Elliot Hill",
                            MatchScore = 1.0,
                            DurationSeconds = 9960,
                            ReadingFormatId = 2
                        }
                    };
                }
                if (tokenList.Any(t => t == "echo"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 22001,
                            BookId = 2201,
                            EditionTitle = "Michael Connelly Collection",
                            BookTitle = "Michael Connelly Collection",
                            AuthorId = 220,
                            AuthorName = "Michael Connelly",
                            MatchScore = 100.0,
                            ReadingFormatId = 2
                        },
                        new EditionFtsMatch
                        {
                            EditionId = 22002,
                            BookId = 2202,
                            EditionTitle = "BOSCH: Schwarzes Echo",
                            BookTitle = "The Black Echo",
                            AuthorId = 220,
                            AuthorName = "Michael Connelly",
                            MatchScore = 1.0,
                            ReadingFormatId = 2
                        }
                    };
                }
                if (tokenList.Any(t => t == "alpha"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 1,
                            BookId = 101,
                            EditionTitle = "Alpha",
                            BookTitle = "Alpha",
                            AuthorId = 1001,
                            AuthorName = "Test Author",
                            MatchScore = 1.0
                        }
                    };
                }

                if (tokenList.Any(t => t == "beta"))
                {
                    return new List<EditionFtsMatch>
                    {
                        new EditionFtsMatch
                        {
                            EditionId = 2,
                            BookId = 102,
                            EditionTitle = "Beta",
                            BookTitle = "Beta",
                            AuthorId = 1001,
                            AuthorName = "Test Author",
                            MatchScore = 1.0
                        }
                    };
                }

                return new List<EditionFtsMatch>();
            }

        }

        private sealed class RecordingV5MatchingService : IV5MatchingService
        {
            public int Calls { get; private set; }
            public List<string> FilePaths { get; } = new();
            public List<string> Queries { get; } = new();
            public List<IDictionary<string, List<string>>> TagsByCall { get; } = new();


            public void ProcessSeriesLinks(List<Book> books)
            {
            }

            public List<V5MatchedAuthor> SearchV5Matching(string query, IDictionary<string, List<string>> tags, string mediaType, string filePath)
            {
                Calls++;
                FilePaths.Add(filePath);
                Queries.Add(query);
                TagsByCall.Add(tags);

                if ((query ?? string.Empty).IndexOf("ruthless", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new List<V5MatchedAuthor>
                    {
                        new V5MatchedAuthor
                        {
                            id = "hc:233776",
                            name = "Caroline Peckham",
                            work_title = "Ruthless Fae",
                            work_id = "hc:463791",
                            edition_title = "Ruthless Fae",
                            edition_hardcover_id = "hc:463791"
                        }
                    };
                }

                if ((query ?? string.Empty).IndexOf("schwarzes", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new List<V5MatchedAuthor>
                    {
                        new V5MatchedAuthor
                        {
                            id = "hc:182508",
                            name = "Michael Connelly",
                            work_title = "The Black Echo",
                            work_id = "hc:1987747",
                            edition_title = "BOSCH: Schwarzes Echo",
                            edition_hardcover_id = "gr:229391768"
                        }
                    };
                }

                return new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor
                    {
                        id = "hc:12345",
                        name = "Test Author",
                        edition_hardcover_id = "hc-edition-999"
                    }
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

            public Author GetAuthor(int authorId) => _author != null && _author.Id == authorId ? _author : null;
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => throw new System.NotImplementedException();
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new System.NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new System.NotImplementedException();
            public Author FindByProviderId(string provider, string providerId)
            {
                if (_author == null)
                {
                    return null;
                }

                return string.Equals(provider, "hc", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(providerId, (_author.HardcoverAuthorId ?? string.Empty).Replace("hc:", string.Empty), StringComparison.OrdinalIgnoreCase)
                    ? _author
                    : null;
            }
            public Author FindByName(string title) => throw new System.NotImplementedException();
            public Author FindByNameInexact(string title) => throw new System.NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new System.NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new System.NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new System.NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new System.NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new System.NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new System.NotImplementedException();
            public Author UpdateAuthor(Author author) => throw new System.NotImplementedException();
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new System.NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new System.NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new System.NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new System.NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new System.NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new System.NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new System.NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new System.NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new System.NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => new List<int>();
            public void ClearAuthorCache() => throw new System.NotImplementedException();
        }

        private sealed class GroupedRecoveryFtsRepository : IEditionFtsRepository
        {
            public int ScopedCalls { get; private set; }

            public bool FtsTableExists() => true;
            public void RebuildIndex() { }

            public List<EditionFtsMatch> SearchWithTwoStep(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType, int limit = 20)
            {
                if (authorId != 220)
                {
                    return new List<EditionFtsMatch>();
                }

                ScopedCalls++;
                return new List<EditionFtsMatch>
                {
                    new()
                    {
                        EditionId = 22001,
                        BookId = 2201,
                        EditionTitle = "Michael Connelly Collection",
                        BookTitle = "Michael Connelly Collection",
                        AuthorId = 220,
                        AuthorName = "Michael Connelly",
                        ReadingFormatId = 2,
                        MatchScore = 100
                    },
                    new()
                    {
                        EditionId = 22002,
                        BookId = 2202,
                        EditionTitle = "BOSCH: Schwarzes Echo",
                        BookTitle = "The Black Echo",
                        AuthorId = 220,
                        AuthorName = "Michael Connelly",
                        ReadingFormatId = 2,
                        DurationSeconds = 58020,
                        MatchScore = 1
                    }
                };
            }
        }

        private class WorkBookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name switch
                {
                    nameof(IBookService.FindAllByWorkProviderId) => Books.ToList(),
                    nameof(IBookService.GetBooks) => Books
                        .Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id))
                        .ToList(),
                    nameof(IBookService.GetBook) => Books.SingleOrDefault(book => book.Id == (int)args[0]),
                    nameof(IBookService.GetBooksByAuthor) => Books.Where(book => book.AuthorId == (int)args[0]).ToList(),
                    _ => throw new NotImplementedException(targetMethod.Name)
                };
            }
        }

        [Test]
        public async Task manual_preview_should_not_split_matched_tracks_by_chapter_title_when_custom_tag_proves_book()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(int track)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "BOOKIDENTITY", new List<string> { "Test Author - Alpha" } },
                    { "TITLE", new List<string> { $"Track {track}" } }
                };
            }

            var files = Enumerable.Range(1, 8)
                .Select(i => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/custom-alpha/{i:00}.mp3",
                    AllTags = Tags(i)
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(m => m.EditionId == 1), Is.True);
                Assert.That(fts.Calls, Is.LessThan(files.Length));
                Assert.That(result.MatchedFiles.Select(m => m.Provenance.DecisionId).Distinct().ToList(), Has.Count.EqualTo(1));
                Assert.That(ReferenceEquals(result.MatchedFiles[0].Provenance, result.MatchedFiles[1].Provenance), Is.False);
                Assert.That(result.MatchedFiles.All(m => m.Provenance.SupportingSignals.Any(signal =>
                    signal.Type == "title" &&
                    signal.Field == "BOOKIDENTITY" &&
                    signal.Observed == "Test Author - Alpha")), Is.True);
                Assert.That(result.MatchedFiles
                    .Skip(1)
                    .SelectMany(m => m.Provenance.SupportingSignals)
                    .Any(signal => signal.Observed == "Track 1"), Is.False);
            });
        }

        [Test]
        public async Task manual_preview_should_use_group_duration_before_testing_individual_audiobook_parts()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var fts = new BranchingEditionFtsRepository();
            var matchingLogger = new NullMatchingUploadLogger();
            var svc = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 344, Name = "George R.R. Martin" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var files = Enumerable.Range(1, 4)
                .SelectMany(i => new[]
                {
                    new DiscoveredFileWithMetadata
                    {
                        Path = $"/downloads/wild-cards/Wild Cards II ({i}).mp3",
                        Size = 100000 + Math.Min(i, 3),
                        DurationSeconds = 1000,
                        AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "ALBUM", new List<string> { "Wild Cards VII" } },
                            { "ALBUMARTIST", new List<string> { "George R.R. Martin" } },
                            { "TITLE", new List<string> { $"Chapter {i}" } }
                        }
                    },
                    new DiscoveredFileWithMetadata
                    {
                        Path = $"/downloads/wild-cards/Wild Cards VII - {i}.mp3",
                        Size = 100000 + Math.Min(i, 3),
                        DurationSeconds = 1000,
                        AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "ALBUM", new List<string> { "Wild Cards VII" } },
                            { "ALBUMARTIST", new List<string> { "George R.R. Martin" } },
                            { "TITLE", new List<string> { $"Chapter {i}" } }
                        }
                    }
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(
                files,
                restrictToAuthorId: null,
                MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(match => match.EditionId == 7), Is.True);
                Assert.That(fts.Calls, Is.EqualTo(1), "the full-book decision must run before any per-part probes");
                Assert.That(matchingLogger.FinalDecisions.Select(decision => decision.Result?.Reason),
                    Is.All.EqualTo("Matched via Holy Grail FTS (grouped seed)"));
            });
        }

        [Test]
        public async Task scoped_scan_should_keep_homogeneous_magpies_tracks_in_one_match_decision()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var fts = new BranchingEditionFtsRepository();
            var matchingLogger = new NullMatchingUploadLogger();
            var author = new Author
            {
                Id = 258,
                Name = "Mark Edwards",
                Path = "/data/media/audiobooks/Mark Edwards"
            };
            var svc = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            var durations = new[]
            {
                261, 732, 795, 488, 844, 518, 587, 594,
                822, 279, 1370, 884, 601, 528, 501, 188
            };
            var files = durations
                .Select((duration, index) =>
                {
                    var track = index + 1;
                    return new DiscoveredFileWithMetadata
                    {
                        Path = $"/data/media/audiobooks/Mark Edwards/A Murder of Magpies/A Murder of Magpies {track:00}.mp3",
                        DurationSeconds = duration,
                        AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "ID3v2:TALB", new List<string> { "M02 A Murder of Magpies" } },
                            { "ALBUM", new List<string> { "M02 A Murder of Magpies" } },
                            { "ID3v2:TPE1", new List<string> { "Mark Edwards" } },
                            { "ARTIST", new List<string> { "Mark Edwards" } },
                            { "ID3v2:TCOM", new List<string> { "Elliot Hill" } },
                            { "COMPOSER", new List<string> { "Elliot Hill" } },
                            { "ID3v2:TRCK", new List<string> { $"{track}/16" } },
                            { "TRACKNUMBER", new List<string> { track.ToString() } },
                            { "TOTALTRACKS", new List<string> { "16" } },
                            { "DATE", new List<string> { "2018" } }
                        }
                    };
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(
                files,
                restrictToAuthorId: author.Id,
                MatchingContextPresets.ForScanScopedRematch());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(match => match.EditionId == 21956), Is.True);
                Assert.That(result.MatchedFiles.Select(match => match.Provenance.DecisionId).Distinct().ToList(), Has.Count.EqualTo(1));
                Assert.That(matchingLogger.FinalDecisions.Select(decision => decision.Result?.Reason),
                    Is.All.EqualTo("Matched via Holy Grail FTS + smoke test (grouped seed)"));
                Assert.That(result.MatchedFiles.First().Provenance.SupportingSignals.Any(signal =>
                    signal.Type == "duration" &&
                    signal.Observed == "9992 seconds" &&
                    signal.Expected == "9960 seconds"), Is.True);
                Assert.That(result.MatchedFiles.SelectMany(match => match.Provenance.ConflictingSignals)
                    .Any(signal => signal.Type == "duration"), Is.False);
            });
        }

        [Test]
        public async Task manual_preview_should_require_the_same_identity_field_even_when_values_are_equal()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var matchingLogger = new NullMatchingUploadLogger();

            var svc = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(int track)
            {
                var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "TITLE", new List<string> { $"Track {track}" } }
                };
                tags[track == 1 ? "BOOKIDENTITY" : "OTHERIDENTITY"] = new List<string> { "Test Author - Alpha" };
                return tags;
            }

            var files = Enumerable.Range(1, 8)
                .Select(i => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/field-exact-alpha/{i:00}.mp3",
                    AllTags = Tags(i)
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(files, null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(match => match.EditionId == 1), Is.True);
                Assert.That(result.MatchedFiles.Select(match => match.Provenance?.DecisionId).Distinct().Count(), Is.EqualTo(2),
                    "equal title text under different physical keys must form separate identity units");
                Assert.That(matchingLogger.FinalDecisions
                    .Where(decision => decision.FilePath?.Contains("/field-exact-alpha/") == true)
                    .Where(decision => decision.FilePath.EndsWith("/01.mp3"))
                    .Select(decision => decision.Result?.Reason),
                    Is.All.EqualTo("Matched via Holy Grail FTS (per-file split)"));
                Assert.That(fts.Calls, Is.LessThan(files.Length));
            });
        }

        [Test]
        public async Task manual_preview_should_not_fast_group_alias_only_files_when_seed_book_proof_uses_raw_field()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var matchingLogger = new NullMatchingUploadLogger();

            var svc = new FileMatchingService(
                matchingLogger: matchingLogger,
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> RawAndAliasTags(int track)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "iD3v2:TALB", new List<string> { $"Alpha Chapter {track}" } },
                    { "ALBUM", new List<string> { $"Alpha Chapter {track}" } },
                    { "TITLE", new List<string> { $"Track {track}" } }
                };
            }

            Dictionary<string, List<string>> AliasOnlyTags(int track)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "ALBUM", new List<string> { $"Alpha Chapter {track}" } },
                    { "TITLE", new List<string> { $"Track {track}" } }
                };
            }

            var files = Enumerable.Range(1, 8)
                .Select(i => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/raw-alias-alpha/{i:00}.mp3",
                    AllTags = i == 1 ? RawAndAliasTags(i) : AliasOnlyTags(i)
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(m => m.EditionId == 1), Is.True);
                Assert.That(matchingLogger.FinalDecisions
                    .Where(d => d.FilePath != null && d.FilePath.Contains("/raw-alias-alpha/"))
                    .Where(d => !d.FilePath.EndsWith("/01.mp3"))
                    .Select(d => d.Result?.Reason),
                    Is.All.EqualTo("Matched via Holy Grail FTS (per-file split)"));
            });
        }

        [Test]
        public async Task manual_preview_should_use_one_v5_suggestion_when_custom_tag_proves_unmatched_book()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(int track)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "BOOKIDENTITY", new List<string> { "Test Author - Untracked Book" } },
                    { "TITLE", new List<string> { $"Track {track}" } }
                };
            }

            var files = Enumerable.Range(1, 8)
                .Select(i => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/custom-untracked/{i:00}.mp3",
                    AllTags = Tags(i)
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles, Is.Empty);
                Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(v5.Calls, Is.EqualTo(1));
                Assert.That(result.UnmatchedFiles.All(u => u.PotentialAuthors?.SingleOrDefault()?.ProviderId == "hc:12345"), Is.True);
            });
        }

        [Test]
        public async Task manual_preview_should_retry_v5_with_path_tags_when_embedded_query_is_empty()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: true),
                authorService: null,
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
                Path = "/downloads/torrents/complete/audiobooks/Ruthless Fae - Caroline Peckham/Ruthless Fae - Caroline Peckham.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "COMMENT", new List<string> { "Encoded by tool" } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles, Is.Empty);
                Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
                Assert.That(result.UnmatchedFiles[0].PotentialAuthors?.SingleOrDefault()?.ProviderId, Is.EqualTo("hc:233776"));
                Assert.That(v5.Calls, Is.EqualTo(2));
                Assert.That(v5.Queries.First(), Is.Empty);
                Assert.That(v5.Queries.Last(), Does.Contain("ruthless fae"));
                Assert.That(v5.Queries.Last(), Does.Contain("caroline peckham"));
                Assert.That(v5.TagsByCall.Last().Keys, Does.Contain("TITLE"));
            });
        }

        [Test]
        public async Task manual_preview_should_not_retry_v5_with_path_tags_when_path_fallback_is_disabled()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: null,
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
                Path = "/downloads/torrents/complete/audiobooks/Ruthless Fae - Caroline Peckham/Ruthless Fae - Caroline Peckham.m4b",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "COMMENT", new List<string> { "Encoded by tool" } }
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles, Is.Empty);
                Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(1));
                Assert.That(result.UnmatchedFiles[0].PotentialAuthors, Is.Empty);
                Assert.That(v5.Calls, Is.EqualTo(1));
                Assert.That(v5.Queries.Single(), Is.Empty);
            });
        }

        [Test]
        public async Task should_not_stamp_across_mixed_identity_groups_in_per_file_mode()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string album)
            {
                return new Dictionary<string, List<string>>
                {
                    { "ALBUMARTIST", new List<string> { "Test Author" } },
                    { "ALBUM", new List<string> { album } },
                    { "TITLE", new List<string> { album } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/books/unit/01-alpha.mp3", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/02-beta.mp3", AllTags = Tags("Beta") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/03-beta.mp3", AllTags = Tags("Beta") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/04-alpha.mp3", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/05-alpha.mp3", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/06-alpha.mp3", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/07-alpha.mp3", AllTags = Tags("Alpha") }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, forDownloads: true);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));

            var matchesByPath = result.MatchedFiles.ToDictionary(m => m.File.Path);
            Assert.That(matchesByPath["/books/unit/02-beta.mp3"].EditionId, Is.EqualTo(2));
            Assert.That(matchesByPath["/books/unit/03-beta.mp3"].EditionId, Is.EqualTo(2));
            Assert.That(matchesByPath["/books/unit/01-alpha.mp3"].EditionId, Is.EqualTo(1));
            Assert.That(matchesByPath["/books/unit/04-alpha.mp3"].EditionId, Is.EqualTo(1));
            Assert.That(matchesByPath["/books/unit/07-alpha.mp3"].EditionId, Is.EqualTo(1));
        }

        [Test]
        public async Task manual_preview_should_not_stamp_shared_path_title_across_unique_embedded_books()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var fts = new BranchingEditionFtsRepository();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string title)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUMARTIST", new List<string> { "Test Author" } },
                    { "ALBUM", new List<string> { title } },
                    { "TITLE", new List<string> { title } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata
                {
                    Path = "/books/Test Author/Alpha/Alpha (1).mp3",
                    AllTags = Tags("Alpha")
                },
                new DiscoveredFileWithMetadata
                {
                    Path = "/books/Test Author/Alpha/Alpha (2).mp3",
                    AllTags = Tags("Beta")
                }
            };

            var result = await svc.MatchFilesToLibraryAsync(
                files,
                restrictToAuthorId: null,
                MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles.Single(match => match.File.Path.EndsWith("(1).mp3")).EditionId, Is.EqualTo(1));
                Assert.That(result.MatchedFiles.Single(match => match.File.Path.EndsWith("(2).mp3")).EditionId, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task should_probe_all_files_before_group_stamping_in_author_restricted_mode()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string album)
            {
                return new Dictionary<string, List<string>>
                {
                    { "ALBUMARTIST", new List<string> { "Test Author" } },
                    { "ALBUM", new List<string> { album } },
                    { "TITLE", new List<string> { album } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/books/unit/01-alpha.mp3", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/02-beta.mp3", AllTags = Tags("Beta") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/03-beta.mp3", AllTags = Tags("Beta") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/04-alpha.mp3", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/05-beta.mp3", AllTags = Tags("Beta") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/06-beta.mp3", AllTags = Tags("Beta") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/07-alpha.mp3", AllTags = Tags("Alpha") }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: 1001, forDownloads: false);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));

            var matchesByPath = result.MatchedFiles.ToDictionary(m => m.File.Path);
            Assert.That(matchesByPath["/books/unit/01-alpha.mp3"].EditionId, Is.EqualTo(1));
            Assert.That(matchesByPath["/books/unit/04-alpha.mp3"].EditionId, Is.EqualTo(1));
            Assert.That(matchesByPath["/books/unit/07-alpha.mp3"].EditionId, Is.EqualTo(1));
            Assert.That(matchesByPath["/books/unit/02-beta.mp3"].EditionId, Is.EqualTo(2));
            Assert.That(matchesByPath["/books/unit/03-beta.mp3"].EditionId, Is.EqualTo(2));
            Assert.That(matchesByPath["/books/unit/05-beta.mp3"].EditionId, Is.EqualTo(2));
            Assert.That(matchesByPath["/books/unit/06-beta.mp3"].EditionId, Is.EqualTo(2));
        }

        [Test]
        public async Task should_split_same_folder_m4b_books_by_tag_identity_when_not_in_per_file_mode()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author", AudiobookPath = "/books" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string album)
            {
                return new Dictionary<string, List<string>>
                {
                    { "ALBUMARTIST", new List<string> { "Test Author" } },
                    { "ALBUM", new List<string> { album } },
                    { "TITLE", new List<string> { album } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/books/unit/Alpha.m4b", AllTags = Tags("Alpha") },
                new DiscoveredFileWithMetadata { Path = "/books/unit/Beta.m4b", AllTags = Tags("Beta") }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: 1001, forDownloads: false);

            Assert.That(result.UnmatchedFiles, Is.Empty);
            Assert.That(result.MatchedFiles, Has.Length.EqualTo(2));

            var matchesByPath = result.MatchedFiles.ToDictionary(m => m.File.Path);
            Assert.That(matchesByPath["/books/unit/Alpha.m4b"].EditionId, Is.EqualTo(1));
            Assert.That(matchesByPath["/books/unit/Beta.m4b"].EditionId, Is.EqualTo(2));
        }

        [Test]
        public async Task manual_preview_should_use_one_v5_suggestion_for_unmatched_multi_track_identity_group()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> tags = new()
            {
                { "ALBUMARTIST", new List<string> { "Test Author" } },
                { "ALBUM", new List<string> { "Untracked Book" } }
            };

            var files = Enumerable.Range(1, 8)
                .Select(i => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/untracked-book/{i:00}.mp3",
                    AllTags = new Dictionary<string, List<string>>(tags, StringComparer.OrdinalIgnoreCase)
                    {
                        ["TITLE"] = new List<string> { $"Track {i}" }
                    }
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles, Is.Empty);
                Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(v5.Calls, Is.EqualTo(1));
                Assert.That(result.UnmatchedFiles.All(u => u.PotentialAuthors?.SingleOrDefault()?.ProviderId == "hc:12345"), Is.True);
                Assert.That(result.UnmatchedFiles.All(u => u.PotentialAuthors?.SingleOrDefault()?.EditionHardcoverId == "hc-edition-999"), Is.True);
            });
        }

        [Test]
        public async Task manual_preview_should_rerun_the_original_group_once_after_v5_resolves_a_local_work_alias()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var fts = new GroupedRecoveryFtsRepository();
            var v5 = new RecordingV5MatchingService();
            var author = new Author
            {
                Id = 220,
                Name = "Michael Connelly",
                HardcoverAuthorId = "hc:182508"
            };
            var targetBook = new Book
            {
                Id = 2202,
                AuthorId = author.Id,
                Author = author,
                Title = "The Black Echo",
                HardcoverBookId = "hc:223021",
                MediaType = BookMediaType.Audiobook
            };
            var bookService = DispatchProxy.Create<IBookService, WorkBookServiceProxy>();
            ((WorkBookServiceProxy)(object)bookService).Books = new List<Book> { targetBook };
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: new StubAuthorService(author),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: bookService,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var files = Enumerable.Range(1, 4)
                .Select(chapter => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/Schwarzes Echo/{chapter:000}.mp3",
                    DurationSeconds = 14505,
                    AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ARTIST"] = new() { "Michael Connelly" },
                        ["ALBUM"] = new() { "BOSCH Schwarzes Echo" },
                        ["TITLE"] = new() { $"Kapitel {chapter:00} BOSCH Schwarzes Echo" }
                    }
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(
                files,
                restrictToAuthorId: null,
                MatchingContextPresets.ForManualPreview(allowPathFallback: false));

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(match => match.BookId == 2202 && match.EditionId == 22002), Is.True);
                Assert.That(v5.Calls, Is.EqualTo(1));
                Assert.That(fts.ScopedCalls, Is.EqualTo(1), "the post-V5 local rerun must preserve the whole subgroup");
                Assert.That(result.MatchedFiles.All(match => match.Provenance?.Route?.StartsWith("v5_provider_work_group/", StringComparison.Ordinal) == true), Is.True);
            });
        }

        [Test]
        public async Task hard_book_constraint_should_never_widen_to_a_higher_scoring_sibling_book()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: false),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: new BranchingEditionFtsRepository(),
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);
            var file = new DiscoveredFileWithMetadata
            {
                Path = "/downloads/Schwarzes Echo/001.mp3",
                AllTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ARTIST"] = new() { "Michael Connelly" },
                    ["ALBUM"] = new() { "BOSCH Schwarzes Echo" }
                }
            };
            var context = MatchingContextPresets.ForManualPreview(allowPathFallback: false);
            context.HardAllowedBookIds = new List<int> { 2202 };

            var result = await svc.MatchFilesToLibraryAsync(new[] { file }, restrictToAuthorId: null, context);

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(1));
                Assert.That(result.MatchedFiles[0].BookId, Is.EqualTo(2202));
                Assert.That(result.MatchedFiles[0].EditionId, Is.EqualTo(22002));
            });
        }

        [Test]
        public async Task manual_preview_should_not_merge_different_unmatched_books_in_one_folder()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string album, string title)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ALBUMARTIST", new List<string> { "Test Author" } },
                    { "ALBUM", new List<string> { album } },
                    { "TITLE", new List<string> { title } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/downloads/mixed/01-gamma.mp3", AllTags = Tags("Gamma Book", "Track 1") },
                new DiscoveredFileWithMetadata { Path = "/downloads/mixed/02-gamma.mp3", AllTags = Tags("Gamma Book", "Track 2") },
                new DiscoveredFileWithMetadata { Path = "/downloads/mixed/01-delta.mp3", AllTags = Tags("Delta Book", "Track 1") },
                new DiscoveredFileWithMetadata { Path = "/downloads/mixed/02-delta.mp3", AllTags = Tags("Delta Book", "Track 2") }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles, Is.Empty);
                Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(v5.Calls, Is.EqualTo(2));
                Assert.That(result.UnmatchedFiles.Select(u => u.File.Path), Is.EquivalentTo(files.Select(f => f.Path)));
            });
        }

        [Test]
        public async Task manual_preview_should_rematch_chapter_suffix_title_field_without_stable_book_proof()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(int chapter)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "TITLE", new List<string> { $"Alpha Chapter {chapter}" } }
                };
            }

            var files = Enumerable.Range(1, 8)
                .Select(i => new DiscoveredFileWithMetadata
                {
                    Path = $"/downloads/alpha-chapters/{i:00}.mp3",
                    AllTags = Tags(i)
                })
                .ToArray();

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.All(m => m.EditionId == 1), Is.True);
                Assert.That(fts.Calls, Is.GreaterThanOrEqualTo(files.Length));
            });
        }

        [Test]
        public async Task manual_preview_should_rematch_unsampled_different_book_in_chaptered_folder()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string title)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "TITLE", new List<string> { title } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/01.mp3", AllTags = Tags("Alpha Chapter 1") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/02.mp3", AllTags = Tags("Alpha Chapter 2") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/03.mp3", AllTags = Tags("Alpha Chapter 3") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/04.mp3", AllTags = Tags("Alpha Chapter 4") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/05.mp3", AllTags = Tags("Alpha Chapter 5") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/06.mp3", AllTags = Tags("Beta Chapter 1") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/07.mp3", AllTags = Tags("Alpha Chapter 6") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/08.mp3", AllTags = Tags("Alpha Chapter 7") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/09.mp3", AllTags = Tags("Alpha Chapter 8") },
                new DiscoveredFileWithMetadata { Path = "/downloads/chaptered-mixed/10.mp3", AllTags = Tags("Alpha Chapter 9") }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles, Is.Empty);
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(result.MatchedFiles.Single(m => m.File.Path.EndsWith("/06.mp3")).EditionId, Is.EqualTo(2));
                Assert.That(result.MatchedFiles.Where(m => !m.File.Path.EndsWith("/06.mp3")).All(m => m.EditionId == 1), Is.True);
                Assert.That(fts.Calls, Is.GreaterThanOrEqualTo(files.Length));
            });
        }

        [Test]
        public async Task manual_preview_should_not_fast_path_unsampled_meaningful_leftover_into_short_title()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: null,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: new StubAuthorService(new Author { Id = 1001, Name = "Test Author" }),
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string title)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "TITLE", new List<string> { title } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/01.mp3", AllTags = Tags("Alpha Chapter 1") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/02.mp3", AllTags = Tags("Alpha Chapter 2") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/03.mp3", AllTags = Tags("Alpha Chapter 3") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/04.mp3", AllTags = Tags("Alpha Chapter 4") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/05.mp3", AllTags = Tags("Alpha Chapter 5") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/06.mp3", AllTags = Tags("Alpha Horizon Chapter 1") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/07.mp3", AllTags = Tags("Alpha Chapter 6") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/08.mp3", AllTags = Tags("Alpha Chapter 7") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/09.mp3", AllTags = Tags("Alpha Chapter 8") },
                new DiscoveredFileWithMetadata { Path = "/downloads/meaningful-leftover/10.mp3", AllTags = Tags("Alpha Chapter 9") }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.UnmatchedFiles.Select(u => u.File.Path), Is.EquivalentTo(new[] { "/downloads/meaningful-leftover/06.mp3" }));
                Assert.That(result.MatchedFiles, Has.Length.EqualTo(files.Length - 1));
                Assert.That(result.MatchedFiles.All(m => m.EditionId == 1), Is.True);
                Assert.That(fts.Calls, Is.GreaterThanOrEqualTo(files.Length));
            });
        }

        [Test]
        public async Task manual_preview_should_split_different_unmatched_books_by_custom_identity_tag()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var containment = new ContainmentValidator(new TagNormalizer(), logger);
            var fts = new BranchingEditionFtsRepository();
            var v5 = new RecordingV5MatchingService();

            var svc = new FileMatchingService(
                matchingLogger: new NullMatchingUploadLogger(),
                v5MatchingService: v5,
                containmentValidator: containment,
                pendingAuthorImportService: null,
                commandQueue: null,
                authorFolderMatchingService: null,
                rootFolderService: null,
                configService: ConfigServiceTestProxy.Create(),
                authorService: null,
                eventAggregator: null,
                authorLibraryService: null,
                editionFtsRepository: fts,
                bookService: null,
                editionService: null,
                editionRepository: null,
                mediaInfoExtractor: null,
                logger: logger);

            Dictionary<string, List<string>> Tags(string identity, int track)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ARTIST", new List<string> { "Test Author" } },
                    { "BOOKIDENTITY", new List<string> { identity } },
                    { "TITLE", new List<string> { $"Track {track}" } }
                };
            }

            var files = new[]
            {
                new DiscoveredFileWithMetadata { Path = "/downloads/custom-mixed/01-gamma.mp3", AllTags = Tags("Test Author - Gamma Book", 1) },
                new DiscoveredFileWithMetadata { Path = "/downloads/custom-mixed/02-gamma.mp3", AllTags = Tags("Test Author - Gamma Book", 2) },
                new DiscoveredFileWithMetadata { Path = "/downloads/custom-mixed/01-delta.mp3", AllTags = Tags("Test Author - Delta Book", 1) },
                new DiscoveredFileWithMetadata { Path = "/downloads/custom-mixed/02-delta.mp3", AllTags = Tags("Test Author - Delta Book", 2) }
            };

            var result = await svc.MatchFilesToLibraryAsync(files, restrictToAuthorId: null, MatchingContextPresets.ForManualPreview());

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles, Is.Empty);
                Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(files.Length));
                Assert.That(v5.Calls, Is.EqualTo(2));
                Assert.That(result.UnmatchedFiles.Select(u => u.File.Path), Is.EquivalentTo(files.Select(f => f.Path)));
            });
        }
    }
}
