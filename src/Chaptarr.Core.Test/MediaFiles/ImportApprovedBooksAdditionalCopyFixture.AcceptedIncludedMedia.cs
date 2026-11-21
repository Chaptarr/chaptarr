using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(ConversionJobStatus.Queued, true)]
        [TestCase(ConversionJobStatus.Converting, true)]
        [TestCase(ConversionJobStatus.ReadyToImport, true)]
        [TestCase(ConversionJobStatus.Queued, false)]
        [TestCase(ConversionJobStatus.Converting, false)]
        [TestCase(ConversionJobStatus.ReadyToImport, false)]
        public void bonus_requires_saved_edition_before_resuming_despite_paused_format_and_missing_profile(ConversionJobStatus status, bool knownEdition)
        {
            var root = Path.Combine(Path.GetTempPath(), "bonus-accepted-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var paths = Enumerable.Range(0, 4).Select(index => Path.Combine(root, "bonus-" + index + ".mp3")).ToArray();
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
                var author = new Author { Id = 1, Name = "Fixture Author", AudiobookMonitored = false, AudiobookRootFolderPath = null,
                    AudiobookQualityProfileId = 2, AudiobookQualityProfile = new QualityProfile { Id = 2, ProfileType = ProfileType.Audiobook,
                        Items = new() { new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true } } } };
                var book = new Book { Id = 2, AuthorId = author.Id, Author = author, Title = "Fixture Book", GoodreadsWorkId = "gr:123",
                    MediaType = BookMediaType.Audiobook, AudiobookMonitored = true, AnyEditionOk = true, BookFiles = new List<BookFile>() };
                var edition = new Edition { Id = 3, BookId = book.Id, Book = book, Monitored = true, Title = book.Title };
                book.Editions = new List<Edition> { edition };
                var matcher = InterfaceProxy.Proxy<IFileMatchingService>((_, _) => throw new AssertionException("A saved accepted edition must not rematch"));
                var tags = InterfaceProxy.Proxy<IMetadataTagService>((_, _) => (new Dictionary<string, List<string>>(), (int?)null));
                book.AudiobookMonitored = false;
                author.AudiobookQualityProfileId = null;
                author.AudiobookQualityProfile = null;
                var importCalls = 0;
                var importer = InterfaceProxy.Proxy<IImportApprovedBooks>((_, args) =>
                {
                    importCalls++;
                    var decisions = (List<ImportDecision<LocalBook>>)args[0];
                    Assert.That(decisions, Has.Count.EqualTo(1));
                    Assert.That(decisions[0].Item.Path, Is.EqualTo(paths[0]));
                    Assert.That(decisions[0].Item.Edition.Id, Is.EqualTo(edition.Id));
                    Assert.That(decisions[0].Item.IsIncludedMedia, Is.True);
                    return new List<ImportResult> { new(decisions[0], status == ConversionJobStatus.ReadyToImport ? ImportResultType.Imported : ImportResultType.Pending) };
                });
                var request = new ConversionJobRequest { DownloadId = "bonus-boundary", BookId = book.Id, EditionId = knownEdition ? edition.Id : 0,
                    Sources = new() { new ConversionArtifactSource { Path = paths[0], Size = new FileInfo(paths[0]).Length,
                        ModifiedUtcTicks = File.GetLastWriteTimeUtc(paths[0]).Ticks } } };
                var acceptedJob = new ConversionJob { DownloadId = request.DownloadId, Status = status, RequestJson = JsonSerializer.Serialize(request) };
                var jobReads = 0;
                var cleanupCalls = 0;
                string[] cleanupSources = null;
                var jobs = InterfaceProxy.Proxy<IConversionJobService>((method, args) =>
                {
                    if (method.Name == nameof(IConversionJobService.Get)) { jobReads++; return acceptedJob; }
                    if (method.Name == nameof(IConversionJobService.IsRunning)) return false;
                    Assert.That(method.Name, Is.EqualTo(nameof(IConversionJobService.RetainCompletedOutput)));
                    cleanupCalls++;
                    cleanupSources = ((IReadOnlyCollection<string>)args[1]).ToArray();
                    Assert.That(args[3], Is.True);
                    var ownsJob = request.Sources.All(source => cleanupSources.Contains(source.Path));
                    return new ConversionRetentionResult(ownsJob && status is ConversionJobStatus.Queued or ConversionJobStatus.Converting
                        ? ConversionRetentionStatus.Pending : ConversionRetentionStatus.None, (string)args[2]);
                });
                var service = new DownloadedBooksImportService(disk, null, matcher, tags, importer,
                    InterfaceProxy.Proxy<IBookService>((method, _) => { Assert.That(method.Name, Is.EqualTo("GetBooksForLookup")); return new List<Book> { book }; }),
                    InterfaceProxy.Proxy<IAuthorService>((method, _) => { Assert.That(method.Name, Is.EqualTo("GetAuthor")); return author; }),
                    null, null, null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger(),
                    ImportLifecycleStubs.EmptyQueue(), jobs);
                var item = new DownloadClientItem { DownloadId = "bonus-boundary", FilePaths = paths.ToList() };
                var remote = new RemoteBook { Author = author, Books = new() { new Book { Id = 4, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:123" } },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) } };
                var results = service.ProcessIncludedMedia(item, remote, new List<EntityHistory>());
                Assert.That(jobReads, Is.EqualTo(1));
                var pendingJob = status is ConversionJobStatus.Queued or ConversionJobStatus.Converting;
                Assert.That(importCalls, Is.EqualTo(!pendingJob && knownEdition ? 1 : 0));
                Assert.That(cleanupCalls, Is.EqualTo(pendingJob ? 0 : 1));
                if (!pendingJob)
                    Assert.That(cleanupSources, Is.EquivalentTo(knownEdition ? paths.Skip(1) : paths), "Only a saved edition grants accepted-work protection");
                Assert.That(results, Has.Count.EqualTo(4));
                if (pendingJob) Assert.That(results.All(result => result.Result == ImportResultType.Pending), Is.True);
                else
                {
                    if (knownEdition) Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Imported));
                    Assert.That(results.Skip(knownEdition ? 1 : 0).All(result => result.IsIncludedMediaSkip), Is.True);
                }
                Assert.That(paths.All(File.Exists), Is.True);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
