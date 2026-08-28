using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ManualImportGroupedExecutionFixture
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
                    "get_LastWriteTimeUtc" => DateTime.UtcNow,
                    _ => throw new NotImplementedException($"IFileInfo.{targetMethod?.Name}")
                };
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.GetFileInfo))
                {
                    return CreateFileInfo(args?[0] as string);
                }

                throw new NotImplementedException($"IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            public RootFolder Root { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IRootFolderService.GetBestRootFolder) => Root,
                    nameof(IRootFolderService.All) => new List<RootFolder> { Root },
                    _ => throw new NotImplementedException($"IRootFolderService.{targetMethod?.Name}")
                };
            }
        }

        private class RootFolderSettingsProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderSettingsResolver.ResolveSettings))
                {
                    return new ResolvedRootFolderSettings
                    {
                        IsConfigured = true,
                        QualityProfileId = 1,
                        MetadataProfileId = 1,
                        MonitorExistingBooks = false,
                        MonitorNewItems = NzbDrone.Core.Books.NewItemMonitorTypes.None,
                        Tags = new List<int>()
                    };
                }

                throw new NotImplementedException($"IRootFolderSettingsResolver.{targetMethod?.Name}");
            }
        }

        private class MetadataTagServiceProxy : DispatchProxy
        {
            public int Reads { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMetadataTagService.ReadAllTagsAndDuration) &&
                    args?[0] is IFileInfo file)
                {
                    Reads++;
                    var number = int.Parse(System.IO.Path.GetFileNameWithoutExtension(file.FullName).Split('-').Last());
                    var duration = number <= 336 ? 133 : 132;
                    return (new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ALBUM"] = new() { "BOSCH Schwarzes Echo" },
                        ["ARTIST"] = new() { "Michael Connelly" },
                        ["TITLE"] = new() { $"Kapitel {number:000} BOSCH Schwarzes Echo" }
                    }, (int?)duration);
                }

                throw new NotImplementedException($"IMetadataTagService.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId) ||
                    targetMethod?.Name == nameof(IAuthorService.GetAuthor))
                {
                    return Author;
                }

                throw new NotImplementedException($"IAuthorService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.FindAllByWorkProviderId))
                {
                    return new List<Book> { Book };
                }

                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book;
                }

                throw new NotImplementedException($"IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Edition Edition { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return Edition;
                }

                throw new NotImplementedException($"IEditionService.{targetMethod?.Name}");
            }
        }

        private class AuthorLibraryServiceProxy : DispatchProxy
        {
            public UserSelectedEditionMaterialization Result { get; set; }
            public int MaterializeCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorLibraryService.MaterializeUserSelectedEditionAsync))
                {
                    MaterializeCalls++;
                    return Task.FromResult(Result);
                }

                throw new AssertionException($"Existing provider work should not hydrate through IAuthorLibraryService.{targetMethod?.Name}");
            }
        }

        private sealed class FileMatchingServiceStub : IFileMatchingService
        {
            private readonly Author _author;
            private readonly Book _book;
            private readonly Edition _edition;

            public FileMatchingServiceStub(Author author, Book book, Edition edition)
            {
                _author = author;
                _book = book;
                _edition = edition;
            }

            public int GroupedCalls { get; private set; }
            public int HolyGrailCalls { get; private set; }
            public int ObservedDurationSeconds { get; private set; }
            public MatchingContext Context { get; private set; }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, null, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
            {
                return MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, new MatchingContext());
            }

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, MatchingContext context)
            {
                GroupedCalls++;
                Context = context;
                ObservedDurationSeconds = filesWithMetadata.Sum(file => file.DurationSeconds ?? 0);
                return Task.FromResult(new FileMatchResult
                {
                    MatchedFiles = filesWithMetadata.Select(file => new FileMatch
                    {
                        File = file,
                        AuthorId = _author.Id,
                        AuthorName = _author.Name,
                        BookId = _book.Id,
                        BookTitle = _book.Title,
                        EditionId = _edition.Id,
                        MatchedVia = "grouped_test"
                    }).ToArray()
                });
            }

            public EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, BookMediaType mediaType)
            {
                HolyGrailCalls++;
                return null;
            }

            public FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, BookMediaType mediaType, int? restrictToAuthorId = null)
            {
                HolyGrailCalls++;
                return null;
            }
        }

        private sealed class ImportApprovedBooksStub : IImportApprovedBooks
        {
            public List<ImportDecision<LocalBook>> Decisions { get; private set; } = new();

            public List<ImportResult> Import(
                List<ImportDecision<LocalBook>> decisions,
                bool replaceExisting,
                DownloadClientItem downloadClientItem = null,
                ImportMode importMode = ImportMode.Auto,
                CancellationToken cancellationToken = default)
            {
                Decisions = decisions;
                return new List<ImportResult>();
            }
        }

        private class TrackedDownloadServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ITrackedDownloadService.GetTrackedDownloads))
                {
                    return new List<TrackedDownload>();
                }

                throw new NotImplementedException($"ITrackedDownloadService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void execution_should_match_a_437_track_provider_work_once_with_total_duration()
        {
            var root = new RootFolder { Id = 1, Path = "/library" };
            var author = new Author
            {
                Id = 42,
                Name = "Michael Connelly",
                Path = "/library/Michael Connelly",
                HardcoverAuthorId = "hc:123"
            };
            var edition = new Edition
            {
                Id = 22002,
                BookId = 2202,
                Title = "BOSCH: Schwarzes Echo",
                ForeignEditionId = "gr:229391768",
                ReadingFormatId = 2
            };
            var book = new Book
            {
                Id = 2202,
                AuthorId = author.Id,
                Author = author,
                Title = "The Black Echo",
                HardcoverBookId = "hc:223021",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { edition }
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var rootFolders = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolders).Root = root;
            var rootSettings = DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsProxy>();
            var tags = DispatchProxy.Create<IMetadataTagService, MetadataTagServiceProxy>();
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authors).Author = author;
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)books).Book = book;
            var editions = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editions).Edition = edition;
            var authorLibrary = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            var matcher = new FileMatchingServiceStub(author, book, edition);
            var importer = new ImportApprovedBooksStub();
            var trackedDownloads = DispatchProxy.Create<ITrackedDownloadService, TrackedDownloadServiceProxy>();

            var service = new ManualImportService(
                diskProvider,
                null,
                rootFolders,
                null,
                null,
                authors,
                books,
                editions,
                authorLibrary,
                rootSettings,
                null,
                matcher,
                null,
                tags,
                importer,
                null,
                trackedDownloads,
                null,
                null,
                null,
                null,
                null,
                LogManager.GetCurrentClassLogger());

            var files = Enumerable.Range(1, 437)
                .Select(number => new ManualImportFile
                {
                    Path = $"/library/Michael Connelly/Schwarzes Echo/chapter-{number}.mp3",
                    ForeignAuthorId = "hc:123",
                    ForeignAuthorName = author.Name,
                    ForeignBookId = "hc:1987747",
                    ForeignBookTitle = book.Title,
                    ForeignEditionId = edition.ForeignEditionId,
                    ForeignEditionTitle = edition.Title,
                    Quality = new QualityModel(Quality.MP3)
                })
                .ToList();

            service.Execute(new ManualImportCommand
            {
                Files = files,
                ImportMode = ImportMode.Move
            });

            Assert.Multiple(() =>
            {
                Assert.That(((MetadataTagServiceProxy)(object)tags).Reads, Is.EqualTo(437));
                Assert.That(matcher.GroupedCalls, Is.EqualTo(1));
                Assert.That(matcher.HolyGrailCalls, Is.Zero);
                Assert.That(((AuthorLibraryServiceProxy)(object)authorLibrary).MaterializeCalls, Is.Zero,
                    "an automatic server suggestion must never gain manual pin authority merely because it carries an edition ID");
                Assert.That(matcher.ObservedDurationSeconds, Is.EqualTo(58020));
                Assert.That(matcher.Context.HardAllowedBookIds, Is.EqualTo(new[] { book.Id }));
                Assert.That(importer.Decisions, Has.Count.EqualTo(437));
                Assert.That(importer.Decisions, Has.All.Matches<ImportDecision<LocalBook>>(decision =>
                    decision.Item.Book?.Id == book.Id &&
                    decision.Item.Edition?.Id == edition.Id &&
                    !decision.Rejections.Any()));
            });
        }

        [Test]
        public void explicit_metadata_selection_should_materialize_once_and_bypass_evidence_vetoes()
        {
            var root = new RootFolder { Id = 1, Path = "/library" };
            var author = new Author
            {
                Id = 42,
                Name = "Michael Connelly",
                Path = "/library/Michael Connelly",
                HardcoverAuthorId = "hc:123"
            };
            var edition = new Edition
            {
                Id = 22002,
                BookId = 2202,
                Title = "BOSCH: Schwarzes Echo",
                ForeignEditionId = "gr:229391768",
                ReadingFormatId = 2
            };
            var book = new Book
            {
                Id = 2202,
                AuthorId = author.Id,
                Author = author,
                Title = "The Black Echo",
                HardcoverBookId = "hc:223021",
                MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { edition }
            };

            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var rootFolders = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            ((RootFolderServiceProxy)(object)rootFolders).Root = root;
            var rootSettings = DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsProxy>();
            var tags = DispatchProxy.Create<IMetadataTagService, MetadataTagServiceProxy>();
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authors).Author = author;
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)books).Book = book;
            var editions = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            ((EditionServiceProxy)(object)editions).Edition = edition;
            var authorLibrary = DispatchProxy.Create<IAuthorLibraryService, AuthorLibraryServiceProxy>();
            ((AuthorLibraryServiceProxy)(object)authorLibrary).Result = new UserSelectedEditionMaterialization
            {
                Author = author,
                Book = book,
                Edition = edition
            };
            var matcher = new FileMatchingServiceStub(author, book, edition);
            var importer = new ImportApprovedBooksStub();
            var trackedDownloads = DispatchProxy.Create<ITrackedDownloadService, TrackedDownloadServiceProxy>();

            var service = new ManualImportService(
                diskProvider,
                null,
                rootFolders,
                null,
                null,
                authors,
                books,
                editions,
                authorLibrary,
                rootSettings,
                null,
                matcher,
                null,
                tags,
                importer,
                null,
                trackedDownloads,
                null,
                null,
                null,
                null,
                null,
                LogManager.GetCurrentClassLogger());

            var files = Enumerable.Range(1, 16)
                .Select(number => new ManualImportFile
                {
                    Path = $"/library/Michael Connelly/Schwarzes Echo/chapter-{number}.mp3",
                    ForeignAuthorId = "hc:123",
                    ForeignAuthorName = author.Name,
                    ForeignBookId = "hc:1987747",
                    ForeignBookTitle = book.Title,
                    ForeignEditionId = edition.ForeignEditionId,
                    ForeignEditionTitle = edition.Title,
                    SelectionSource = ManualImportSelectionSource.UserMetadataSuggestion,
                    Quality = new QualityModel(Quality.MP3)
                })
                .ToList();

            service.Execute(new ManualImportCommand
            {
                Files = files,
                ImportMode = ImportMode.Move
            });

            Assert.Multiple(() =>
            {
                Assert.That(((AuthorLibraryServiceProxy)(object)authorLibrary).MaterializeCalls, Is.EqualTo(1));
                Assert.That(matcher.GroupedCalls, Is.Zero);
                Assert.That(matcher.HolyGrailCalls, Is.Zero);
                Assert.That(importer.Decisions, Has.Count.EqualTo(16));
                Assert.That(importer.Decisions, Has.All.Matches<ImportDecision<LocalBook>>(decision =>
                    decision.Item.Book?.Id == book.Id &&
                    decision.Item.Edition?.Id == edition.Id &&
                    decision.Item.MatchProvenance?.SelectionSource == "user_metadata" &&
                    !decision.Rejections.Any()));
            });
        }

        [Test]
        public void explicit_metadata_selection_source_should_round_trip_through_the_command_json_contract()
        {
            var json = STJson.ToJson(new ManualImportFile
            {
                Path = "/library/book/chapter.mp3",
                SelectionSource = ManualImportSelectionSource.UserMetadataSuggestion
            });
            var roundTripped = STJson.Deserialize<ManualImportFile>(json);

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Contain("\"selectionSource\": \"userMetadataSuggestion\""));
                Assert.That(roundTripped.SelectionSource, Is.EqualTo(ManualImportSelectionSource.UserMetadataSuggestion));
            });
        }

        private static IFileInfo CreateFileInfo(string path)
        {
            var file = DispatchProxy.Create<IFileInfo, FileInfoProxy>();
            ((FileInfoProxy)(object)file).Path = path;
            return file;
        }
    }
}
