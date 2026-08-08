using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Aggregation;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class BookImportServiceOutcomeFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            private int _nextId = 1000;
            private readonly Dictionary<string, BookFile> _stored = new(StringComparer.OrdinalIgnoreCase);

            public int AddManyCalls { get; private set; }
            public int UpdateCalls { get; private set; }
            public bool IgnoreUpdates { get; set; }

            public void Seed(BookFile file)
            {
                _stored[file.Path] = Clone(file);
                _nextId = Math.Max(_nextId, file.Id);
            }

            public BookFile Stored(string path)
            {
                return _stored.TryGetValue(path, out var file) ? Clone(file) : null;
            }

            public BookFile Add(BookFile bookFile)
            {
                bookFile.Id = ++_nextId;
                _stored[bookFile.Path] = Clone(bookFile);
                return bookFile;
            }

            public void AddMany(List<BookFile> bookFiles)
            {
                AddManyCalls++;
                foreach (var file in bookFiles)
                {
                    file.Id = ++_nextId;
                    _stored[file.Path] = Clone(file);
                }
            }

            public void Update(BookFile bookFile)
            {
                UpdateCalls++;
                if (!IgnoreUpdates)
                {
                    _stored[bookFile.Path] = Clone(bookFile);
                }
            }

            public void Update(List<BookFile> bookFiles)
            {
                foreach (var file in bookFiles ?? new List<BookFile>())
                {
                    Update(file);
                }
            }

            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthor(int authorId) => new();
            public List<BookFile> GetFilesByBook(int bookId) => new();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => new();
            public List<BookFile> GetFilesByEdition(int editionId) => new();
            public List<BookFile> GetUnmappedFiles() => new();
            public BookFile Get(int id) => _stored.Values.Where(file => file.Id == id).Select(Clone).FirstOrDefault();
            public List<BookFile> Get(IEnumerable<int> ids) => new();
            public List<BookFile> GetFilesWithBasePath(string path) => new();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => new();
            public List<BookFile> GetFileWithPath(List<string> path) => path?.Select(GetFileWithPath).Where(file => file != null).ToList() ?? new List<BookFile>();
            public BookFile GetFileWithPath(string path) => Stored(path);
            public void UpdateMediaInfo(List<BookFile> bookFiles) { }
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => new();

            private static BookFile Clone(BookFile file)
            {
                if (file == null)
                {
                    return null;
                }

                return new BookFile
                {
                    Id = file.Id,
                    Path = file.Path,
                    EditionId = file.EditionId,
                    Edition = file.Edition,
                    Part = file.Part,
                    PartCount = file.PartCount,
                    Size = file.Size,
                    Modified = file.Modified,
                    DateAdded = file.DateAdded,
                    Quality = file.Quality,
                    MediaInfo = file.MediaInfo,
                    MediaType = file.MediaType,
                    AllTags = file.AllTags,
                    DurationSeconds = file.DurationSeconds,
                    CalibreId = file.CalibreId,
                    Author = file.Author,
                    LastMatchAttempt = file.LastMatchAttempt,
                    MatchDetails = file.MatchDetails,
                    MatchProvenance = file.MatchProvenance
                };
            }
        }

        private sealed class StubMediaInfoExtractor : IMediaInfoExtractor
        {
            public int ExtractMediaInfoCalls { get; private set; }
            public int GetDurationCalls { get; private set; }

            public MediaInfoModel ExtractMediaInfo(string filePath)
            {
                ExtractMediaInfoCalls++;
                return new MediaInfoModel();
            }

            public TimeSpan GetDuration(string filePath)
            {
                GetDurationCalls++;
                return TimeSpan.Zero;
            }

            public bool IsAudiobookFile(string filePath, MediaInfoModel mediaInfo = null) => false;
        }

        private sealed class CapturingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent => Events.Add(@event);
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book?.Id == (int)args[0] ? Book : null;
                }

                throw new NotImplementedException($"Unexpected call to IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public List<Edition> Editions { get; set; } = new();
            public List<Edition> MonitoredSelections { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return Editions.FirstOrDefault(edition => edition.Id == (int)args[0]);
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook))
                {
                    return Editions.Where(edition => edition.BookId == (int)args[0]).ToList();
                }

                if (targetMethod?.Name == nameof(IEditionService.SetMonitored))
                {
                    var selected = (Edition)args[0];
                    MonitoredSelections.Add(selected);
                    foreach (var edition in Editions.Where(edition => edition.BookId == selected.BookId))
                    {
                        edition.Monitored = edition.Id == selected.Id;
                        if (edition.Id != selected.Id)
                        {
                            edition.ManualAdd = false;
                        }
                    }

                    return Editions.Where(edition => edition.BookId == selected.BookId).ToList();
                }

                throw new NotImplementedException($"Unexpected call to IEditionService.{targetMethod?.Name}");
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public Dictionary<string, (long Size, DateTime Modified)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int GetFileSizeCalls { get; private set; }
            public int FileGetLastWriteCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var path = args?.Length > 0 ? args[0] as string : null;
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    return path != null && Files.ContainsKey(path);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetFileSize))
                {
                    GetFileSizeCalls++;
                    return Files[path].Size;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.FileGetLastWrite))
                {
                    FileGetLastWriteCalls++;
                    return Files[path].Modified;
                }

                throw new NotImplementedException($"Unexpected call to IDiskProvider.{targetMethod?.Name}");
            }
        }

        private sealed class Context
        {
            public BookImportService Sut { get; init; }
            public StubMediaFileService MediaFiles { get; init; }
            public DiskProviderProxy Disk { get; init; }
            public EditionServiceProxy Editions { get; init; }
            public Book Book { get; init; }
            public Edition TargetEdition { get; init; }
            public CapturingEventAggregator Events { get; init; }
            public StubMediaInfoExtractor MediaInfo { get; init; }
        }

        private static T Proxy<T>() where T : class => DispatchProxy.Create<T, ThrowingProxy<T>>();

        private static Context CreateContext()
        {
            var author = new Author { Id = 1, Name = "Author", Path = "/books/Author", HardcoverAuthorId = "101" };
            var book = new Book
            {
                Id = 10,
                AuthorId = author.Id,
                Author = author,
                Title = "Book",
                CleanTitle = "book",
                MediaType = BookMediaType.Ebook,
                HardcoverBookId = "201"
            };
            var edition = new Edition { Id = 20, BookId = book.Id, Book = book, Title = book.Title, Monitored = true, IsEbook = true, HardcoverEditionId = "301" };

            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Book = book;

            var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            var editionProxy = (EditionServiceProxy)(object)editionService;
            editionProxy.Editions.Add(edition);

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            var mediaFiles = new StubMediaFileService();
            var events = new CapturingEventAggregator();
            var mediaInfo = new StubMediaInfoExtractor();

            var sut = new BookImportService(
                bookService,
                editionService,
                mediaFiles,
                Proxy<IUpgradeMediaFiles>(),
                Proxy<IAugmentingService>(),
                diskProvider,
                mediaInfo,
                events,
                Proxy<IConfigService>(),
                Proxy<IAuthorService>(),
                LogManager.GetLogger("BookImportServiceOutcomeFixture"));

            return new Context
            {
                Sut = sut,
                MediaFiles = mediaFiles,
                Disk = diskProxy,
                Editions = editionProxy,
                Book = book,
                TargetEdition = edition,
                Events = events,
                MediaInfo = mediaInfo
            };
        }

        [Test]
        public async System.Threading.Tasks.Task track_in_place_returns_one_result_per_path_when_part_of_a_batch_disappears()
        {
            var context = CreateContext();
            var present = @"C:\books\Author\Book\Disc 1.epub".AsOsAgnostic();
            var missing = @"C:\books\Author\Book\Disc 2.epub".AsOsAgnostic();
            context.Disk.Files[present] = (100, DateTime.UtcNow);

            var results = await context.Sut.ImportFilesAsync(
                new List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)>
                {
                    (present, context.TargetEdition.Id, new Dictionary<string, List<string>>(), null),
                    (missing, context.TargetEdition.Id, new Dictionary<string, List<string>>(), null)
                },
                context.Book.Id);

            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.Single(result => result.Path == present).Outcome, Is.EqualTo(ImportOutcome.Imported));
            Assert.That(results.Single(result => result.Path == missing).Outcome, Is.EqualTo(ImportOutcome.Failed));
            Assert.That(results.Single(result => result.Path == missing).ReasonCode, Is.EqualTo("FILE_MISSING_AT_APPLY"));
            Assert.That(context.MediaFiles.Stored(present)?.EditionId, Is.EqualTo(context.TargetEdition.Id));
            Assert.That(context.MediaFiles.Stored(missing), Is.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task exact_identity_refreshes_changed_metadata_without_counting_a_new_import()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            var dateAdded = DateTime.UtcNow.AddYears(-1);
            var oldModified = DateTime.UtcNow.AddDays(-1);
            var observedModified = DateTime.UtcNow;
            context.Disk.Files[path] = (999, observedModified);
            context.MediaFiles.Seed(new BookFile
            {
                Id = 50,
                Path = path,
                EditionId = context.TargetEdition.Id,
                Part = 1,
                PartCount = 1,
                Size = 10,
                Modified = oldModified,
                DateAdded = dateAdded,
                CalibreId = 77,
                MediaInfo = MediaDuration.CreateMediaInfo(10),
                AllTags = new Dictionary<string, List<string>> { ["TITLE"] = new() { "stale" } }
            });

            var results = await context.Sut.ImportFilesAsync(
                new List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)>
                {
                    (new DiscoveredFileWithMetadata
                    {
                        Path = path,
                        Size = 999,
                        Modified = observedModified,
                        Quality = new QualityModel { Quality = Quality.EPUB },
                        AllTags = new Dictionary<string, List<string>> { ["TITLE"] = new() { "fresh" } },
                        DurationSeconds = 123
                    }, context.TargetEdition.Id, null)
                },
                context.Book.Id);

            var result = results.Single();
            var stored = context.MediaFiles.Stored(path);
            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.AlreadyLinked));
            Assert.That(context.MediaFiles.UpdateCalls, Is.EqualTo(1));
            Assert.That(stored.Size, Is.EqualTo(999));
            Assert.That(stored.Modified, Is.EqualTo(observedModified));
            Assert.That(stored.AllTags["TITLE"], Is.EqualTo(new[] { "fresh" }));
            Assert.That(stored.DurationSeconds, Is.EqualTo(123));
            Assert.That(stored.MediaInfo.Duration, Is.EqualTo(TimeSpan.FromSeconds(123)));
            Assert.That(stored.DateAdded, Is.EqualTo(dateAdded));
            Assert.That(stored.CalibreId, Is.EqualTo(77));
            Assert.That(context.Disk.GetFileSizeCalls, Is.EqualTo(0));
            Assert.That(context.Disk.FileGetLastWriteCalls, Is.EqualTo(0));
            Assert.That(context.MediaInfo.GetDurationCalls, Is.EqualTo(0));
            Assert.That(context.MediaInfo.ExtractMediaInfoCalls, Is.EqualTo(0));
            Assert.That(context.Events.Events.OfType<BookFileUpdatedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public async System.Threading.Tasks.Task verified_apply_persists_decision_provenance_and_clears_failure_scratchpad()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            var provenance = new MatchProvenance
            {
                DecisionId = "decision-apply",
                Mode = "Balanced",
                Route = "global/embedded_tags",
                SupportingSignals = new List<MatchSignal>
                {
                    new MatchSignal { Type = "title", Scope = "book", Field = "TITLE", Observed = "Book", Expected = "Book" }
                }
            };
            context.Disk.Files[path] = (100, DateTime.UtcNow);
            context.MediaFiles.Seed(new BookFile
            {
                Id = 53,
                Path = path,
                EditionId = 0,
                Part = 1,
                LastMatchAttempt = DateTime.UtcNow,
                MatchDetails = "NO_MATCH"
            });

            var results = await context.Sut.ImportFilesAsync(
                new List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds, MatchProvenance Provenance)>
                {
                    (path, context.TargetEdition.Id, null, null, provenance)
                },
                context.Book.Id);

            var stored = context.MediaFiles.Stored(path);
            Assert.That(results.Single().Outcome, Is.EqualTo(ImportOutcome.Imported));
            Assert.That(stored.MatchDetails, Is.Null);
            Assert.That(stored.LastMatchAttempt, Is.Null);
            Assert.That(stored.MatchProvenance.DecisionId, Is.EqualTo("decision-apply"));
            Assert.That(stored.MatchProvenance.AuthorProviderIds, Does.Contain("hc:101"));
            Assert.That(stored.MatchProvenance.BookProviderIds, Does.Contain("hc:201"));
            Assert.That(stored.MatchProvenance.EditionProviderIds, Does.Contain("hc:301"));
            var relinkEvent = context.Events.Events.OfType<BookFileAddedEvent>().Single();
            Assert.That(relinkEvent.BookFile.Id, Is.EqualTo(stored.Id));
            Assert.That(relinkEvent.BookFile.Edition, Is.SameAs(context.TargetEdition));
            Assert.That(relinkEvent.BookFile.Edition.Book, Is.SameAs(context.Book));
            Assert.That(relinkEvent.BookFile.Author, Is.SameAs(context.Book.Author));
        }

        [Test]
        public async System.Threading.Tasks.Task singular_unmapped_relink_publishes_hydrated_added_event()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);
            context.MediaFiles.Seed(new BookFile { Id = 56, Path = path, EditionId = 0, Part = 1 });

            var result = await context.Sut.ImportExistingFileAsync(
                path,
                context.Book.Id,
                context.TargetEdition.Id,
                "Unknown",
                null);

            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.Imported));
            var relinkEvent = context.Events.Events.OfType<BookFileAddedEvent>().Single();
            Assert.That(relinkEvent.BookFile.Id, Is.EqualTo(56));
            Assert.That(relinkEvent.BookFile.Edition, Is.SameAs(context.TargetEdition));
            Assert.That(relinkEvent.BookFile.Edition.Book, Is.SameAs(context.Book));
            Assert.That(relinkEvent.BookFile.Author, Is.SameAs(context.Book.Author));
        }

        [Test]
        public async System.Threading.Tasks.Task unchanged_exact_identity_does_not_rewrite_metadata_or_provenance()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            var modified = DateTime.UtcNow;
            context.Disk.Files[path] = (999, modified);
            context.MediaFiles.Seed(new BookFile
            {
                Id = 54,
                Path = path,
                EditionId = context.TargetEdition.Id,
                Part = 1,
                PartCount = 1,
                Size = 999,
                Modified = modified,
                Quality = new QualityModel { Quality = Quality.EPUB },
                MediaType = "ebook",
                AllTags = new Dictionary<string, List<string>> { ["TITLE"] = new() { "stale" } },
                MatchProvenance = new MatchProvenance { DecisionId = "original-decision" }
            });

            var results = await context.Sut.ImportFilesAsync(
                new List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)>
                {
                    (new DiscoveredFileWithMetadata
                    {
                        Path = path,
                        Size = 999,
                        Modified = modified.AddMilliseconds(500),
                        AllTags = new Dictionary<string, List<string>> { ["TITLE"] = new() { "fresh" } },
                        DurationSeconds = 123
                    }, context.TargetEdition.Id,
                        new MatchProvenance { DecisionId = "decision-already", Mode = "Strict", Route = "author_scoped/embedded_tags" })
                },
                context.Book.Id);

            var stored = context.MediaFiles.Stored(path);
            Assert.That(results.Single().Outcome, Is.EqualTo(ImportOutcome.AlreadyLinked));
            Assert.That(context.MediaFiles.UpdateCalls, Is.EqualTo(0));
            Assert.That(stored.MatchProvenance.DecisionId, Is.EqualTo("original-decision"));
            Assert.That(stored.Size, Is.EqualTo(999));
            Assert.That(stored.AllTags["TITLE"], Is.EqualTo(new[] { "stale" }));
            Assert.That(context.Disk.GetFileSizeCalls, Is.EqualTo(0));
            Assert.That(context.Disk.FileGetLastWriteCalls, Is.EqualTo(0));
            Assert.That(context.MediaInfo.GetDurationCalls, Is.EqualTo(0));
            Assert.That(context.MediaInfo.ExtractMediaInfoCalls, Is.EqualTo(0));
            Assert.That(context.Events.Events, Is.Empty);
        }

        [Test]
        public async System.Threading.Tasks.Task changed_exact_identity_records_new_decision_and_clears_failure_scratchpad()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            var observedModified = DateTime.UtcNow;
            context.Disk.Files[path] = (999, observedModified);
            context.MediaFiles.Seed(new BookFile
            {
                Id = 55,
                Path = path,
                EditionId = context.TargetEdition.Id,
                Part = 1,
                PartCount = 1,
                Size = 10,
                Modified = observedModified.AddDays(-1),
                MatchDetails = "APPLY_FAILED:OLD",
                LastMatchAttempt = observedModified.AddDays(-1)
            });

            var results = await context.Sut.ImportFilesAsync(
                new List<(DiscoveredFileWithMetadata File, int? EditionId, MatchProvenance Provenance)>
                {
                    (new DiscoveredFileWithMetadata
                    {
                        Path = path,
                        Size = 999,
                        Modified = observedModified,
                        AllTags = new Dictionary<string, List<string>> { ["TITLE"] = new() { "fresh" } },
                        DurationSeconds = 123
                    }, context.TargetEdition.Id,
                        new MatchProvenance { DecisionId = "decision-refreshed", Mode = "Strict", Route = "author_scoped/embedded_tags" })
                },
                context.Book.Id);

            var stored = context.MediaFiles.Stored(path);
            Assert.That(results.Single().Outcome, Is.EqualTo(ImportOutcome.AlreadyLinked));
            Assert.That(stored.MatchProvenance.DecisionId, Is.EqualTo("decision-refreshed"));
            Assert.That(stored.MatchDetails, Is.Null);
            Assert.That(stored.LastMatchAttempt, Is.Null);
            Assert.That(context.MediaFiles.UpdateCalls, Is.EqualTo(1));
        }

        [Test]
        public async System.Threading.Tasks.Task singular_apply_rejects_a_path_already_linked_to_another_edition()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            var otherEdition = new Edition { Id = 21, BookId = context.Book.Id, Book = context.Book, Title = context.Book.Title, IsEbook = true };
            context.Editions.Editions.Add(otherEdition);
            context.Disk.Files[path] = (100, DateTime.UtcNow);
            context.MediaFiles.Seed(new BookFile { Id = 51, Path = path, EditionId = otherEdition.Id, Part = 1 });
            var rejectedProvenance = new MatchProvenance { DecisionId = "must-not-persist-unmapped" };

            var result = await context.Sut.ImportExistingFileAsync(
                path,
                context.Book.Id,
                context.TargetEdition.Id,
                "Unknown",
                null,
                null,
                rejectedProvenance);

            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.Unmapped));
            Assert.That(result.ReasonCode, Is.EqualTo("PATH_LINKED_TO_DIFFERENT_EDITION"));
            Assert.That(context.MediaFiles.UpdateCalls, Is.EqualTo(0));
            Assert.That(context.MediaFiles.Stored(path).EditionId, Is.EqualTo(otherEdition.Id));
            Assert.That(context.MediaFiles.Stored(path).MatchProvenance, Is.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task applied_update_is_failed_when_the_persisted_postcondition_does_not_hold()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);
            context.MediaFiles.Seed(new BookFile { Id = 52, Path = path, EditionId = 0, Part = 1 });
            context.MediaFiles.IgnoreUpdates = true;
            var failedProvenance = new MatchProvenance { DecisionId = "must-not-persist-failed" };

            var result = await context.Sut.ImportExistingFileAsync(
                path,
                context.Book.Id,
                context.TargetEdition.Id,
                "Unknown",
                null,
                null,
                failedProvenance);

            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.Failed));
            Assert.That(result.ReasonCode, Is.EqualTo("APPLY_POSTCONDITION_EDITION_MISMATCH"));
            Assert.That(context.Editions.MonitoredSelections, Is.Empty);
            Assert.That(context.Events.Events, Is.Empty);
            Assert.That(context.MediaFiles.Stored(path).MatchProvenance, Is.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task explicit_unknown_edition_does_not_fall_back_to_the_default_edition()
        {
            var context = CreateContext();
            var path = "/books/Author/Book/Book.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);

            var results = await context.Sut.ImportFilesAsync(
                new List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)>
                {
                    (path, 999, null, null)
                },
                context.Book.Id);

            Assert.That(results.Single().Outcome, Is.EqualTo(ImportOutcome.Unmapped));
            Assert.That(results.Single().ReasonCode, Is.EqualTo("EDITION_NOT_FOUND_FOR_BOOK_AT_APPLY"));
            Assert.That(context.MediaFiles.Stored(path), Is.Null);
        }

        [Test]
        public async System.Threading.Tasks.Task singular_new_file_refuses_to_replace_a_different_pinned_edition()
        {
            var context = CreateContext();
            context.Book.AnyEditionOk = false;
            context.TargetEdition.ManualAdd = true;
            var matchedEdition = new Edition { Id = 21, BookId = context.Book.Id, Book = context.Book, Title = "Book - Other Edition", IsEbook = true };
            context.Editions.Editions.Add(matchedEdition);
            var path = "/books/Author/Book/Other.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);

            var result = await context.Sut.ImportExistingFileAsync(path, context.Book.Id, matchedEdition.Id, "Unknown", null);

            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.Unmapped));
            Assert.That(result.ReasonCode, Is.EqualTo("PINNED_EDITION_DESTINATION_CONFLICT"));
            Assert.That(context.MediaFiles.Stored(path), Is.Null);
            Assert.That(context.Editions.MonitoredSelections, Is.Empty);
            Assert.That(context.TargetEdition.Monitored, Is.True);
            Assert.That(context.TargetEdition.ManualAdd, Is.True);
        }

        [Test]
        public async System.Threading.Tasks.Task singular_unmapped_relink_refuses_to_replace_a_different_pinned_edition()
        {
            var context = CreateContext();
            context.Book.AnyEditionOk = false;
            context.TargetEdition.ManualAdd = true;
            var matchedEdition = new Edition { Id = 21, BookId = context.Book.Id, Book = context.Book, Title = "Book - Other Edition", IsEbook = true };
            context.Editions.Editions.Add(matchedEdition);
            var path = "/books/Author/Book/Other.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);
            context.MediaFiles.Seed(new BookFile { Id = 60, Path = path, EditionId = 0, Part = 1 });

            var result = await context.Sut.ImportExistingFileAsync(path, context.Book.Id, matchedEdition.Id, "Unknown", null);

            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.Unmapped));
            Assert.That(result.ReasonCode, Is.EqualTo("PINNED_EDITION_DESTINATION_CONFLICT"));
            Assert.That(context.MediaFiles.Stored(path).EditionId, Is.EqualTo(0));
            Assert.That(context.MediaFiles.UpdateCalls, Is.EqualTo(0));
            Assert.That(context.Editions.MonitoredSelections, Is.Empty);
            Assert.That(context.TargetEdition.ManualAdd, Is.True);
        }

        [Test]
        public async System.Threading.Tasks.Task batch_track_in_place_refuses_to_replace_a_different_pinned_edition()
        {
            var context = CreateContext();
            context.Book.AnyEditionOk = false;
            context.TargetEdition.ManualAdd = true;
            var matchedEdition = new Edition { Id = 21, BookId = context.Book.Id, Book = context.Book, Title = "Book - Other Edition", IsEbook = true };
            context.Editions.Editions.Add(matchedEdition);
            var path = "/books/Author/Book/Other.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);

            var results = await context.Sut.ImportFilesAsync(
                new List<(string Path, int? EditionId, Dictionary<string, List<string>> Tags, int? DurationSeconds)>
                {
                    (path, matchedEdition.Id, null, null)
                },
                context.Book.Id);

            Assert.That(results.Single().Outcome, Is.EqualTo(ImportOutcome.Unmapped));
            Assert.That(results.Single().ReasonCode, Is.EqualTo("PINNED_EDITION_DESTINATION_CONFLICT"));
            Assert.That(context.MediaFiles.AddManyCalls, Is.EqualTo(0));
            Assert.That(context.Editions.MonitoredSelections, Is.Empty);
            Assert.That(context.TargetEdition.ManualAdd, Is.True);
        }

        [Test]
        public async System.Threading.Tasks.Task legacy_import_refuses_to_replace_a_different_pinned_edition_before_disk_mutation()
        {
            var context = CreateContext();
            context.Book.AnyEditionOk = false;
            context.TargetEdition.ManualAdd = true;
            var matchedEdition = new Edition { Id = 21, BookId = context.Book.Id, Book = context.Book, Title = "Book - Other Edition", IsEbook = true };
            context.Editions.Editions.Add(matchedEdition);
            var path = "/books/Author/Book/Other.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);

            await context.Sut.ImportFileAsync(path, context.Book.Id, matchedEdition.Id, "Unknown", null);

            Assert.That(context.MediaFiles.Stored(path), Is.Null);
            Assert.That(context.Editions.MonitoredSelections, Is.Empty);
            Assert.That(context.Disk.GetFileSizeCalls, Is.EqualTo(0));
            Assert.That(context.TargetEdition.ManualAdd, Is.True);
        }

        [Test]
        public async System.Threading.Tasks.Task unpinned_book_still_switches_to_the_edition_that_received_the_file()
        {
            var context = CreateContext();
            context.Book.AnyEditionOk = true;
            var matchedEdition = new Edition { Id = 21, BookId = context.Book.Id, Book = context.Book, Title = "Book - Other Edition", IsEbook = true };
            context.Editions.Editions.Add(matchedEdition);
            var path = "/books/Author/Book/Other.epub";
            context.Disk.Files[path] = (100, DateTime.UtcNow);

            var result = await context.Sut.ImportExistingFileAsync(path, context.Book.Id, matchedEdition.Id, "Unknown", null);

            Assert.That(result.Outcome, Is.EqualTo(ImportOutcome.Imported));
            Assert.That(context.MediaFiles.Stored(path)?.EditionId, Is.EqualTo(matchedEdition.Id));
            Assert.That(context.Editions.MonitoredSelections.Select(edition => edition.Id), Is.EqualTo(new[] { matchedEdition.Id }));
            Assert.That(context.TargetEdition.Monitored, Is.False);
            Assert.That(matchedEdition.Monitored, Is.True);
        }
    }
}
