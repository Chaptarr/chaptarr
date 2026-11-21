using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public partial class AuthorServiceLookupHydrationFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public Action<IEvent> OnEvent { get; set; }

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                OnEvent?.Invoke(@event);
            }
        }

        private sealed class StubAuthorRepository : IAuthorRepository
        {
            public List<int> GetDueInitialFormatSync(DateTime now) => throw new NotSupportedException();

            private readonly Author _lookupAuthor;
            private Author _byIdAuthor;
            public int GetCallCount { get; private set; }
            public int AllCallCount { get; private set; }
            public List<Author> Authors { get; set; }
            public Action BeforeWrite { get; set; }
            public Action AfterRead { get; set; }
            public int WriteCallCount { get; private set; }

            public StubAuthorRepository(Author lookupAuthor, Author byIdAuthor = null)
            {
                _lookupAuthor = lookupAuthor;
                _byIdAuthor = byIdAuthor ?? lookupAuthor;
                Authors = new List<Author> { lookupAuthor };
            }

            // Frozen production still uses this repository member for the same public regression fixture.
            public Author FindByName(string cleanName) => Authors
                .Where(author => string.Equals(author.Name.CleanAuthorName(), cleanName, StringComparison.OrdinalIgnoreCase))
                .ExclusiveOrDefault();
            public Author FindByGoodreadsId(string goodreadsId) => _lookupAuthor;
            public Author FindByHardcoverId(string hardcoverId) => _lookupAuthor;
            public Author FindByAudnexusId(string audnexusId) => _lookupAuthor;
            public Author FindByOpenLibraryId(string openLibraryId) => _lookupAuthor;
            public Author FindByGoogleBooksId(string googleBooksAuthorId) => _lookupAuthor;
            public Author Get(int id)
            {
                GetCallCount++;
                return Authors.Any(author => author.Id == id)
                    ? (id == _byIdAuthor?.Id ? _byIdAuthor : Authors.Single(author => author.Id == id))
                    : null;
            }
            public IEnumerable<Author> All()
            {
                AllCallCount++;
                var snapshot = Authors.ToList();
                AfterRead?.Invoke();
                return snapshot;
            }
            public IEnumerable<Author> Get(IEnumerable<int> ids) => Authors.Where(author => ids.Contains(author.Id)).ToList();

            public int Count() => throw new NotImplementedException();
            public Author Find(int id) => throw new NotImplementedException();
            public Author Insert(Author model)
            {
                BeforeWrite?.Invoke();
                WriteCallCount++;
                Authors = Authors.Append(model).ToList();
                return model;
            }
            public Author Update(Author model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public Author Update(Author model)
            {
                BeforeWrite?.Invoke();
                WriteCallCount++;
                Authors = Authors.Select(author => author.Id == model.Id ? model : author).ToList();
                if (_byIdAuthor?.Id == model.Id)
                {
                    _byIdAuthor = model;
                }
                return model;
            }
            public Author Upsert(Author model) => throw new NotImplementedException();
            public void SetFields(Author model, params Expression<Func<Author, object>>[] properties) => throw new NotImplementedException();
            public void Delete(Author model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void InsertMany(IList<Author> model)
            {
                BeforeWrite?.Invoke();
                WriteCallCount++;
                Authors = Authors.Concat(model).ToList();
            }
            public void InsertMany(IList<Author> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<Author> model)
            {
                BeforeWrite?.Invoke();
                WriteCallCount++;
                var changed = model.ToDictionary(author => author.Id);
                Authors = Authors.Select(author => changed.GetValueOrDefault(author.Id) ?? author).ToList();
                if (_byIdAuthor != null && changed.TryGetValue(_byIdAuthor.Id, out var updated))
                {
                    _byIdAuthor = updated;
                }
            }
            public void SetFields(IList<Author> models, params Expression<Func<Author, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<Author> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids)
            {
                BeforeWrite?.Invoke();
                WriteCallCount++;
                var removed = ids.ToHashSet();
                Authors = Authors.Where(author => !removed.Contains(author.Id)).ToList();
            }
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => throw new NotImplementedException();
            public Author Single() => throw new NotImplementedException();
            public Author SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<Author> GetPaged(PagingSpec<Author> pagingSpec) => throw new NotImplementedException();
            public List<Author> FindExisting(IEnumerable<int> ids) => throw new NotImplementedException();
            public bool AuthorPathExists(string path) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public List<Author> AllAuthorPaths() => new List<Author> { _lookupAuthor };
            public Dictionary<int, List<int>> AllAuthorTags() => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public bool TryClaimAuthorImport(string providerId, int leaseSec = 300) => throw new NotImplementedException();
            public void ReleaseAuthorImportClaim(string providerId) => throw new NotImplementedException();
            public int DeleteExpiredClaims() => throw new NotImplementedException();
        }

        private class NoOpProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotSupportedException(targetMethod.Name);
            }
        }

        private class NoOpCommandQueue : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IManageCommandQueue.Push))
                {
                    return null;
                }

                throw new NotSupportedException(targetMethod.Name);
            }
        }

        private sealed class NoOpDeleteMediaFiles : IDeleteMediaFiles
        {
            public List<BookFile> DeleteBookFiles(List<Book> books) => new();
            public void DeleteTrackFile(Author author, BookFile bookFile) { }
            public void DeleteTrackFile(BookFile bookFile, string subfolder = "") { }
            public List<BookFile> DeleteAuthorFiles(List<Author> authors, BookMediaType? mediaType = null, bool deleteOtherAuthorsFiles = false) => new();
            public AuthorDeletionPreview PreviewAuthorFiles(List<Author> authors, BookMediaType? mediaType = null) => new();
        }

        private static AuthorService CreateSubject(IAuthorRepository repository, Logger logger = null,
            CacheManager cacheManager = null, StubEventAggregator events = null)
        {
            return new AuthorService(
                repository,
                events ?? new StubEventAggregator(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: DispatchProxy.Create<IManageCommandQueue, NoOpCommandQueue>(),
                cacheManager: cacheManager ?? new CacheManager(),
                bookRepository: null,
                mediaFileService: null,
                logger: logger ?? LogManager.GetCurrentClassLogger(),
                importListExclusionService: null,
                deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => null),
                bookService: new Lazy<IBookService>(() => null), mainDatabase: DispatchProxy.Create<IMainDatabase, NoOpProxy>());
        }

        private static LogFactory CreateLookupLogs(out NLog.Targets.MemoryTarget target)
        {
            var logs = new LogFactory();
            target = new NLog.Targets.MemoryTarget("author-lookup-counts") { Layout = "${message}" };
            var configuration = new NLog.Config.LoggingConfiguration(logs);
            configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
            logs.Configuration = configuration;
            return logs;
        }

        private static int PreparedCatalogCount(NLog.Targets.MemoryTarget target)
        {
            return target.Logs.Count(message => message.StartsWith("Author name catalog: indexed ", StringComparison.Ordinal));
        }

        [Test]
        public void indexed_candidates_preserve_full_scan_results_for_compact_inverted_and_suffix_names()
        {
            var names = new[] { "Frank Herbert", "Frank Herberts", "Nora Robert", "Nora Roberts", "J. K. Rowling", "JKRowling",
                "Isaac Asimov", "Asimov, Isaac", "Robert Louis Stevenson", "George R. R. Martin", "Jean-Paul Sartre",
                "John and Jane Smith", "John Smith", "Jane Smiths", "Александр Пушкин", "王小明", "Émile Zola", "Mary-Jane Smith-Jones" };
            var authors = names.Select((name, i) => new Author { Id = i + 1, Name = name }).ToList();
            var repository = new StubAuthorRepository(authors[0]) { Authors = authors };
            var service = CreateSubject(repository);
            foreach (var query in names.Concat(new[] { "Frank Herbert’s", "Nora A Roberts", "I. Asimov", "IsaacAsimov", "Robert Stevenson",
                "J K Rowling", "George RR Martin", "John Smith and Jane Smiths", "Emile Zola", "А. Пушкин", "No Such Author" }))
            {
                var tokens = ReleaseTitleMatchScorer.TokenizeWithSpans(query);
                var prepared = AuthorNameMatcher.PrepareName(query, tokens);
                var expected = authors.Where(a => AuthorNameMatcher.AuthorNamesExactlyMatch(AuthorNameMatcher.PrepareName(a.Name), prepared)).ToList();
                for (var pass = 0; pass < 2 && expected.Count == 0; pass++)
                {
                    expected = authors.Where(a =>
                    {
                        var name = AuthorNameMatcher.PrepareName(a.Name);
                        return (pass == 0 || AuthorNameMatcher.HasSurnameSuffixAnchor(name.Tokens, prepared.Tokens)) &&
                            (AuthorNameMatcher.AuthorNamesMatch(name, prepared, pass > 0) || AuthorNameMatcher.ContainsAuthorName(name, query, tokens, pass > 0));
                    }).ToList();
                }
                Assert.That(service.GetCandidates(query).Select(a => a.Id), Is.EqualTo(expected.Select(a => a.Id)), query);
            }
            Assert.That(repository.AllCallCount, Is.EqualTo(1));
        }

        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("Frank Herbert", "Frank Herberts", true)]
        [TestCase("Nora Roberts", "Nora Robert", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Robert Louis Stevenson", "Robert Stevenson", true)]
        [TestCase("Robert Louis Stevenson", "Stevenson, Robert", true)]
        [TestCase("Robert Louis Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("C S Lewis", "CS Lewis", true)]
        [TestCase("Лев Н. Толстой", "Лев Толстой", true)]
        [TestCase("Лев Н. Толстой", "Лев П. Толстой", false)]
        [TestCase("Stephen King", "Stephen Kingsley", false)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("Li Wei", "Louise Irene Wei", false)]
        [TestCase("がとう", "かとう", false)]
        [TestCase("がとう", "か\u3099とう", true)]
        public void name_lookup_routes_should_use_the_shared_author_rule(string stored, string supplied, bool matches)
        {
            var author = new Author { Id = 1, Name = stored, NameLastFirst = stored };
            var repository = new StubAuthorRepository(author);
            var subject = CreateSubject(repository);

            Assert.That(AuthorNameMatcher.AuthorNamesMatch(stored, supplied), Is.EqualTo(matches));
            Assert.That(subject.FindByName(supplied), Is.EqualTo(matches ? author : null));
            Assert.That(subject.GetCandidates(supplied), Is.EqualTo(matches ? new[] { author } : Array.Empty<Author>()));
            Assert.That(repository.AllCallCount, Is.EqualTo(1));
            Assert.That(repository.GetCallCount, Is.Zero);
        }

        [TestCase("Tom A Smith", "Tom B Smith", "Tom Smith")]
        [TestCase("Smith, John", "John Smith", "John Smith")]
        [TestCase("Linda Kay Silva", "Linda Kay Silva", "Linda Kay Silva")]
        [TestCase("John Smith", "Smith John", "Smith, John")]
        public void compatible_people_should_remain_ambiguous_in_either_order(string first, string second, string supplied)
        {
            var authors = new[]
            {
                new Author { Id = 1, Name = first, NameLastFirst = first },
                new Author { Id = 2, Name = second, NameLastFirst = second }
            };
            foreach (var candidates in new[] { authors, authors.Reverse().ToArray() })
            {
                var repository = new StubAuthorRepository(authors[0]) { Authors = candidates.ToList() };
                using var logs = CreateLookupLogs(out var messages);
                var subject = CreateSubject(repository, logs.GetLogger("AuthorLookup"));

                Assert.That(subject.FindByName(supplied), Is.Null);
                Assert.That(messages.Logs.Any(message => message.Contains("is ambiguous: matches author IDs")), Is.True);
                Assert.That(subject.GetCandidates(supplied), Is.EquivalentTo(authors));
                Assert.That(repository.AllCallCount, Is.EqualTo(1));
                Assert.That(repository.GetCallCount, Is.Zero);
            }
        }

        [TestCase("Robert Heinlein", "Robert A. Heinlein", "Robert Heinlein")]
        [TestCase("Nora Roberts", "Nora Robert", "Nora Roberts")]
        [TestCase("Nora Roberts", "Nora Robert", "Nora A Roberts")]
        [TestCase("Nora Robert", "Nora Roberts", "Nora A Robert")]
        [TestCase("Nora Roberts", "Nora Robert", "Roberts, Nora A")]
        [TestCase("Nora Robert", "Nora Roberts", "Nora Robert")]
        [TestCase("Robert A. Heinlein", "Robert Heinlein", "Robert A Heinlein")]
        [TestCase("Stevenson, Robert Louis", "Robert Stevenson", "Robert Louis Stevenson")]
        [TestCase("Renée Ballard", "Renée A Ballard", "Rene\u0301e Ballard")]
        [TestCase("CS Lewis", "CS A Lewis", "CS Lewis")]
        [TestCase("Ana Bell", "An Abell", "Ana Bell")]
        [TestCase("𐐀 Reader", "𐐀 A Reader", "𐐨 Reader")]
        public void exact_name_or_family_should_precede_a_suffix_only_match_in_either_order(
            string exact, string compatible, string supplied)
        {
            var authors = new[]
            {
                new Author { Id = 1, Name = exact, NameLastFirst = exact },
                new Author { Id = 2, Name = compatible, NameLastFirst = compatible }
            };
            Assert.That(AuthorNameMatcher.AuthorNamesMatch(compatible, supplied), Is.True);

            foreach (var candidates in new[] { authors, authors.Reverse().ToArray() })
            {
                var repository = new StubAuthorRepository(authors[0]) { Authors = candidates.ToList() };
                var subject = CreateSubject(repository);

                Assert.Multiple(() =>
                {
                    Assert.That(subject.FindByName(supplied), Is.SameAs(authors[0]));
                    Assert.That(subject.GetCandidates(supplied), Is.EqualTo(new[] { authors[0] }));
                    Assert.That(repository.AllCallCount, Is.EqualTo(1));
                    Assert.That(repository.GetCallCount, Is.Zero);
                });
            }
        }

        [Test]
        public void identical_prepared_names_should_remain_ambiguous_even_when_another_name_is_compatible()
        {
            var exact = new[]
            {
                new Author { Id = 1, Name = "Robert Heinlein" },
                new Author { Id = 2, Name = "Heinlein, Robert" }
            };
            var authors = exact.Append(new Author { Id = 3, Name = "Robert A Heinlein" }).ToArray();
            foreach (var candidates in new[] { authors, authors.Reverse().ToArray() })
            {
                var repository = new StubAuthorRepository(exact[0]) { Authors = candidates.ToList() };
                var subject = CreateSubject(repository);

                Assert.Multiple(() =>
                {
                    Assert.That(subject.FindByName("Robert Heinlein"), Is.Null);
                    Assert.That(subject.GetCandidates("Robert Heinlein"), Is.EquivalentTo(exact));
                    Assert.That(repository.AllCallCount, Is.EqualTo(1));
                    Assert.That(repository.GetCallCount, Is.Zero);
                });
            }
        }

        [TestCase("Ann and Pat Smith")]
        [TestCase("Ann Smith; Pat Smith")]
        public void every_credited_author_should_remain_a_candidate_even_when_one_whole_name_is_compatible(string supplied)
        {
            var authors = new[]
            {
                new Author { Id = 1, Name = "Ann Smith" },
                new Author { Id = 2, Name = "Pat Smith" }
            };
            foreach (var candidates in new[] { authors, authors.Reverse().ToArray() })
            {
                var repository = new StubAuthorRepository(authors[0]) { Authors = candidates.ToList() };
                var subject = CreateSubject(repository);

                Assert.Multiple(() =>
                {
                    Assert.That(subject.FindByName(supplied), Is.Null);
                    Assert.That(subject.GetCandidates(supplied), Is.EquivalentTo(authors));
                    Assert.That(repository.AllCallCount, Is.EqualTo(1));
                    Assert.That(repository.GetCallCount, Is.Zero);
                });
            }
        }

        [TestCase(2)]
        [TestCase(398)]
        [TestCase(10000)]
        [TestCase(100000)]
        public void repeated_exact_lookup_should_reuse_the_catalog_without_per_author_reads(int count)
        {
            AssertRepeatedLookupUsesCatalog(count, missingMiddle: false);
        }

        [TestCase(398, false)]
        [TestCase(10000, false)]
        [TestCase(100000, false)]
        [TestCase(398, true)]
        [TestCase(10000, true)]
        [TestCase(100000, true)]
        public void repeated_warm_compatible_lookup_should_reuse_the_catalog_without_per_author_reads(int count, bool surnameSuffix)
        {
            AssertRepeatedLookupUsesCatalog(count, missingMiddle: true, surnameSuffix);
        }

        private static void AssertRepeatedLookupUsesCatalog(int count, bool missingMiddle, bool surnameSuffix = false)
        {
            using var logs = CreateLookupLogs(out var target);
            var exact = new Author { Id = 1, Name = missingMiddle ? "Robert Louis Stevenson" : "Robert Heinlein" };
            var supplied = missingMiddle ? (surnameSuffix ? "Robert Stevensons" : "Robert Stevenson") : exact.Name;
            var repository = new StubAuthorRepository(exact)
            {
                Authors = new[] { exact }
                    .Concat(Enumerable.Range(2, count - 2).Select(id => new Author { Id = id, Name = $"Unrelated Writer {id}" }))
                    .Append(new Author { Id = count, Name = missingMiddle ? "Other Writer" : "Robert A Heinlein" })
                    .ToList()
            };
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)));
            if (missingMiddle)
            {
                Assert.That(subject.GetCandidates(supplied), Is.EqualTo(new[] { exact }));
            }

            var operations = count >= 100000 ? 10 : 100;
            var candidates = new List<List<Author>>();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            for (var operation = 0; operation < operations; operation++)
            {
                candidates.Add(subject.GetCandidates(supplied));
            }

            stopwatch.Stop();
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var lookupLogs = target.Logs.Where(message => message.StartsWith("Author name lookup: examined ", StringComparison.Ordinal))
                .Skip(missingMiddle ? 1 : 0).ToList();
            var examined = lookupLogs.Sum(message => int.Parse(message.Split(' ')[4].Split('/')[0]));
            var prepared = target.Logs.Where(message => message.StartsWith("Author name catalog: indexed ", StringComparison.Ordinal))
                .Sum(message => int.Parse(message.Split(' ')[4]));
            var cohort = missingMiddle ? (surnameSuffix ? "warm surname-suffix" : "warm missing-middle") : "exact including catalog preparation";
            TestContext.Progress.WriteLine($"Author {cohort} lookup: catalog={count}, candidateOperations={operations}, maxMatches={candidates.Max(items => items.Count)}, allocatedBytes={allocatedBytes}, examined={examined}, prepared={prepared}, repositoryAllCalls={repository.AllCallCount}, perAuthorReads={repository.GetCallCount}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}");
            Assert.Multiple(() =>
            {
                Assert.That(candidates.All(items => items.Count == 1 && ReferenceEquals(items[0], exact)), Is.True);
                Assert.That(repository.AllCallCount, Is.EqualTo(1));
                Assert.That(repository.GetCallCount, Is.Zero);
                Assert.That(lookupLogs, Has.Count.EqualTo(operations));
                Assert.That(PreparedCatalogCount(target), Is.EqualTo(1));
                Assert.That(prepared, Is.EqualTo(count));
            });
        }

        [Test]
        public void catalog_read_should_prepare_names_only_when_lookup_needs_them()
        {
            using var logs = CreateLookupLogs(out var target);
            var author = new Author { Id = 1, Name = "Robert Heinlein" };
            var repository = new StubAuthorRepository(author);
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)));

            Assert.That(subject.GetAllAuthors(), Is.EqualTo(new[] { author }));
            Assert.That(PreparedCatalogCount(target), Is.Zero);
            Assert.That(subject.FindByName(author.Name), Is.SameAs(author));
            Assert.That(PreparedCatalogCount(target), Is.EqualTo(1));
            Assert.That(repository.AllCallCount, Is.EqualTo(1));
        }

        [Test]
        public void author_update_should_invalidate_prepared_names_with_the_existing_catalog()
        {
            using var logs = CreateLookupLogs(out var target);
            var author = new Author { Id = 1, Name = "Robert A Heinlein" };
            var repository = new StubAuthorRepository(author);
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)));
            Assert.That(subject.GetCandidates(author.Name), Is.EqualTo(new[] { author }));
            var updated = new Author { Id = author.Id, Name = "Robert B Heinlein" };

            subject.UpdateAuthor(updated);

            Assert.Multiple(() =>
            {
                Assert.That(subject.GetCandidates(author.Name), Is.Empty);
                Assert.That(subject.GetCandidates(updated.Name), Is.EqualTo(new[] { updated }));
                Assert.That(repository.AllCallCount, Is.EqualTo(2));
                Assert.That(PreparedCatalogCount(target), Is.EqualTo(2));
            });
        }

        [TestCase(null)]
        [TestCase("audiobook")]
        [TestCase("ebook")]
        public void author_updates_preserve_retry_state_at_the_service_boundary(string mediaType)
        {
            var due = DateTime.UtcNow.AddMinutes(3);
            var author = new Author { Id = 1, Name = "Example Writer", AddOptions = new AddAuthorOptions
                { InitialFormatSyncFailureCount = 4, InitialFormatSyncNotBefore = due } };
            var repository = new StubAuthorRepository(author);
            var resource = new Chaptarr.Api.V1.Author.AuthorResource { Id = author.Id, AuthorName = author.Name,
                AddOptions = new AddAuthorOptions { InitialFormatSyncFailureCount = 999, InitialFormatSyncNotBefore = due.AddYears(1) } };
            var requestAuthor = new Author { Id = author.Id, Name = author.Name };
            var model = Chaptarr.Api.V1.Author.AuthorResourceMapper.ToModel(resource, requestAuthor, null, mediaType);
            CreateSubject(repository).UpdateAuthor(model);
            Assert.That(repository.Get(author.Id).AddOptions, Is.SameAs(author.AddOptions));
            Assert.That(model.AddOptions.InitialFormatSyncFailureCount, Is.EqualTo(4));
            Assert.That(model.AddOptions.InitialFormatSyncNotBefore, Is.EqualTo(due));
        }

        private static Author CacheAuthor(int id, string name) => new Author
        {
            Id = id,
            Name = name,
            Path = $@"C:\authors\{id}".AsOsAgnostic()
        };

        private static void WriteAuthor(AuthorService subject, string operation, Author changed)
        {
            switch (operation)
            {
                case "add": subject.AddAuthor(changed, false); break;
                case "add-many": subject.AddAuthors(new List<Author> { changed }, false); break;
                case "update": subject.UpdateAuthor(changed); break;
                case "update-many": subject.UpdateAuthors(new List<Author> { changed }, false); break;
                case "delete": subject.DeleteAuthor(changed.Id, false); break;
                case "delete-many": subject.DeleteAuthors(new List<int> { changed.Id }, false, applyToBothFormats: true); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        [TestCase("update")]
        [TestCase("add-many")]
        public async Task a_catalog_load_started_before_a_write_must_not_refill_the_invalidated_cache(string operation)
        {
            using var loaded = new ManualResetEventSlim();
            using var releaseLoad = new ManualResetEventSlim();
            using var writing = new ManualResetEventSlim();
            var original = CacheAuthor(1, "Original Writer");
            var changed = CacheAuthor(operation == "add-many" ? 2 : 1, "Changed Writer");
            var repository = new StubAuthorRepository(original);
            var subject = CreateSubject(repository);
            repository.AfterRead = () =>
            {
                if (repository.AllCallCount == 1)
                {
                    loaded.Set();
                    Assert.That(releaseLoad.Wait(TimeSpan.FromSeconds(10)), Is.True, "Controlled cache load was not released");
                }
            };
            repository.BeforeWrite = () => writing.Set();

            var reader = Task.Run(() => subject.GetAllAuthors());
            Task writer = null;
            try
            {
                Assert.That(loaded.Wait(TimeSpan.FromSeconds(10)), Is.True, "Catalog load did not reach the controlled snapshot boundary");
                writer = Task.Run(() => WriteAuthor(subject, operation, changed));
                Assert.That(writing.Wait(TimeSpan.FromSeconds(10)), Is.True, "Writer did not reach persistence");
                var finishedBeforeLoad = writer.Wait(TimeSpan.FromSeconds(1));
                TestContext.WriteLine($"catalog_write operation={operation} completed_before_load_release={finishedBeforeLoad}");
            }
            finally
            {
                releaseLoad.Set();
            }

            await reader;
            if (writer != null) await writer;
            Assert.Multiple(() =>
            {
                Assert.That(subject.GetCandidates(changed.Name), Is.EqualTo(new[] { changed }));
                Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(operation == "add-many" ? new[] { original } : Array.Empty<Author>()));
                Assert.That(repository.WriteCallCount, Is.EqualTo(1));
                Assert.That(repository.AllCallCount, Is.EqualTo(2));
            });
        }

        [TestCase("add")]
        [TestCase("add-many")]
        [TestCase("update")]
        [TestCase("update-many")]
        [TestCase("delete")]
        [TestCase("delete-many")]
        public async Task successful_write_should_discard_a_catalog_read_while_persistence_is_paused(string operation)
        {
            using var logs = CreateLookupLogs(out var target);
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            var original = CacheAuthor(1, "Original Writer");
            var changed = CacheAuthor(operation.StartsWith("add", StringComparison.Ordinal) ? 2 : 1, "Changed Writer");
            var repository = new StubAuthorRepository(original);
            var events = new StubEventAggregator();
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)), events: events);
            var deleting = operation.StartsWith("delete", StringComparison.Ordinal);
            Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
            Assert.That(subject.GetAuthor(original.Id), Is.SameAs(original));

            events.OnEvent = @event =>
            {
                if (@event is AuthorDeletedEvent)
                {
                    Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
                    Assert.That(subject.GetAuthor(original.Id), Is.SameAs(original));
                }
                else if (@event is AuthorAddedEvent || @event is AuthorsImportedEvent || @event is AuthorEditedEvent)
                {
                    Assert.That(subject.GetCandidates(changed.Name), Is.EqualTo(new[] { changed }));
                }
            };
            repository.BeforeWrite = () =>
            {
                entered.Set();
                Assert.That(proceed.Wait(TimeSpan.FromSeconds(10)), Is.True, "Controlled reader did not release the write");
            };

            var writer = Task.Run(() => WriteAuthor(subject, operation, changed));
            try
            {
                Assert.That(entered.Wait(TimeSpan.FromSeconds(10)), Is.True, "Write did not reach the controlled persistence boundary");
                Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
                Assert.That(subject.GetCandidates(changed.Name), Is.Empty);
            }
            finally
            {
                proceed.Set();
            }

            await writer;
            Assert.Multiple(() =>
            {
                Assert.That(subject.GetCandidates(changed.Name), Is.EqualTo(deleting ? Array.Empty<Author>() : new[] { changed }));
                Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(operation.StartsWith("add", StringComparison.Ordinal)
                    ? new[] { original } : Array.Empty<Author>()));
                Assert.That(subject.GetAuthor(changed.Id), Is.SameAs(deleting ? null : changed));
                Assert.That(repository.WriteCallCount, Is.EqualTo(1));
                Assert.That(repository.AllCallCount, Is.EqualTo(2));
                Assert.That(PreparedCatalogCount(target), Is.EqualTo(2));
            });
        }

        [TestCase("add")]
        [TestCase("add-many")]
        [TestCase("update")]
        [TestCase("update-many")]
        [TestCase("delete")]
        [TestCase("delete-many")]
        public void failed_write_should_not_discard_the_unchanged_catalog(string operation)
        {
            var original = CacheAuthor(1, "Original Writer");
            var changed = CacheAuthor(operation.StartsWith("add", StringComparison.Ordinal) ? 2 : 1, "Changed Writer");
            var repository = new StubAuthorRepository(original);
            var subject = CreateSubject(repository);
            var cached = subject.GetAllAuthors();
            Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
            repository.BeforeWrite = () => throw new InvalidOperationException("Controlled persistence failure");

            Assert.Throws<InvalidOperationException>(() => WriteAuthor(subject, operation, changed));

            Assert.Multiple(() =>
            {
                Assert.That(subject.GetAllAuthors(), Is.SameAs(cached));
                Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
                Assert.That(subject.GetCandidates(changed.Name), Is.Empty);
                Assert.That(repository.AllCallCount, Is.EqualTo(1));
                Assert.That(repository.WriteCallCount, Is.Zero);
            });
        }

        [Test]
        public void saving_a_mutated_cached_author_should_rebuild_its_prepared_name()
        {
            using var logs = CreateLookupLogs(out var target);
            var original = CacheAuthor(1, "Original Writer");
            var repository = new StubAuthorRepository(original);
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)));
            var cached = subject.GetCandidates(original.Name).Single();
            cached.Name = "Changed Writer";

            subject.UpdateAuthor(cached);

            Assert.Multiple(() =>
            {
                Assert.That(subject.GetCandidates("Original Writer"), Is.Empty);
                Assert.That(subject.GetCandidates(cached.Name), Is.EqualTo(new[] { cached }));
                Assert.That(repository.AllCallCount, Is.EqualTo(2));
                Assert.That(PreparedCatalogCount(target), Is.EqualTo(2));
            });
        }

        private static object CatalogItem(CacheManager manager)
        {
            var cache = manager.Caches.Single(value => value.GetType().GetGenericArguments()[0] != typeof(Author));
            var store = (System.Collections.IDictionary)cache.GetType()
                .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache);
            return store["GetAllAuthors"];
        }

        [Test]
        public void warm_reads_should_preserve_absolute_expiry_and_expiration_should_refresh_names_once()
        {
            using var logs = CreateLookupLogs(out var target);
            var original = CacheAuthor(1, "Original Writer");
            var repository = new StubAuthorRepository(original);
            var manager = new CacheManager();
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)), manager);
            Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
            var item = CatalogItem(manager);
            var expiry = item.GetType().GetProperty("ExpiryTime");
            var fixedFuture = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            expiry.SetValue(item, fixedFuture);
            var changed = CacheAuthor(2, "Changed Writer");
            repository.Authors = new List<Author> { changed };

            for (var read = 0; read < 10; read++)
            {
                Assert.That(subject.GetCandidates(original.Name), Is.EqualTo(new[] { original }));
            }

            Assert.That(expiry.GetValue(CatalogItem(manager)), Is.EqualTo(fixedFuture));
            expiry.SetValue(CatalogItem(manager), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Multiple(() =>
            {
                Assert.That(subject.GetCandidates(changed.Name), Is.EqualTo(new[] { changed }));
                Assert.That(subject.GetCandidates(original.Name), Is.Empty);
                Assert.That(repository.AllCallCount, Is.EqualTo(2));
                Assert.That(PreparedCatalogCount(target), Is.EqualTo(2));
            });
        }

        [Test]
        public void bypass_catalog_read_should_leave_the_cached_authors_and_prepared_names_together()
        {
            using var logs = CreateLookupLogs(out var target);
            var author = new Author { Id = 1, Name = "Robert A Heinlein" };
            var repository = new StubAuthorRepository(author);
            var subject = CreateSubject(repository, logs.GetLogger(nameof(AuthorService)));
            var cached = subject.GetAllAuthors();
            Assert.That(subject.GetCandidates(author.Name), Is.EqualTo(new[] { author }));
            var fresh = new Author { Id = 2, Name = "Other Reader" };
            repository.Authors = new List<Author> { fresh };

            Assert.That(subject.GetAllAuthors(bypassCache: true), Is.EqualTo(new[] { fresh }));

            Assert.Multiple(() =>
            {
                Assert.That(subject.GetAllAuthors(), Is.SameAs(cached));
                Assert.That(subject.GetCandidates(author.Name), Is.EqualTo(new[] { author }));
                Assert.That(repository.AllCallCount, Is.EqualTo(2));
                Assert.That(PreparedCatalogCount(target), Is.EqualTo(1));
            });
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("...")]
        public void empty_name_evidence_should_not_load_the_author_catalog(string name)
        {
            var repository = new StubAuthorRepository(new Author { Id = 1, Name = "Test Writer", NameLastFirst = "Writer, Test" });
            var subject = CreateSubject(repository);

            Assert.That(subject.FindByName(name), Is.Null);
            Assert.That(subject.GetCandidates(name), Is.Empty);
            Assert.That(repository.AllCallCount, Is.Zero);
        }

        [Test]
        public void clean_key_lookup_should_not_override_a_conflicting_name()
        {
            var author = new Author { Id = 1, Name = "John A Smith" };
            var repository = new StubAuthorRepository(author);

            Assert.That(author.Name.CleanAuthorName(), Is.Not.EqualTo("John Or Smith".CleanAuthorName()));
            Assert.That(CreateSubject(repository).FindByName("John Or Smith"), Is.Null);
            Assert.That(repository.AllCallCount, Is.EqualTo(1));
        }

        [TestCase(1)]
        [TestCase(1000)]
        [TestCase(10000)]
        public void lookup_routes_should_reuse_one_catalog_without_per_author_reads(int count)
        {
            var author = new Author { Id = count, Name = "Robert Louis Stevenson", NameLastFirst = "Stevenson, Robert Louis" };
            var repository = new StubAuthorRepository(author)
            {
                Authors = Enumerable.Range(1, count - 1)
                    .Select(id => new Author { Id = id, Name = $"Unrelated Writer {id}", NameLastFirst = $"Writer {id}, Unrelated" })
                    .Append(author)
                    .ToList()
            };
            var subject = CreateSubject(repository);
            var stopwatch = Stopwatch.StartNew();

            Assert.That(subject.FindByName("Robert Stevenson"), Is.SameAs(author));
            Assert.That(subject.GetCandidates("Robert Stevenson"), Is.EqualTo(new[] { author }));
            Assert.That(repository.AllCallCount, Is.EqualTo(1));
            Assert.That(repository.GetCallCount, Is.Zero);
            TestContext.Progress.WriteLine($"Author lookup: catalog={count}, operations=2, repositoryAllCalls={repository.AllCallCount}, perAuthorReads={repository.GetCallCount}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}");
        }

        [Test]
        public void dry_ioc_should_resolve_lazy_media_file_deletion()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterInstance<IAuthorRepository>(new StubAuthorRepository(new Author { Id = 1189, Path = @"C:\authors\Pascale Lacelle".AsOsAgnostic() }));
            container.RegisterInstance<IEventAggregator>(new StubEventAggregator());
            container.RegisterInstance<IBuildAuthorPaths>(DispatchProxy.Create<IBuildAuthorPaths, NoOpProxy>());
            container.RegisterInstance<IRootFolderService>(DispatchProxy.Create<IRootFolderService, NoOpProxy>());
            container.RegisterInstance<IManageCommandQueue>(DispatchProxy.Create<IManageCommandQueue, NoOpProxy>());
            container.RegisterInstance<ICacheManager>(new CacheManager());
            container.RegisterInstance<IBookRepository>(DispatchProxy.Create<IBookRepository, NoOpProxy>());
            container.RegisterInstance<IMediaFileService>(DispatchProxy.Create<IMediaFileService, NoOpProxy>());
            container.RegisterInstance<IImportListExclusionService>(DispatchProxy.Create<IImportListExclusionService, NoOpProxy>());
            container.RegisterInstance<IDeleteMediaFiles>(new NoOpDeleteMediaFiles());
            container.RegisterInstance<IBookService>(DispatchProxy.Create<IBookService, NoOpProxy>());
            container.RegisterInstance<IMainDatabase>(DispatchProxy.Create<IMainDatabase, NoOpProxy>());
            container.Register<AuthorService>();

            var deletion = container.Resolve<Lazy<IDeleteMediaFiles>>();
            var service = container.Resolve<AuthorService>();

            Assert.That(deletion, Is.Not.Null);
            Assert.That(deletion.IsValueCreated, Is.False);
            var books = (Lazy<IBookService>)typeof(AuthorService).GetField("_bookService", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            Assert.That(books, Is.Not.Null);
            Assert.That(books.IsValueCreated, Is.False);
            Assert.That(books.Value, Is.Not.Null);
            Assert.That(service.AllAuthorPaths(), Has.Count.EqualTo(1));
            Assert.That(deletion.Value, Is.TypeOf<NoOpDeleteMediaFiles>());
            Assert.That(deletion.IsValueCreated, Is.True);
        }

        [Test]
        public void all_author_paths_should_include_each_distinct_media_path()
        {
            var lookup = new Author
            {
                Id = 1189,
                Path = @"C:\legacy\Pascale Lacelle".AsOsAgnostic(),
                AudiobookPath = @"C:\audio\Pascale Lacelle".AsOsAgnostic(),
                EbookPath = @"C:\ebooks\Pascale Lacelle".AsOsAgnostic()
            };

            var result = CreateSubject(new StubAuthorRepository(lookup)).AllAuthorPaths();

            Assert.That(result.Select(path => path.Value), Is.EqualTo(new[]
            {
                @"C:\audio\Pascale Lacelle".AsOsAgnostic(),
                @"C:\ebooks\Pascale Lacelle".AsOsAgnostic(),
                @"C:\legacy\Pascale Lacelle".AsOsAgnostic()
            }));
            Assert.That(result.All(path => path.Key == lookup.Id), Is.True);
        }

        [Test]
        public void find_by_name_should_return_author_with_quality_profiles_loaded()
        {
            var lookup = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 1,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" }),
                EbookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 1, Name = "Ebook" })
            };

            var repository = new StubAuthorRepository(lookup);
            var subject = CreateSubject(repository);

            var result = subject.FindByName("Pascale Lacelle");

            Assert.That(result, Is.SameAs(lookup));
            Assert.That(result.AudiobookQualityProfile.Value?.Id, Is.EqualTo(2));
            Assert.That(result.EbookQualityProfile.Value?.Id, Is.EqualTo(1));
            Assert.That(repository.GetCallCount, Is.EqualTo(0));
        }

        [Test]
        public void find_by_provider_id_should_return_author_with_quality_profiles_loaded()
        {
            var lookup = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                HardcoverAuthorId = "hc:123",
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" })
            };

            var repository = new StubAuthorRepository(lookup);
            var subject = CreateSubject(repository);

            var result = subject.FindByProviderId("hc", "123");

            Assert.That(result, Is.SameAs(lookup));
            Assert.That(result.AudiobookQualityProfile.Value?.Id, Is.EqualTo(2));
            Assert.That(repository.GetCallCount, Is.EqualTo(0));
        }

        [Test]
        public void find_by_name_should_prefer_cached_author_instance_when_available()
        {
            var lookup = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" })
            };

            var cached = new Author
            {
                Id = 1189,
                Name = "Pascale Lacelle",
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(new QualityProfile { Id = 2, Name = "Audio" })
            };

            var repository = new StubAuthorRepository(lookup, cached);
            var subject = CreateSubject(repository);

            Assert.That(subject.GetAuthor(1189), Is.SameAs(cached));

            var result = subject.FindByName("Pascale Lacelle");

            Assert.That(result, Is.SameAs(cached));
            Assert.That(repository.GetCallCount, Is.EqualTo(1));
        }
    }
}
