using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Download;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        private static IEnumerable<TestCaseData> IdenticalCopyCases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var scenario in new[] { "identical", "same-size-different", "different-size", "missing-library", "other-side", "unreadable", "permission" })
                yield return new TestCaseData(postgres, media, scenario);

            foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "all-parts", "different-part", "one-file-twice", "in-place", "before-conversion", "last-size-differs", "size-count-mismatch" })
                yield return new TestCaseData(postgres, BookMediaType.Audiobook, scenario);
            yield return new TestCaseData(false, BookMediaType.Audiobook, "slow-identical");
        }

        [TestCaseSource(nameof(IdenticalCopyCases))]
        public void additional_copy_requires_distinct_identical_library_files_before_stopping(bool postgres, BookMediaType mediaType, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var root = Path.Combine(Path.GetTempPath(), "identical-import-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    using var logs = new LogFactory();
                    var sink = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
                    var logging = new NLog.Config.LoggingConfiguration();
                    logging.AddRuleForAllLevels(sink); logs.Configuration = logging;
                    var logger = logs.GetLogger("identical-copy");
                    var events = new NoOpEventAggregator();
                    var (profile, author, book, edition) = CreateAudiobookConversionGraph(501);
                    var quality = mediaType == BookMediaType.Ebook ? Quality.EPUB : scenario == "before-conversion" ? Quality.MP3 : Quality.M4B;
                    profile.UpgradeAllowed = false;
                    profile.ConvertToQualityId = scenario == "before-conversion" ? Quality.M4B.Id : null;
                    profile.ProfileType = mediaType == BookMediaType.Ebook ? ProfileType.Ebook : ProfileType.Audiobook;
                    profile.Items.Add(new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true });
                    author.Id = 0;
                    author.Added = DateTime.UtcNow;
                    author.Path = root;
                    author.EbookPath = root;
                    author.EbookRootFolderPath = root;
                    author.AudiobookPath = root;
                    author.AudiobookRootFolderPath = root;
                    author.EbookQualityProfile = profile;
                    author.EbookQualityProfileId = profile.Id;
                    author.CleanName = "davidarcher";
                    author.SortName = "davidarcher";
                    author.TitleSlug = "david-archer";
                    new AuthorRepository(database, events, logger).Insert(author);
                    book.Id = 0;
                    book.Added = DateTime.UtcNow;
                    book.MediaType = mediaType;
                    book.AuthorId = author.Id;
                    book.AnyEditionOk = true;
                    book.CleanTitle = "blacksheep";
                    var books = new BookRepository(database, events);
                    books.Insert(book);
                    edition.Id = 0;
                    edition.BookId = book.Id;
                    edition.ReadingFormatId = mediaType == BookMediaType.Ebook ? 3 : 2;
                    edition.Monitored = true;
                    var editions = new EditionRepository(database, events);
                    editions.Insert(edition);
                    book.Editions = new List<Edition> { edition };
                    var inventoryEdition = edition;
                    if (scenario == "other-side")
                    {
                        var other = books.Insert(new Book { AuthorId = author.Id, Author = author, Title = book.Title,
                            CleanTitle = book.CleanTitle, TitleSlug = "other-side", Added = DateTime.UtcNow, MediaType = mediaType == BookMediaType.Ebook ? BookMediaType.Audiobook : BookMediaType.Ebook });
                        inventoryEdition = editions.Insert(new Edition { BookId = other.Id, Book = other,
                            Title = edition.Title, ForeignEditionId = edition.ForeignEditionId, ReadingFormatId = mediaType == BookMediaType.Ebook ? 2 : 3 });
                    }

                    var count = scenario is "all-parts" or "different-part" or "last-size-differs" or "size-count-mismatch" ? 3 : scenario == "one-file-twice" ? 2 : 1;
                    var files = new MediaFileRepository(database, events);
                    var decisions = new List<ImportDecision<LocalBook>>();
                    var originals = new Dictionary<string, string>();
                    for (var i = 0; i < count; i++)
                    {
                        var oldPath = Path.Combine(root, "library-" + i + "." + quality.Name.ToLowerInvariant());
                        var source = scenario == "in-place" ? oldPath : Path.Combine(root, "download-" + i + "." + quality.Name.ToLowerInvariant());
                        var oldBytes = scenario == "size-count-mismatch" && i == 2 ? "longer recording" : "recording " + (scenario == "one-file-twice" ? 0 : i);
                        var sourceBytes = scenario == "same-size-different" || (scenario == "different-part" && i == 1)
                            ? "different " + i : scenario == "different-size" || (scenario == "last-size-differs" && i == 2) ? "longer recording"
                            : scenario == "size-count-mismatch" ? "recording " + i : oldBytes;
                        File.WriteAllText(source, sourceBytes);
                        if (scenario != "one-file-twice" || i == 0)
                        {
                            if (scenario != "missing-library") File.WriteAllText(oldPath, oldBytes);
                            files.Insert(new BookFile { EditionId = inventoryEdition.Id, Edition = inventoryEdition, Path = oldPath,
                                Size = oldBytes.Length, Quality = new QualityModel(quality), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                                MediaType = mediaType == BookMediaType.Ebook ? "ebook" : "audiobook", MediaInfo = new MediaInfoModel() });
                        }
                        decisions.Add(new ImportDecision<LocalBook>(new LocalBook { Path = source, Author = author, Book = book,
                            Edition = edition, Quality = new QualityModel(quality), Size = sourceBytes.Length,
                            DurationSeconds = 500, Modified = File.GetLastWriteTimeUtc(source) }));
                    }
                    foreach (var path in Directory.GetFiles(root)) originals[path] = File.ReadAllText(path);
                    var stored = files.All();
                    var memoryFiles = new StubMediaFileService();
                    var catalogLoads = 0;
                    var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) =>
                    {
                        if (method.Name == nameof(IMediaFileService.GetFilesByBook)) { catalogLoads++; return files.GetFilesByBook((int)args[0]); }
                        if (method.Name == nameof(IMediaFileService.GetFileWithPath)) return args[0] is List<string> paths ? files.GetFileWithPath(paths) : files.GetFileWithPath((string)args[0]);
                        return method.Invoke(memoryFiles, args);
                    });
                    var (bookService, bookProxy) = CreateBookService(new List<Book> { book });
                    var mover = new StubMoveBookFiles { DestinationForFile = local => Path.Combine(root, "new-" + Path.GetFileName(local.Path)), TransferFilesOnDisk = true };
                    var disk = RealDiskProvider.Create(logger);
                    var opens = 0;
                    var checkedDisk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
                    {
                        if (method.Name == nameof(IDiskProvider.OpenReadStream))
                        {
                            opens++;
                            if (scenario == "slow-identical" && opens == 1) System.Threading.Thread.Sleep(1100);
                            if (scenario == "unreadable") throw new IOException("test share unavailable");
                            if (scenario == "permission") throw new UnauthorizedAccessException("test permission denied");
                        }
                        return method.Invoke(disk, args);
                    });
                    var converter = new SuccessfulM4bConversionService();
                    var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                        Proxy<IAuthorService>(), bookService, CreateEditionService(book.Editions), new StubRecycleBinProvider(),
                        Proxy<IExtraService>(), mover, Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                        events, Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                        Proxy<IQualityProfileService>(), converter, logger,
                        diskProvider: checkedDisk, configService: CreateConfigService(),
                        conversionJobService: scenario == "before-conversion" ? CreateConversionJobs(converter, media: media) : null);
                    List<ImportResult> results = null;
                    var harness = new CompletedDownloadServiceQualityProfileFixture.Harness(client => results = service.Import(decisions, true, client, ImportMode.Copy));
                    harness.TrackedDownload.RemoteBook.Author = author;
                    harness.TrackedDownload.RemoteBook.Books = new List<Book> { book };
                    harness.TrackedDownload.RemoteBook.ParsedBookInfo.Quality = new QualityModel(quality);
                    harness.Subject.Import(harness.TrackedDownload);

                    var stopped = scenario is "identical" or "all-parts" or "in-place" or "before-conversion" or "unreadable" or "permission" or "slow-identical";
                    Assert.That(results, Has.Count.EqualTo(count));
                    Assert.That(files.All().Select(file => (file.Id, file.EditionId, file.Path)), Is.EqualTo(stored.Select(file => (file.Id, file.EditionId, file.Path))));
                    Assert.That(originals.All(pair => File.Exists(pair.Key) && File.ReadAllText(pair.Key) == pair.Value), Is.True);
                    Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
                    if (stopped)
                    {
                        Assert.That(results.All(result => result.Result == (scenario == "unreadable" ? ImportResultType.Pending : ImportResultType.Skipped)), Is.True);
                        Assert.That(harness.TrackedDownload.State, Is.EqualTo(scenario == "unreadable" ? TrackedDownloadState.ImportPending : TrackedDownloadState.ImportBlocked));
                        if (scenario is "unreadable" or "permission")
                        {
                            Assert.That(results.SelectMany(result => result.Errors), Has.Some.Contains("Unable to compare"));
                            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(message => message.Messages),
                                Has.Some.Contains(scenario == "permission" ? "permission denied" : "Unable to compare"));
                        }
                        else
                            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains("Already in your library"));
                        Assert.That(harness.Subject.CanRemoveDownload(harness.TrackedDownload), Is.False);
                        Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
                        Assert.That(bookProxy.InsertedBooks, Is.Empty);
                        Assert.That(mover.CopyCalls + mover.MoveCalls, Is.Zero);
                        Assert.That(memoryFiles.FilesByBook, Is.Empty);
                        Assert.That(catalogLoads, Is.EqualTo(1));
                    }
                    else
                    {
                        Assert.That(results.All(result => result.Result == ImportResultType.Imported), Is.True, string.Join("; ", results.SelectMany(result => result.Errors)));
                        Assert.That(mover.CopyCalls, Is.EqualTo(count));
                        Assert.That(bookProxy.InsertedBooks.Count, Is.EqualTo(scenario == "other-side" ? 0 : 1));
                    }
                    if (scenario is "different-size" or "other-side" or "in-place" or "last-size-differs" or "size-count-mismatch") Assert.That(opens, Is.Zero);
                    var stops = sink.Logs.Where(line => line.StartsWith("Info|[IMPORT-IDENTICAL]")).ToList();
                    Assert.That(stops.Count, Is.EqualTo(stopped && scenario is not ("unreadable" or "permission") ? 1 : 0));
                    if (stops.Count > 0)
                    {
                        Assert.That(stops.Single(), Does.Contain("BookId=" + book.Id));
                        Assert.That(stops.Single(), Does.Contain("DownloadId=" + harness.TrackedDownload.DownloadItem.DownloadId));
                        Assert.That(stops.Single(), Does.Contain("Nothing was imported"));
                    }
                    if (scenario == "slow-identical")
                        Assert.That(sink.Logs.Count(line => line.StartsWith("Warn|[IMPORT-IDENTICAL]") && line.Contains("comparison took")), Is.EqualTo(1));
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });
        }
    }
}
