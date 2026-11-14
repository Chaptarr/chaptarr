using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportVanishedFileFixture
    {
        private string _folder;
        private string _presentPath;
        private string _vanishedPath;
        private readonly FileSystem _fileSystem = new();
        private readonly Dictionary<string, int> _lengthReads = new();
        private readonly Dictionary<string, int> _tagReads = new();
        private List<ImportDecision<LocalBook>> _importedDecisions;
        private Action _beforeVanishedLength;
        private int _importCalls;
        private int _authorReads;
        private int _matchCalls;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory.GetActualCasing(),
                @"ManualImport\Vanished".AsOsAgnostic(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _presentPath = Path.Combine(_folder, "Disc 2.mp3");
            _vanishedPath = Path.Combine(_folder, "Disc 1.mp3");
            File.WriteAllBytes(_presentPath, new byte[1024]);
            File.WriteAllBytes(_vanishedPath, new byte[512]);
            _lengthReads.Clear();
            _tagReads.Clear();
            _importedDecisions = null;
            _beforeVanishedLength = null;
            _importCalls = 0;
            _authorReads = 0;
            _matchCalls = 0;
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_folder, true);
        }

        [Test]
        public void preview_should_preserve_present_file_and_existing_rejection_when_file_vanishes_after_listing()
        {
            var service = CreateService(vanishAfterListing: true);

            var result = service.GetMediaFiles(_folder, null, new Author { Id = 1 },
                FilterFilesType.None, false, CancellationToken.None, new[] { _presentPath, _vanishedPath });

            Assert.Multiple(() =>
            {
                Assert.That(result.Select(item => item.Path), Is.EquivalentTo(new[] { _presentPath, _vanishedPath }));
                Assert.That(result.Single(item => item.Path == _presentPath).Size, Is.EqualTo(1024));
                Assert.That(result.Single(item => item.Path == _presentPath).Rejections, Is.Empty);
                Assert.That(result.Single(item => item.Path == _vanishedPath).Size, Is.Zero);
                Assert.That(result.Single(item => item.Path == _vanishedPath).Rejections.Select(rejection => rejection.Reason),
                    Has.Some.StartsWith("Failed to process file:"));
                Assert.That(_lengthReads[_presentPath], Is.EqualTo(2));
                Assert.That(_lengthReads[_vanishedPath], Is.EqualTo(1));
                Assert.That(_matchCalls, Is.EqualTo(1));
                Assert.That(_importCalls, Is.Zero);
            });
            TestContext.WriteLine($"Real preview paths: present={_presentPath}; vanished={_vanishedPath}");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void execution_should_reject_missing_file_and_import_present_neighbor(bool suggested)
        {
            File.Delete(_vanishedPath);
            ExecuteAndAssertNeighbor(CreateService(), suggested);
            Assert.That(_tagReads.ContainsKey(_vanishedPath), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void execution_should_reject_file_deleted_during_size_read_and_import_present_neighbor(bool suggested)
        {
            _beforeVanishedLength = () => File.Delete(_vanishedPath);
            ExecuteAndAssertNeighbor(CreateService(), suggested);
            Assert.That(_tagReads.ContainsKey(_vanishedPath), Is.False);
        }

        [Test]
        public void execution_should_reject_missing_parent_directory_and_import_present_neighbor()
        {
            var missingDirectory = Path.Combine(_folder, "Removed");
            Directory.CreateDirectory(missingDirectory);
            File.Move(_vanishedPath, Path.Combine(missingDirectory, "Disc 1.mp3"));
            _vanishedPath = Path.Combine(missingDirectory, "Disc 1.mp3");
            Directory.Delete(missingDirectory, true);
            ExecuteAndAssertNeighbor(CreateService(), false);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void execution_should_preserve_directory_not_found_per_file_semantics(bool suggested)
        {
            _beforeVanishedLength = () => throw new DirectoryNotFoundException("Selected parent directory disappeared");
            ExecuteAndAssertNeighbor(CreateService(), suggested);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void execution_should_not_hide_other_io_or_access_failures(bool suggested, bool accessDenied)
        {
            _beforeVanishedLength = () =>
            {
                if (accessDenied)
                {
                    throw new UnauthorizedAccessException("Access denied");
                }

                throw new IOException("Storage failure");
            };
            var service = CreateService();
            var command = CreateCommand(suggested);
            if (accessDenied)
            {
                Assert.Throws<UnauthorizedAccessException>(() => service.Execute(command));
            }
            else
            {
                Assert.Throws<IOException>(() => service.Execute(command));
            }

            Assert.That(_importCalls, Is.Zero);
        }

        [Test]
        public void absent_single_file_preview_should_remain_empty()
        {
            File.Delete(_vanishedPath);
            Assert.That(CreateService().GetMediaFiles(_vanishedPath, null, null, FilterFilesType.None, false), Is.Empty);
        }

        private void ExecuteAndAssertNeighbor(ManualImportService service, bool suggested)
        {
            var failure = Assert.Throws<NzbDrone.Core.Messaging.Commands.CommandFailedException>(() => service.Execute(CreateCommand(suggested)));
            Assert.That(failure.Message, Does.Contain("Imported 1 files; 1 failed or skipped").And.Contain(_vanishedPath).And.Contain("no longer exists"));
            Assert.Multiple(() =>
            {
                Assert.That(_importCalls, Is.EqualTo(1));
                Assert.That(_importedDecisions.Select(decision => decision.Item.Path),
                    Is.EquivalentTo(new[] { _presentPath, _vanishedPath }));
                var vanished = _importedDecisions.Single(decision => decision.Item.Path == _vanishedPath);
                Assert.That(vanished.Approved, Is.False);
                Assert.That(vanished.Item.Size, Is.Zero);
                Assert.That(vanished.Rejections.Select(rejection => rejection.Reason), Has.Some.Contains("no longer exists"));
                var present = _importedDecisions.Single(decision => decision.Item.Path == _presentPath);
                Assert.That(present.Approved, Is.True);
                Assert.That(present.Item.Size, Is.EqualTo(1024));
                Assert.That(present.Item.Author.Id, Is.EqualTo(1));
                Assert.That(present.Item.Book.Id, Is.EqualTo(10));
                Assert.That(present.Item.Edition.Id, Is.EqualTo(100));
                Assert.That(_authorReads, Is.EqualTo(1));
                Assert.That(_matchCalls, Is.Zero);
                Assert.That(_lengthReads[_presentPath], Is.EqualTo(1));
            });
            TestContext.WriteLine($"Real execution paths: present={_presentPath}; vanished={_vanishedPath}; suggested={suggested}");
        }

        private ManualImportCommand CreateCommand(bool suggested)
        {
            return new ManualImportCommand
            {
                ImportMode = ImportMode.Copy,
                Files = new List<ManualImportFile>
                {
                    new()
                    {
                        Path = _vanishedPath, AuthorId = suggested ? 0 : 1, BookId = suggested ? 0 : 10,
                        EditionId = suggested ? 0 : 100, ForeignAuthorId = suggested ? "hc:9001" : null,
                        ForeignBookId = suggested ? "hc:1001" : null, Quality = new QualityModel(Quality.MP3)
                    },
                    new()
                    {
                        Path = _presentPath, AuthorId = 1, BookId = 10, EditionId = 100,
                        Quality = new QualityModel(Quality.MP3)
                    }
                }
            };
        }

        private ManualImportService CreateService(bool vanishAfterListing = false)
        {
            var author = new Author { Id = 1, Path = Path.Combine(_folder, "Author") };
            var book = new Book { Id = 10, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook };
            var edition = new Edition { Id = 100, BookId = book.Id };
            var logger = LogManager.GetCurrentClassLogger();
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.GetFileInfo) => GetFileInfo((string)args[0]),
                nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                _ => throw new AssertionException(method.Name)
            });
            var tags = InterfaceProxy.Proxy<IMetadataTagService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IMetadataTagService.ReadAllTagsAndDuration)));
                var path = ((IFileInfo)args[0]).FullName;
                _tagReads[path] = _tagReads.GetValueOrDefault(path) + 1;
                return (new Dictionary<string, List<string>>(), (int?)null);
            });
            var matcher = InterfaceProxy.Proxy<IFileMatchingService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IFileMatchingService.MatchFilesToLibraryAsync)));
                _matchCalls++;
                return Task.FromResult(new FileMatchResult());
            });
            var mediaFiles = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                method.Name == nameof(IMediaFileService.GetFileWithPath)
                    ? new List<BookFile>()
                    : throw new AssertionException(method.Name));
            var decisionMaker = new SimpleImportDecisionMaker(tags, matcher, null, null, null, mediaFiles, logger);
            var decisions = InterfaceProxy.Proxy<IMakeImportDecision>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IMakeImportDecision.GetImportDecisions)));
                if (vanishAfterListing)
                {
                    File.Delete(_vanishedPath);
                }

                return decisionMaker.GetImportDecisions((List<IFileInfo>)args[0], (IdentificationOverrides)args[1],
                    (ImportDecisionMakerInfo)args[2], (ImportDecisionMakerConfig)args[3], (CancellationToken)args[4]);
            });
            var dependencies = new Dictionary<Type, object>
            {
                [typeof(IDiskProvider)] = disk,
                [typeof(IMakeImportDecision)] = decisions,
                [typeof(IMetadataTagService)] = tags,
                [typeof(IRootFolderService)] = InterfaceProxy.Proxy<IRootFolderService>((method, args) =>
                    method.Name == nameof(IRootFolderService.GetBestRootFolder)
                        ? (string)args[0] == author.Path ? new RootFolder { Id = 1, Path = _folder } : null
                        : throw new AssertionException(method.Name)),
                [typeof(IAuthorService)] = InterfaceProxy.Proxy<IAuthorService>((method, _) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.GetAuthor)));
                    _authorReads++;
                    return author;
                }),
                [typeof(IBookService)] = InterfaceProxy.Proxy<IBookService>((method, _) =>
                    method.Name == nameof(IBookService.GetBook) ? book : throw new AssertionException(method.Name)),
                [typeof(IEditionService)] = InterfaceProxy.Proxy<IEditionService>((method, _) =>
                    method.Name == nameof(IEditionService.GetEdition) ? edition : throw new AssertionException(method.Name)),
                [typeof(ICustomFormatCalculationService)] = InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, _) =>
                    method.Name == nameof(ICustomFormatCalculationService.ParseCustomFormat)
                        ? new List<CustomFormat>() : throw new AssertionException(method.Name)),
                [typeof(ITrackedDownloadService)] = InterfaceProxy.Proxy<ITrackedDownloadService>((method, _) =>
                    method.Name == nameof(ITrackedDownloadService.GetTrackedDownloads)
                        ? new List<TrackedDownload>() : throw new AssertionException(method.Name)),
                [typeof(IImportApprovedBooks)] = InterfaceProxy.Proxy<IImportApprovedBooks>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IImportApprovedBooks.Import)));
                    _importCalls++;
                    _importedDecisions = (List<ImportDecision<LocalBook>>)args[0];
                    return _importedDecisions.Select(decision => new ImportResult(decision,
                        decision.Approved ? Array.Empty<string>() : decision.Rejections.Select(rejection => rejection.Reason).ToArray())).ToList();
                }),
                [typeof(NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadAlreadyImported)] = new NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()), [typeof(Logger)] = logger
            };
            var constructor = typeof(ManualImportService).GetConstructors().Single();
            return (ManualImportService)constructor.Invoke(constructor.GetParameters()
                .Select(parameter => dependencies.GetValueOrDefault(parameter.ParameterType)).ToArray());
        }

        private IFileInfo GetFileInfo(string path)
        {
            var real = _fileSystem.FileInfo.FromFileName(path);
            return InterfaceProxy.Proxy<IFileInfo>((method, _) =>
            {
                if (method.Name == "get_Length")
                {
                    _lengthReads[path] = _lengthReads.GetValueOrDefault(path) + 1;
                    if (path == _vanishedPath)
                    {
                        _beforeVanishedLength?.Invoke();
                    }

                    real.Refresh();
                    return real.Length;
                }

                return method.Name switch
                {
                    "get_FullName" => real.FullName,
                    "get_Exists" => real.Exists,
                    "get_LastWriteTimeUtc" => real.LastWriteTimeUtc,
                    "get_Extension" => real.Extension,
                    "get_Name" => real.Name,
                    _ => throw new AssertionException(method.Name)
                };
            });
        }
    }
}
