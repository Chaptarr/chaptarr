using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using Chaptarr.Core.Test.Common;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportScopedPreviewFixture
    {
        private class FileInfoProxy : DispatchProxy
        {
            public string Path { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_FullName" => Path,
                    "get_Exists" => true,
                    "get_Length" => 1024L,
                    "get_Extension" => System.IO.Path.GetExtension(Path),
                    "get_Name" => System.IO.Path.GetFileName(Path),
                    _ => throw new NotImplementedException($"IFileInfo.{targetMethod?.Name}")
                };
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public bool IsFolder { get; set; } = true;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var path = args?.FirstOrDefault() as string;
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FolderExists) => IsFolder,
                    nameof(IDiskProvider.FileExists) => true,
                    nameof(IDiskProvider.GetFileInfo) => CreateFileInfo(path),
                    _ => throw new NotImplementedException($"IDiskProvider.{targetMethod?.Name}")
                };
            }
        }

        private class DiskScanServiceProxy : DispatchProxy
        {
            public int Calls { get; private set; }
            public IFileInfo[] Files { get; set; } = Array.Empty<IFileInfo>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskScanService.GetBookFiles))
                {
                    Calls++;
                    return Files;
                }

                throw new NotImplementedException($"IDiskScanService.{targetMethod?.Name}");
            }
        }

        private class ImportDecisionMakerProxy : DispatchProxy
        {
            public List<string> Paths { get; private set; } = new List<string>();
            public Func<string, LocalBook> CreateBook { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMakeImportDecision.GetImportDecisions) &&
                    args?[0] is List<IFileInfo> files)
                {
                    Paths = files.Select(file => file.FullName).ToList();
                    return files.Select(file =>
                    {
                        var local = CreateBook?.Invoke(file.FullName) ?? new LocalBook
                        {
                            Path = file.FullName,
                            Quality = new QualityModel(Quality.MP3)
                        };
                        if (args[1] is IdentificationOverrides { Edition: not null } overrides)
                        {
                            local.Edition = overrides.Edition;
                        }

                        return new ImportDecision<LocalBook>(local);
                    }).ToList();
                }

                throw new NotImplementedException($"IMakeImportDecision.{targetMethod?.Name}");
            }
        }

        private class FormatCalculatorProxy : DispatchProxy
        {
            public List<LocalBook> Inputs { get; } = new();
            public List<CustomFormat> Formats { get; } = new() { new CustomFormat { Id = 41, Name = "Recording" } };

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ICustomFormatCalculationService.ParseCustomFormat) && args?[0] is LocalBook input)
                {
                    Inputs.Add(input);
                    return Formats;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class ConversionJobsProxy : DispatchProxy
        {
            public int BatchCalls { get; private set; }
            public List<string> DownloadIds { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IConversionJobService.GetCompletedConversions))
                {
                    BatchCalls++;
                    DownloadIds = ((IEnumerable<string>)args[0]).ToList();
                    return new List<ConversionJobRequest> { new() { DownloadId = "download-A", OutputPath = "/downloads/book/output.m4b" } };
                }

                if (targetMethod?.Name == nameof(IConversionJobService.GetCompletedConversion)) return null;
                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void preview_scores_recordings_once_and_keeps_manual_source_quality(bool update, bool generated)
        {
            var calculator = DispatchProxy.Create<ICustomFormatCalculationService, FormatCalculatorProxy>();
            var formatProxy = (FormatCalculatorProxy)(object)calculator;
            var conversionJobs = DispatchProxy.Create<IConversionJobService, ConversionJobsProxy>();
            var jobsProxy = (ConversionJobsProxy)(object)conversionJobs;
            var (service, _, decisionMaker) = CreateService("unused", true, calculator, conversionJobs);
            var author = new Author
            {
                Id = 1,
                Name = "Author",
                AudiobookQualityProfile = new NzbDrone.Core.Datastore.LazyLoaded<QualityProfile>(new QualityProfile
                {
                    ProfileType = ProfileType.Audiobook, ConvertToQualityId = Quality.M4B.Id,
                    Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.M4B, Allowed = true } }
                })
            };
            var book = new Book { Id = 5, MediaType = BookMediaType.Audiobook };
            var preserveGenerated = true;
            decisionMaker.CreateBook = path => new LocalBook
            {
                Path = path, Author = author, Book = book,
                Edition = new Edition { Id = path.Contains("other") ? 12 : 11, BookId = book.Id },
                Size = path.Contains("output") ? 13 : path.Contains("other") ? 90 : 10,
                Quality = new QualityModel(path.Contains("output") ? Quality.M4B : Quality.MP3),
                IsGeneratedConversion = preserveGenerated && path.Contains("output"),
                RawTags = new RawFileTags { AllTags = new Dictionary<string, List<string>>
                {
                    ["COMMENT"] = new() { path.EndsWith("2.mp3") ? "Dramatized" : "First" }
                } }
            };
            var paths = new[] { "/downloads/book/1.mp3", "/downloads/book/2.mp3", "/downloads/book/other.mp3" };
            if (generated) paths = paths.Append("/downloads/book/output.m4b").ToArray();
            var preview = service.GetMediaFiles("/downloads/book", null, author, FilterFilesType.Matched,
                false, CancellationToken.None, paths);
            if (update)
            {
                formatProxy.Inputs.Clear();
                preserveGenerated = false;
                foreach (var item in preview) item.DownloadId = "download-A";
                preview = service.UpdateItems(preview);
                Assert.That(jobsProxy.BatchCalls, Is.EqualTo(1));
                Assert.That(jobsProxy.DownloadIds, Is.EqualTo(new[] { "download-A" }));
            }

            Assert.That(formatProxy.Inputs, Has.Count.EqualTo(generated ? 3 : 2));
            Assert.That(formatProxy.Inputs.Select(input => input.Size), Is.EquivalentTo(generated ? new long[] { 20, 90, 13 } : new long[] { 20, 90 }));
            Assert.That(formatProxy.Inputs.Where(input => !input.IsGeneratedConversion).Select(input => input.Quality.Quality), Is.All.EqualTo(Quality.MP3));
            if (generated) Assert.That(formatProxy.Inputs.Single(input => input.IsGeneratedConversion).Quality.Quality, Is.EqualTo(Quality.M4B));
            Assert.That(formatProxy.Inputs.Single(input => input.Edition.Id == 11 && !input.IsGeneratedConversion).RawTags.AllTags["COMMENT"],
                Is.EquivalentTo(new[] { "First", "Dramatized" }));
            Assert.That(preview.All(item => item.CustomFormats.SequenceEqual(formatProxy.Formats)), Is.True);
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        public void download_linked_preview_and_reprocess_share_apply_release_and_completed_part_evidence(bool update, bool blankDownloadId, bool replace)
        {
            var directory = Path.Combine(Path.GetTempPath(), "manual-linked-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var storedPath = Path.Combine(directory, "stored-first.mp3");
                File.WriteAllText(storedPath, "durable-first");
                var author = new Author { Id = 1, Name = "Author" };
                var book = new Book { Id = 5, MediaType = BookMediaType.Audiobook, Author = author };
                var edition = new Edition { Id = 11, BookId = book.Id, Book = book };
                var stored = new BookFile { Id = 7, Path = storedPath, Size = 15, Edition = edition, EditionId = edition.Id,
                    Quality = new QualityModel(Quality.MP3), MatchProvenance = new MatchProvenance
                    { ImportDownloadId = "linked", ImportSourcePaths = new() { Path.Combine(directory, "moved-first.mp3") } } };
                book.BookFiles = new List<BookFile> { stored };
                var remote = new RemoteBook { Author = author, Books = new() { book },
                    Release = new ReleaseInfo { Title = "Author - Book Retail" },
                    ParsedBookInfo = new ParsedBookInfo { ReleaseTitle = "Author - Book Retail", ReleaseGroup = "GroupX" } };
                var tracked = new TrackedDownload { State = TrackedDownloadState.ImportPending, RemoteBook = remote, DownloadItem = new DownloadClientItem { DownloadId = "linked", OutputPath = new NzbDrone.Common.Disk.OsPath(directory) } };
                tracked.ImportItem = tracked.DownloadItem;
                var finds = 0; var bookReads = 0; var historyReads = 0;
                var trackedService = InterfaceProxy.Proxy<ITrackedDownloadService>((method, _) =>
                {
                    finds++;
                    if (method.Name == "GetTrackedDownloads") return new List<TrackedDownload> { tracked };
                    Assert.That(method.Name, Is.EqualTo("Find")); return tracked;
                });
                var bookService = InterfaceProxy.Proxy<IBookService>((method, _) =>
                {
                    if (method.Name == "GetBook") return book;
                    Assert.That(method.Name, Is.EqualTo("GetBooksForLookup")); bookReads++; return new List<Book> { book };
                });
                var history = InterfaceProxy.Proxy<IHistoryService>((method, _) => { Assert.That(method.Name, Is.EqualTo("FindByDownloadIds")); historyReads++; return new List<EntityHistory>(); });
                var calculator = DispatchProxy.Create<ICustomFormatCalculationService, FormatCalculatorProxy>();
                var proxy = (FormatCalculatorProxy)(object)calculator;
                var (service, _, maker) = CreateService("unused", true, calculator, null, trackedService, bookService, history);
                var selectedPath = Path.Combine(directory, "remaining.mp3");
                maker.CreateBook = path => new LocalBook { Path = path, Author = author, Book = book, Edition = edition,
                    Size = 20, Quality = new QualityModel(Quality.MP3) };
                var preview = service.GetMediaFiles(directory, update ? null : "linked", author, FilterFilesType.Matched, replace, CancellationToken.None, new[] { selectedPath });
                if (update)
                {
                    proxy.Inputs.Clear(); finds = 0; bookReads = 0; historyReads = 0;
                    foreach (var item in preview) item.DownloadId = blankDownloadId ? null : "linked";
                    preview = service.UpdateItems(preview);
                }
                var actual = proxy.Inputs.Single();
                var selected = maker.CreateBook(selectedPath);
                selected.IsManualImport = true;
                selected.ReleaseGroup = preview.Single().ReleaseGroup;
                DownloadedBooksImportService.ApplyTrackedReleaseEvidence(new[] { new ImportDecision<LocalBook>(selected) }, remote, tracked.DownloadItem);
                var completed = RecordingCustomFormatHelper.GetCompletedParts(new[] { selected }, new[] { stored }, "linked", new Dictionary<int, List<string>>(),
                    excludedFileIds: replace ? new HashSet<int> { stored.Id } : null);
                var apply = RecordingCustomFormatHelper.CreateInput(new[] { selected }.Concat(completed).ToList());
                Assert.That(actual.Size, Is.EqualTo(replace ? 20L : 35L).And.EqualTo(apply.Size));
                Assert.That(actual.SceneName, Is.EqualTo(apply.SceneName).And.EqualTo("Author - Book Retail"));
                Assert.That(actual.ReleaseGroup, Is.EqualTo(apply.ReleaseGroup).And.EqualTo("GroupX"));
                Assert.That(actual.Quality.Quality, Is.EqualTo(Quality.MP3));
                Assert.That(File.Exists(stored.MatchProvenance.ImportSourcePaths.Single()), Is.False, "Moved source must fall back to durable stored size");
                Assert.That(finds, Is.EqualTo(1)); Assert.That(bookReads, Is.EqualTo(1)); Assert.That(historyReads, Is.EqualTo(1));
                if (update)
                {
                    proxy.Inputs.Clear();
                    service.UpdateItems(preview);
                    Assert.That(proxy.Inputs.Single().Size, Is.EqualTo(replace ? 20L : 35L));
                }
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void shared_manual_builder_preserves_edits_and_limits_other_format_release_evidence(bool otherFormat)
        {
            var local = new LocalBook { IsManualImport = true, Book = new Book { Id = 5, MediaType = otherFormat ? BookMediaType.Ebook : BookMediaType.Audiobook },
                Quality = new QualityModel(otherFormat ? Quality.EPUB : Quality.MP3), ReleaseGroup = "EditedGroup", IndexerFlags = IndexerFlags.Internal,
                Narrator = "File Narrator", AudioProductionType = "File Production" };
            var decision = new ImportDecision<LocalBook>(local);
            var tracked = new TrackedDownload { DownloadItem = new DownloadClientItem { Title = "Release Retail" },
                RemoteBook = new RemoteBook { Books = new() { new Book { Id = 5, MediaType = BookMediaType.Audiobook } },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3), ReleaseGroup = "ReleaseGroup", Narrator = "Release Narrator", AudioProductionType = "Release Production" },
                    Release = new ReleaseInfo { Title = "Release Retail", IndexerFlags = IndexerFlags.Freeleech, Narrator = "Release Narrator" } } };
            typeof(ManualImportService).GetMethod("PrepareManualAssociation", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new List<ImportDecision<LocalBook>> { decision }, tracked });
            Assert.That(local.IsManualImport, Is.True);
            Assert.That(local.ReleaseGroup, Is.EqualTo("EditedGroup"));
            Assert.That(local.IndexerFlags, Is.EqualTo(IndexerFlags.Internal));
            Assert.That(local.SceneName, Is.EqualTo("Release Retail"));
            Assert.That(local.Quality.Quality, Is.EqualTo(otherFormat ? Quality.EPUB : Quality.MP3));
            Assert.That(local.Narrator, Is.EqualTo(otherFormat ? "File Narrator" : "Release Narrator"));
            Assert.That(local.AudioProductionType, Is.EqualTo(otherFormat ? "File Production" : "Release Production"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void shared_manual_builder_validates_alternative_copy_targets_and_carries_grab_date(bool allowed)
        {
            var date = DateTime.UtcNow.AddHours(-1);
            var local = new LocalBook { Book = new Book { Id = allowed ? 5 : 6, MediaType = BookMediaType.Audiobook }, Quality = new QualityModel(Quality.MP3) };
            var decision = new ImportDecision<LocalBook>(local);
            var tracked = new TrackedDownload { DownloadItem = new DownloadClientItem(), RemoteBook = new RemoteBook
            { AlternativeCopies = true, AlternativeCopyGrabDate = date, Books = new() { new Book { Id = 5, MediaType = BookMediaType.Audiobook } } } };
            typeof(ManualImportService).GetMethod("PrepareManualAssociation", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new List<ImportDecision<LocalBook>> { decision }, tracked });
            Assert.That(decision.Approved, Is.EqualTo(allowed));
            Assert.That(local.AlternativeCopyGrabDate, Is.EqualTo(allowed ? date : (DateTime?)null));
            if (!allowed) Assert.That(decision.Rejections.Select(rejection => rejection.Reason), Has.Some.Contains("original alternative-copy target"));
        }

        [Test]
        public void row_scoped_preview_should_match_only_requested_book_unit_files()
        {
            var folder = "/library/Author/Book";
            var requested = new[]
            {
                $"{folder}/Disc 1.mp3",
                $"{folder}/Disc 2.mp3"
            };
            var unrelated = $"{folder}/Different Book.mp3";
            var (service, diskScan, decisionMaker) = CreateService(unrelated);

            var result = service.GetMediaFiles(
                folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                requested);

            Assert.That(diskScan.Calls, Is.Zero, "an exact row scope must not enumerate the folder");
            Assert.That(decisionMaker.Paths, Is.EquivalentTo(requested));
            Assert.That(decisionMaker.Paths, Does.Not.Contain(unrelated));
            Assert.That(result.Select(item => item.Path), Is.EquivalentTo(requested));
        }

        [Test]
        public void exact_scope_should_allow_files_in_authoritative_descendant_disc_folders()
        {
            var folder = "/library/Author/Book";
            var requested = new[]
            {
                $"{folder}/CD1/01.mp3",
                $"{folder}/Disc 2/02.mp3"
            };
            var unrelated = $"{folder}/Other/Unrelated.mp3";
            var (service, diskScan, decisionMaker) = CreateService(unrelated);

            var result = service.GetMediaFiles(
                folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                requested);

            Assert.That(diskScan.Calls, Is.Zero, "an exact descendant scope must not enumerate the folder");
            Assert.That(decisionMaker.Paths, Is.EquivalentTo(requested));
            Assert.That(decisionMaker.Paths, Does.Not.Contain(unrelated));
            Assert.That(result.Select(item => item.Path), Is.EquivalentTo(requested));
        }

        [Test]
        public void stale_empty_row_scope_should_not_expand_back_to_the_whole_folder()
        {
            var folder = "/library/Author/Book";
            var unrelated = $"{folder}/Different Book.mp3";
            var (service, diskScan, decisionMaker) = CreateService(unrelated);

            var result = service.GetMediaFiles(
                folder,
                null,
                new Author { Id = 1, Name = "Author" },
                FilterFilesType.Matched,
                false,
                CancellationToken.None,
                Array.Empty<string>());

            Assert.That(diskScan.Calls, Is.Zero, "an empty exact scope is still an exact scope");
            Assert.That(decisionMaker.Paths, Is.Empty);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void exact_preview_should_exclude_mac_sidecars_without_excluding_real_chapters()
        {
            var folder = "/downloads/Dune";
            var real = new[] { $"{folder}/Dune Part 01.mp3", $"{folder}/Dune Part 02.mp3" };
            var requested = real.Concat(real.Select(path => $"{folder}/._{Path.GetFileName(path)}")).ToArray();
            var (service, diskScan, decisionMaker) = CreateService("unused");

            var result = service.GetMediaFiles(folder, null, new Author { Id = 1 }, FilterFilesType.Matched,
                false, CancellationToken.None, requested);

            Assert.That(diskScan.Calls, Is.Zero);
            Assert.That(decisionMaker.Paths, Is.EquivalentTo(real));
            Assert.That(result.Select(item => item.Path), Is.EquivalentTo(real));
        }

        [Test]
        public void single_file_preview_should_not_offer_a_mac_sidecar()
        {
            var (service, diskScan, decisionMaker) = CreateService("unused", false);

            var result = service.GetMediaFiles("/downloads/Dune/._Dune Part 01.mp3", null,
                new Author { Id = 1 }, FilterFilesType.Matched, false, CancellationToken.None);

            Assert.That(result, Is.Empty);
            Assert.That(decisionMaker.Paths, Is.Empty);
            Assert.That(diskScan.Calls, Is.Zero);
        }

        private static (ManualImportService Service, DiskScanServiceProxy DiskScan, ImportDecisionMakerProxy DecisionMaker) CreateService(string unrelatedPath, bool isFolder = true, ICustomFormatCalculationService formatCalculator = null, IConversionJobService conversionJobs = null, ITrackedDownloadService trackedDownloads = null, IBookService books = null, IHistoryService history = null)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).IsFolder = isFolder;
            var diskScanService = DispatchProxy.Create<IDiskScanService, DiskScanServiceProxy>();
            var diskScanProxy = (DiskScanServiceProxy)(object)diskScanService;
            diskScanProxy.Files = new[] { CreateFileInfo(unrelatedPath) };
            var decisionMaker = DispatchProxy.Create<IMakeImportDecision, ImportDecisionMakerProxy>();
            var decisionMakerProxy = (ImportDecisionMakerProxy)(object)decisionMaker;

            var service = new ManualImportService(
                diskProvider,
                null,
                null,
                diskScanService,
                decisionMaker,
                null,
                books,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                formatCalculator,
                trackedDownloads,
                null,
                history,
                null,
                null,
                null,
                LogManager.GetCurrentClassLogger(), new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(NLog.LogManager.GetCurrentClassLogger()), conversionJobs);


            return (service, diskScanProxy, decisionMakerProxy);
        }

        private static IFileInfo CreateFileInfo(string path)
        {
            var fileInfo = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
            ((FileInfoProxy)(object)fileInfo).Path = path;
            return fileInfo;
        }
    }
}
