using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NzbDrone.Common.Cache;
using NUnit.Framework;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class PathAuthorInferenceFixture
    {
        [TestCase("Alex Reed", "Alex Reed", true)]
        [TestCase("Alex Reed", "AlexReed", true)]
        [TestCase("Alex Reed", "Reed, Alex", true)]
        [TestCase("Alex Reed", "Alex Reed - A Collection", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("José García", "Jose Garcia", true)]
        [TestCase("Bo Li", "Bo Li", true)]
        [TestCase("李白", "李白", true)]
        [TestCase("Lee", "Lee", true)]
        [TestCase("Alex Reeding", "Alex Reed", false)]
        [TestCase("Alex Reed", "Alex Reeding", false)]
        [TestCase("Alex Reed", "Pat Reed", false)]
        [TestCase("Stan Lee", "Lee", false)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        public void path_inference_must_use_complete_folder_identity(string canonical, string folder, bool matches)
        {
            var harness = new Harness(new[] { NewAuthor(11, canonical) });

            var result = harness.FindAuthorFromPath(folder);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.SameAs(matches ? harness.Authors[0] : null));
                Assert.That(harness.CatalogLoads, Is.EqualTo(1));
                Assert.That(harness.AuthorReads, Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void duplicate_complete_names_must_not_choose_the_first_local_author(bool reverse)
        {
            var authors = new[] { NewAuthor(11, "Alex Reed"), NewAuthor(12, "Alex Reed") };
            var harness = new Harness(reverse ? authors.Reverse() : authors);

            Assert.That(harness.FindAuthorFromPath("Alex Reed"), Is.Null);
            Assert.That(harness.CatalogLoads, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void complete_folder_spelling_must_prefer_the_exact_person_over_a_compatible_middle_name(bool reverse)
        {
            var exact = NewAuthor(11, "Robert Heinlein");
            var authors = new[] { exact, NewAuthor(12, "Robert A Heinlein") };
            var harness = new Harness(reverse ? authors.Reverse() : authors);

            Assert.That(harness.FindAuthorFromPath("Robert Heinlein"), Is.SameAs(exact));
            Assert.That(harness.CatalogLoads, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void decorated_folder_must_not_guess_between_compatible_people_without_a_whole_exact_credit(bool reverse)
        {
            var authors = new[] { NewAuthor(11, "Robert Heinlein"), NewAuthor(12, "Robert A Heinlein") };
            var harness = new Harness(reverse ? authors.Reverse() : authors);

            Assert.That(harness.FindAuthorFromPath("Robert Heinlein - A Collection"), Is.Null);
        }

        [TestCase("Alex Reeding", "Alex Reed")]
        [TestCase("Stan Lee", "Lee")]
        public void path_fallback_must_not_promote_a_partial_person_to_trusted_scope(string canonical, string folder)
        {
            var harness = new Harness(new[] { NewAuthor(11, canonical) });

            var match = harness.Service.HolyGrailMatchFile(harness.File(folder), BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(match, Is.Null);
                Assert.That(harness.Fts.AuthorScopes, Is.EqualTo(new int?[] { null }));
                Assert.That(harness.CatalogLoads, Is.EqualTo(1));
            });
        }

        [TestCase("Alex Reed")]
        [TestCase("Alex Reed - A Collection")]
        public void valid_folder_proof_must_keep_the_existing_path_fallback(string folder)
        {
            var harness = new Harness(new[] { NewAuthor(11, "Alex Reed") });

            var match = harness.Service.HolyGrailMatchFile(harness.File(folder), BookMediaType.Ebook);

            Assert.Multiple(() =>
            {
                Assert.That(match, Is.Not.Null);
                Assert.That(match?.AuthorId, Is.EqualTo(11));
                Assert.That(match?.BookId, Is.EqualTo(101));
                Assert.That(match?.EditionId, Is.EqualTo(201));
                Assert.That(harness.Fts.AuthorScopes, Is.EqualTo(new int?[] { 11 }));
                Assert.That(harness.CatalogLoads, Is.EqualTo(1));
            });
        }

        [Test]
        public void folder_person_proof_must_not_replace_book_title_proof()
        {
            var harness = new Harness(new[] { NewAuthor(11, "Alex Reed") });

            var match = harness.Service.HolyGrailMatchFile(harness.File("Alex Reed", "Other Story"), BookMediaType.Ebook);

            Assert.That(match, Is.Null);
            Assert.That(harness.Fts.AuthorScopes, Is.EqualTo(new int?[] { 11 }));
        }

        [Test]
        public void explicit_caller_author_scope_must_keep_its_existing_authority()
        {
            var harness = new Harness(new[] { NewAuthor(11, "Alex Reed") });

            var match = harness.Service.HolyGrailMatchFile(harness.File("Unrelated Folder"), BookMediaType.Ebook, restrictToAuthorId: 11);

            Assert.That(match, Is.Not.Null);
            Assert.That(match?.AuthorId, Is.EqualTo(11));
            Assert.That(harness.CatalogLoads, Is.Zero);
            Assert.That(harness.Fts.AuthorScopes, Is.EqualTo(new int?[] { 11 }));
        }

        [Test]
        public void embedded_author_next_to_the_actual_edition_title_must_match_when_the_work_title_differs()
        {
            var harness = new Harness(new[] { NewAuthor(11, "Alex Reed") }, editionTitle: "Silent Stream");
            var file = harness.File("Unrelated Folder", "Unrelated Story");
            file.AllTags["album"] = new List<string> { "Alex Reed Silent Stream" };

            var match = harness.Service.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: null, disablePathFallback: true);

            Assert.Multiple(() =>
            {
                Assert.That(match, Is.Not.Null);
                Assert.That(match?.AuthorId, Is.EqualTo(11));
                Assert.That(match?.BookId, Is.EqualTo(101));
                Assert.That(match?.EditionId, Is.EqualTo(201));
                Assert.That(harness.Fts.AuthorScopes, Is.EqualTo(new int?[] { null }));
                Assert.That(harness.CatalogLoads, Is.Zero);
            });
        }

        [TestCase("Ann Smith", "Mary Ann Smith Silent Stream", 1, true)]
        [TestCase("Lee", "Stan Lee Silent Stream", 1, true)]
        [TestCase("Alex Reed", "Silent Stream", 0, false)]
        public void staged_name_occurrence_requires_independent_edition_title_evidence(string canonical, string observed, int rankCalls, bool matches)
        {
            var harness = new Harness(new[] { NewAuthor(11, canonical) }, editionTitle: "Silent Stream");
            var file = harness.File("Unrelated Folder", "Unrelated Story");
            file.AllTags["album"] = new List<string> { observed };

            var match = harness.Service.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: null, disablePathFallback: true);

            Assert.Multiple(() =>
            {
                Assert.That(match != null, Is.EqualTo(matches));
                if (matches)
                {
                    Assert.That(match.AuthorId, Is.EqualTo(11));
                    Assert.That(match.BookId, Is.EqualTo(101));
                    Assert.That(match.EditionId, Is.EqualTo(201));
                }
                Assert.That(harness.Fts.RankCalls, Is.EqualTo(rankCalls));
                Assert.That(harness.CatalogLoads, Is.Zero);
                Assert.That(harness.AuthorReads, Is.EqualTo(1));
            });
        }

        [Test]
        public void possible_staged_pseudonym_span_must_use_the_cached_person_and_actual_edition_title()
        {
            var author = NewAuthor(11, "Alex Reed");
            author.Pseudonyms = new List<string> { "Pat Stone" };
            var harness = new Harness(new[] { author }, editionTitle: "Silent Stream");
            var file = harness.File("Unrelated Folder", "Unrelated Story");
            file.AllTags["album"] = new List<string> { "Pat Stone Silent Stream" };

            var match = harness.Service.HolyGrailMatchFile(file, BookMediaType.Ebook, restrictToAuthorId: null, disablePathFallback: true);

            Assert.Multiple(() =>
            {
                Assert.That(match?.AuthorId, Is.EqualTo(11));
                Assert.That(match?.EditionId, Is.EqualTo(201));
                Assert.That(harness.Fts.RankCalls, Is.EqualTo(1));
                Assert.That(harness.CatalogLoads, Is.Zero);
                Assert.That(harness.AuthorReads, Is.EqualTo(1));
            });
        }

        [Test]
        public void a_large_library_must_read_the_catalog_once_without_per_author_reads()
        {
            using var logs = new LogFactory();
            var target = new NLog.Targets.MemoryTarget("path-author-lookup-counts") { Layout = "${message}" };
            var configuration = new NLog.Config.LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            var authors = Enumerable.Range(1, 10000).Select(index => NewAuthor(index, $"Other Writer {index}")).ToList();
            authors.Add(NewAuthor(10001, "Alex Reed"));
            var harness = new Harness(authors, logger: logs.GetLogger(nameof(AuthorService)));
            var timer = Stopwatch.StartNew();
            var results = new List<Author>();

            for (var operation = 0; operation < 100; operation++)
            {
                results.Add(harness.FindAuthorFromPath("Alex Reed"));
            }

            timer.Stop();
            var preparation = target.Logs.Where(message => message.StartsWith("Author name catalog: indexed ", StringComparison.Ordinal)).ToList();
            var indexed = preparation.Sum(message => int.Parse(message.Split(' ')[4]));
            TestContext.WriteLine("Path author lookup: authors={0}, operations=100, indexed={1}, catalog_loads={2}, author_reads={3}, elapsed_ms={4:F2}",
                authors.Count, indexed, harness.CatalogLoads, harness.AuthorReads, timer.Elapsed.TotalMilliseconds);
            Assert.That(results, Is.All.SameAs(authors[^1]));
            Assert.That(harness.CatalogLoads, Is.EqualTo(1));
            Assert.That(harness.AuthorReads, Is.Zero);
            Assert.That(preparation, Has.Count.EqualTo(1));
            Assert.That(indexed, Is.EqualTo(authors.Count));
        }

        private static Author NewAuthor(int id, string name) => new Author { Id = id, Name = name };

        private sealed class Harness
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "path-author-fixture");
            public List<Author> Authors { get; }
            public FileMatchingService Service { get; }
            public RecordingFtsRepository Fts { get; }
            public int CatalogLoads { get; private set; }
            public int AuthorReads { get; private set; }

            public Harness(IEnumerable<Author> authors, Logger logger = null, string editionTitle = "Quiet River")
            {
                Authors = authors.ToList();
                logger ??= LogManager.GetCurrentClassLogger();
                var book = new Book { Id = 101, AuthorId = Authors[0].Id, Author = Authors[0], Title = "Quiet River", MediaType = BookMediaType.Ebook };
                var candidate = new EditionFtsMatch
                {
                    BookId = book.Id,
                    EditionId = 201,
                    AuthorId = book.AuthorId,
                    AuthorName = Authors[0].Name,
                    BookTitle = book.Title,
                    EditionTitle = editionTitle,
                    MatchingTitle = editionTitle.ToLowerInvariant()
                };
                Fts = new RecordingFtsRepository(candidate);
                var repository = InterfaceProxy.Proxy<IAuthorRepository>((method, args) => method.Name switch
                {
                    nameof(IAuthorRepository.All) => LoadCatalog(),
                    nameof(IAuthorRepository.Get) => LoadAuthor((int)args[0]),
                    _ => throw new NotSupportedException(method.Name)
                });
                var authorService = new AuthorService(repository, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null),
                    authorPathBuilder: null, rootFolderService: null, commandQueueManager: null, cacheManager: new CacheManager(),
                    bookRepository: null, mediaFileService: null, logger: logger, importListExclusionService: null,
                    deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => null), bookService: new Lazy<IBookService>(() => null),
                    mainDatabase: InterfaceProxy.Proxy<IMainDatabase>((method, _) => throw new NotSupportedException(method.Name)));
                var bookService = InterfaceProxy.Proxy<IBookService>((method, _) => method.Name switch
                {
                    nameof(IBookService.GetBooks) => new List<Book> { book },
                    nameof(IBookService.GetBook) => book,
                    nameof(IBookService.GetBooksByAuthor) => new List<Book> { book },
                    _ => throw new NotSupportedException(method.Name)
                });
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name switch
                {
                    nameof(IRootFolderService.GetBestRootFolderPath) => _root,
                    _ => throw new NotSupportedException(method.Name)
                });
                Service = new FileMatchingService(
                    matchingLogger: null,
                    v5MatchingService: null,
                    containmentValidator: new ContainmentValidator(new TagNormalizer(), logger),
                    pendingAuthorImportService: null,
                    commandQueue: null,
                    authorFolderMatchingService: new AuthorFolderMatchingService(null, logger),
                    rootFolderService: roots,
                    configService: ConfigServiceTestProxy.Create(usePathAsTagsFallback: true),
                    authorService: authorService,
                    eventAggregator: null,
                    authorLibraryService: null,
                    editionFtsRepository: Fts,
                    bookService: bookService,
                    editionService: null,
                    editionRepository: null,
                    mediaInfoExtractor: null,
                    logger: logger);
            }

            public DiscoveredFileWithMetadata File(string folder, string title = "Quiet River") => new DiscoveredFileWithMetadata
            {
                Path = Path.Combine(_root, folder, title, title + ".epub"),
                AllTags = new Dictionary<string, List<string>>()
            };

            public Author FindAuthorFromPath(string folder) => (Author)typeof(FileMatchingService)
                .GetMethod("FindAuthorFromPath", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(Service, new object[] { File(folder).Path });

            private List<Author> LoadCatalog()
            {
                CatalogLoads++;
                return Authors;
            }

            private Author LoadAuthor(int id)
            {
                AuthorReads++;
                return Authors.Single(author => author.Id == id);
            }
        }

        private sealed class RecordingFtsRepository : IEditionFtsRepository
        {
            private readonly EditionFtsMatch Candidate;
            public List<int?> AuthorScopes { get; } = new List<int?>();
            public int RankCalls { get; private set; }

            public RecordingFtsRepository(EditionFtsMatch candidate) => Candidate = candidate;
            public void EnsureIndexPopulated() => RebuildIndex();
            public bool FtsTableExists() => true;
            public void RebuildIndex() => throw new NotSupportedException();

            public List<BookFtsMatch> RecallBooks(int? authorId, IEnumerable<string> tokens, BookMediaType mediaType,
                Action<EditionFtsTraceEvent> trace = null, int limit = 20, bool monitoredOnly = false)
            {
                AuthorScopes.Add(authorId);
                return new List<BookFtsMatch>
                {
                    new BookFtsMatch { BookId = Candidate.BookId, AuthorId = Candidate.AuthorId, AuthorName = Candidate.AuthorName, BookTitle = Candidate.BookTitle }
                };
            }

            public List<EditionFtsMatch> RankEditions(IReadOnlyCollection<BookFtsMatch> recalledBooks,
                IReadOnlyCollection<EditionFtsFieldQuery> fieldQueries, BookMediaType mediaType, Action<EditionFtsTraceEvent> trace = null)
            {
                RankCalls++;
                Candidate.Stage2FieldHits = fieldQueries.Where(query => query.Terms.Any(term => Candidate.MatchingTitle.Split(' ').Contains(term)))
                    .Select(query => new EditionFtsFieldHit { FieldKey = query.Key, SourceFields = query.SourceFields, TitleHit = true, TitleBm25 = 1.0 })
                    .ToList();
                return new List<EditionFtsMatch> { Candidate };
            }
        }
    }
}
