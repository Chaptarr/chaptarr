using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using IMainDatabase = NzbDrone.Core.Datastore.IMainDatabase;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void included_boundary_cleans_failed_sources_once_and_preserves_imported_and_pending_results_when_cleanup_fails(bool cleanupThrows)
        {
            RunIncludedBoundary(cleanupThrows, false);
        }

        [Test]
        public void partly_refused_bonus_ebook_reports_only_the_refused_path_as_skipped()
        {
            RunIncludedBoundary(false, true);
        }

        [Test]
        public void unexpected_bonus_import_exception_reports_skips_and_cleans_uncommitted_owned_workspaces()
        {
            RunIncludedBoundary(false, false, true);
        }

        [Test]
        public void real_bonus_importer_preserves_committed_recording_and_cleans_only_uncommitted_sources_after_commit_failure()
        {
            RunIncludedBoundary(false, false, realBatch: true);
        }

        [Test]
        public void real_bonus_importer_keeps_committed_success_when_later_import_observer_throws_without_discard()
        {
            RunIncludedBoundary(false, false, realBatch: true, postCommitThrows: true);
        }

        [Test]
        public void bonus_exception_cleans_real_uncommitted_job_but_keeps_committed_attempt(
            [Values(false, true)] bool postgres, [Values("none", "partial", "all")] string committed)
        {
            DualDatabaseFixture.Run(postgres, database => RunIncludedBoundary(false, false,
                importThrows: committed == "none", realBatch: committed != "none", postCommitThrows: committed == "all", database: database));
        }

        private void RunIncludedBoundary(bool cleanupThrows, bool partialRefusal, bool importThrows = false, bool realBatch = false, bool postCommitThrows = false, IMainDatabase database = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "bonus-boundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            ConversionJobService realJobs = null;
            try
            {
                var paths = Enumerable.Range(0, 4).Select(index => Path.Combine(root, "bonus-" + index + (realBatch ? ".m4b" : ".epub"))).ToArray();
                foreach (var path in paths) File.WriteAllText(path, "fixture source");
                var fs = new FileSystem();
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
                {
                    nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                    nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                    nameof(IDiskProvider.GetFileSize) => new FileInfo((string)args[0]).Length,
                    nameof(IDiskProvider.FileGetLastWrite) => File.GetLastWriteTimeUtc((string)args[0]),
                    _ => throw new InvalidOperationException("Unexpected disk method " + method.Name)
                });
                var author = new Author { Id = 1, Name = "Fixture Author", EbookMonitored = true, EbookRootFolderPath = root,
                    EbookQualityProfileId = 2, EbookQualityProfile = new QualityProfile { Id = 2, ProfileType = ProfileType.Ebook,
                        Items = new() { new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true } } } };
                var book = new Book { Id = 2, AuthorId = author.Id, Author = author, Title = "Fixture Book", GoodreadsWorkId = "gr:123",
                    MediaType = BookMediaType.Ebook, EbookMonitored = true, AnyEditionOk = true, BookFiles = new List<BookFile>() };
                var edition = new Edition { Id = 3, BookId = book.Id, Book = book, Monitored = true, Title = book.Title };
                book.Editions = new List<Edition> { edition };
                if (realBatch)
                {
                    book.MediaType = BookMediaType.Audiobook; book.AudiobookMonitored = true; book.CleanTitle = "fixturebook";
                    author.AudiobookMonitored = true; author.AudiobookRootFolderPath = root; author.AudiobookPath = root;
                    author.AudiobookQualityProfileId = 8;
                    author.AudiobookQualityProfile = new QualityProfile { Id = 8, ProfileType = ProfileType.Audiobook,
                        Items = new() { new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true } } };
                    book.Editions.Add(new Edition { Id = 4, BookId = book.Id, Book = book, Monitored = true, Title = "Fixture Book Other Recording" });
                }
                var matcher = InterfaceProxy.Proxy<IFileMatchingService>((_, args) => Task.FromResult(new FileMatchResult
                {
                    MatchedFiles = ((DiscoveredFileWithMetadata[])args[0]).Select(file => new FileMatch
                    { File = file, AuthorId = partialRefusal && file.Path == paths[2] ? author.Id + 1 : author.Id, BookId = book.Id,
                        EditionId = realBatch && file.Path != paths[0] ? 4 : edition.Id }).ToArray(),
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                }));
                var tags = InterfaceProxy.Proxy<IMetadataTagService>((_, _) => (new Dictionary<string, List<string>>(), (int?)null));
                ImportResult imported = null;
                ImportResult pending = null;
                var importer = InterfaceProxy.Proxy<IImportApprovedBooks>((_, args) =>
                {
                    if (importThrows) throw new InvalidOperationException("Bonus database unavailable");
                    var decisions = (List<ImportDecision<LocalBook>>)args[0];
                    if (partialRefusal)
                        return decisions.Select(decision => new ImportResult(decision, ImportResultType.Imported)).ToList();
                    imported = new ImportResult(decisions.Single(decision => decision.Item.Path == paths[0]), ImportResultType.Imported);
                    pending = new ImportResult(decisions.Single(decision => decision.Item.Path == paths[1]), ImportResultType.Pending);
                    var failed = decisions.Single(decision => decision.Item.Path == paths[2]);
                    failed.Item.GeneratedConversionSourcePaths = new() { paths[2], paths[0], paths[1] };
                    return new List<ImportResult> { imported, pending, new(failed, "copy failure"),
                        new(decisions.Single(decision => decision.Item.Path == paths[3]), "verification failure") };
                });
                var cleanupCalls = 0;
                var committed = new StubMediaFileService();
                var published = new List<object>();
                if (realBatch)
                {
                    var writer = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (!postCommitThrows && (method.Name == nameof(IMediaFileService.AddMany) || method.Name == nameof(IMediaFileService.ReplaceMany)) &&
                            ((List<BookFile>)args[0]).Any(file => file.EditionId == 4))
                            throw new InvalidOperationException("Second recording database write failed");
                        return method.Invoke(committed, args);
                    });
                    importer = new ImportApprovedBooks(writer, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        Proxy<IAuthorService>(), CreateBookService(new List<Book> { book }).Service, CreateEditionService(book.Editions),
                        new StubRecycleBinProvider(), Proxy<IExtraService>(),
                        new StubMoveBookFiles { TransferFilesOnDisk = true, DestinationForFile = local => Path.Combine(root, "library-" + Path.GetFileName(local.Path)) },
                        InterfaceProxy.Proxy<IHistoryService>((method, _) => method.Name == nameof(IHistoryService.FindByDownloadId) ? new List<EntityHistory>() : throw new NotImplementedException(method.Name)),
                        Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                        InterfaceProxy.Proxy<IEventAggregator>((_, args) =>
                        {
                            published.Add(args[0]);
                            if (postCommitThrows && args[0] is TrackImportedEvent importedEvent && importedEvent.ImportedBook.EditionId == 4)
                                throw new InvalidOperationException("Imported event observer unavailable");
                            return null;
                        }),
                        Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(),
                        Proxy<IM4bConversionService>(), LogManager.GetCurrentClassLogger());
                }
                string[] cleanupSources = null;
                ConversionJobRequest realRequest = null;
                if (database != null)
                {
                    realJobs = CreateConversionJobs(new SuccessfulM4bConversionService(), database: database);
                    var workRoot = Path.Combine(root, ".chaptarr-conversions", "bonus-boundary");
                    var workFolder = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
                    realRequest = new ConversionJobRequest { DownloadId = "bonus-boundary", BookId = book.Id, EditionId = edition.Id,
                        WorkRoot = workRoot, WorkFolder = workFolder, OutputPath = Path.Combine(workFolder, "converted.m4b"),
                        TargetQualityId = Quality.M4B.Id, TargetQualityName = Quality.M4B.Name,
                        Sources = paths.Select(path => new ConversionArtifactSource { Path = path, Size = new FileInfo(path).Length,
                            ModifiedUtcTicks = File.GetLastWriteTimeUtc(path).Ticks }).ToList() };
                    realJobs.Enqueue(realRequest);
                    Assert.That(ConversionJobServiceFixture.DispatchWorkers(realJobs).Wait(TimeSpan.FromSeconds(15)), Is.True);
                    Assert.That(realJobs.Get(realRequest.DownloadId).Status, Is.EqualTo(ConversionJobStatus.ReadyToImport));
                    Assert.That(File.Exists(realRequest.OutputPath), Is.True);
                    // A dependency failure can leave a successful receipt on a Failed row; no acceptance bypass is used by this boundary test.
                    realJobs.Fail(realRequest.DownloadId, "Import dependency unavailable");
                }
                var jobs = InterfaceProxy.Proxy<IConversionJobService>((method, args) =>
                {
                    if (method.Name == nameof(IConversionJobService.Get)) return realJobs?.Get((string)args[0]);
                    if (method.Name == nameof(IConversionJobService.IsRunning)) return realJobs?.IsRunning((string)args[0]) == true;
                    Assert.That(method.Name, Is.EqualTo(nameof(IConversionJobService.RetainCompletedOutput)));
                    cleanupCalls++;
                    cleanupSources = ((IReadOnlyCollection<string>)args[1]).ToArray();
                    Assert.That(args[3], Is.True);
                    if (realJobs != null) return realJobs.RetainCompletedOutput((string)args[0], (IReadOnlyCollection<string>)args[1], (string)args[2], discardOutput: true);
                    if (cleanupThrows) throw new InvalidOperationException("Conversion database unavailable");
                    return new ConversionRetentionResult(ConversionRetentionStatus.None, (string)args[2]);
                });
                var service = new DownloadedBooksImportService(disk, null, matcher, tags, importer,
                    InterfaceProxy.Proxy<IBookService>((method, _) => { Assert.That(method.Name, Is.EqualTo("GetBooksForLookup")); return new List<Book> { book }; }),
                    InterfaceProxy.Proxy<IAuthorService>((method, _) => { Assert.That(method.Name, Is.EqualTo("GetAuthor")); return author; }),
                    null, null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(),
                    ImportLifecycleStubs.EmptyQueue(), jobs);
                var item = new DownloadClientItem { DownloadId = "bonus-boundary", FilePaths = paths.ToList() };
                var remote = new RemoteBook { Author = author, Books = new() { new Book { Id = 4, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:123" } },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) } };
                if (realBatch)
                {
                    remote.Books[0].MediaType = BookMediaType.Ebook;
                    remote.ParsedBookInfo.Quality = new QualityModel(Quality.EPUB);
                }
                var results = service.ProcessIncludedMedia(item, remote, new List<EntityHistory>());
                if (realBatch)
                {
                    Assert.That(cleanupCalls, Is.EqualTo(postCommitThrows ? 0 : 1));
                    Assert.That(results, Has.Count.EqualTo(4));
                    if (postCommitThrows)
                    {
                        Assert.That(results.All(result => result.Result == ImportResultType.Imported), Is.True);
                        Assert.That(committed.FilesByBook, Has.Count.EqualTo(4));
                        Assert.That(committed.FilesByBook.All(file => File.Exists(file.Path)), Is.True);
                        Assert.That(paths.All(File.Exists), Is.True);
                        if (realJobs != null)
                        {
                            Assert.That(realJobs.Get(realRequest.DownloadId), Is.Not.Null);
                            Assert.That(Directory.Exists(realRequest.WorkFolder), Is.True);
                            Assert.That(File.Exists(realRequest.OutputPath), Is.True);
                        }
                        return;
                    }
                    Assert.That(results.Single(result => result.ImportDecision.Item.Path == paths[0]).Result, Is.EqualTo(ImportResultType.Imported));
                    Assert.That(results.Where(result => result.ImportDecision.Item.Path != paths[0]).All(result => result.IsIncludedMediaSkip), Is.True);
                    Assert.That(committed.FilesByBook, Has.Count.EqualTo(1));
                    Assert.That(File.Exists(committed.FilesByBook.Single().Path), Is.True);
                    Assert.That(published.OfType<TrackImportedEvent>().Count(), Is.EqualTo(1));
                    Assert.That(paths.All(File.Exists), Is.True);
                    if (realJobs != null)
                    {
                        Assert.That(cleanupSources, Is.EquivalentTo(paths.Skip(1)));
                        Assert.That(realJobs.Get(realRequest.DownloadId), Is.Not.Null, "The all-sources guard protects a job that includes a committed source");
                        Assert.That(Directory.Exists(realRequest.WorkFolder), Is.True);
                        Assert.That(File.Exists(realRequest.OutputPath), Is.True);
                    }
                    return;
                }
                if (importThrows)
                {
                    Assert.That(cleanupCalls, Is.EqualTo(1));
                    Assert.That(cleanupSources, Is.EquivalentTo(paths));
                    Assert.That(results, Has.Count.EqualTo(paths.Length));
                    Assert.That(results.All(result => result.IsIncludedMediaSkip), Is.True);
                    Assert.That(results.SelectMany(result => result.Errors), Has.Some.Contains("Bonus database unavailable"));
                    Assert.That(paths.All(File.Exists), Is.True);
                    if (realJobs != null)
                    {
                        Assert.That(realJobs.Get(realRequest.DownloadId), Is.Null);
                        Assert.That(Directory.Exists(realRequest.WorkFolder), Is.False);
                        Assert.That(File.Exists(realRequest.OutputPath), Is.False);
                    }
                    return;
                }
                Assert.That(cleanupCalls, Is.EqualTo(1));
                if (partialRefusal)
                {
                    Assert.That(cleanupSources, Is.EqualTo(new[] { paths[2] }));
                    Assert.That(results.Where(result => result.Result == ImportResultType.Imported).Select(result => result.ImportDecision.Item.Path),
                        Is.EquivalentTo(paths.Where(path => path != paths[2])));
                    Assert.That(results.Where(result => result.IsIncludedMediaSkip).Select(result => result.ImportDecision.Item.Path), Is.EqualTo(new[] { paths[2] }));
                    return;
                }
                Assert.That(cleanupSources, Is.EquivalentTo(paths.Skip(2)));
                Assert.That(results, Has.Count.EqualTo(4));
                Assert.That(results[0], Is.SameAs(imported));
                Assert.That(results[1], Is.SameAs(pending));
                Assert.That(results.Skip(2).All(result => result.IsIncludedMediaSkip), Is.True);
                if (cleanupThrows) Assert.That(results.Skip(2).SelectMany(result => result.Errors), Has.Some.Contains("Conversion database unavailable"));
                Assert.That(paths.All(File.Exists), Is.True);
            }
            finally
            {
                ConversionJobServiceFixture.ShutdownAndWait(realJobs);
                Directory.Delete(root, true);
            }
        }
    }
}
