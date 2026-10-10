using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class RenameBookFileServiceFixture
    {
        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class StubAuthorService : IAuthorService
        {
            public Author Author { get; set; }

            public Author GetAuthor(int authorId) => Author;
            public List<Author> GetAuthors(IEnumerable<int> authorIds) => new List<Author> { Author };
            public Author AddAuthor(Author newAuthor, bool doRefresh) => throw new NotImplementedException();
            public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh) => throw new NotImplementedException();
            public Author FindByProviderId(string provider, string providerId) => throw new NotImplementedException();
            public Author FindByName(string title) => throw new NotImplementedException();
            public Author FindByNameInexact(string title) => throw new NotImplementedException();
            public List<Author> GetCandidates(string title) => throw new NotImplementedException();
            public List<Author> GetReportCandidates(string reportTitle) => throw new NotImplementedException();
            public void DeleteAuthor(int authorId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
            public List<Author> GetAllAuthors(bool bypassCache = false) => throw new NotImplementedException();
            public Dictionary<int, List<int>> GetAllAuthorTags() => throw new NotImplementedException();
            public List<Author> AllForTag(int tagId) => throw new NotImplementedException();
            public Author UpdateAuthor(Author author) => Author = author;
            public Author UpdateAuthorProgressiveSettings(Author author, int? audiobookQualityProfileId, int? audiobookMetadataProfileId, int? audiobookMonitorExisting, bool? audiobookMonitorFuture, int? ebookQualityProfileId, int? ebookMetadataProfileId, int? ebookMonitorExisting, bool? ebookMonitorFuture, string rootFolderPath) => throw new NotImplementedException();
            public List<Author> UpdateAuthors(List<Author> authors, bool useExistingRelativeFolder) => throw new NotImplementedException();
            public Dictionary<int, string> AllAuthorPaths() => throw new NotImplementedException();
            public bool AuthorPathExists(string folder) => throw new NotImplementedException();
            public void RemoveAddOptions(Author author) => throw new NotImplementedException();
            public void SetMediaTypeMonitoring(int authorId, string mediaType, bool monitored) => throw new NotImplementedException();
            public long GetAuthorSizeForMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public void UpdateLastSelectedMediaType(int authorId, string mediaType) => throw new NotImplementedException();
            public List<Book> GetAuthorBooksFromCache(int authorId) => throw new NotImplementedException();
            public List<int> GetAuthorIdsByMetadataProfileId(int metadataProfileId) => throw new NotImplementedException();
            public void ClearAuthorCache() => throw new NotImplementedException();
        }

        private sealed class StubMediaFileService : IMediaFileService
        {
            public List<BookFile> Files { get; set; } = new();
            public List<BookFile> Updated { get; } = new();

            public List<BookFile> GetFilesByAuthor(int authorId) => Files;
            public List<BookFile> Get(IEnumerable<int> ids)
            {
                var requested = ids.ToHashSet();
                return Files.Where(f => requested.Contains(f.Id)).ToList();
            }

            public void Update(BookFile bookFile) => Updated.Add(bookFile);
            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<IFileInfo> FilterUnchangedFiles(List<IFileInfo> files, FilterFilesType filter) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => Files
                .Where(file => (file.Edition?.BookId ?? file.Edition?.Book?.Id ?? 0) == bookId)
                .ToList();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private sealed class RecordingMoveBookFiles : IMoveBookFiles
        {
            public List<int> MovedFileIds { get; } = new();
            public Func<BookFile, string> DestinationFactory { get; set; }
            public Func<BookFile, bool, BookFileMovePlan> PlanFactory { get; set; }
            public List<bool> CanonicalRequests { get; } = new();
            public List<BookFileMovePlan> ExecutedPlans { get; } = new();
            public DiskProviderProxy Disk { get; set; }

            public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null)
            {
                CanonicalRequests.Add(moveToCanonicalAuthorFolder);
                return PlanFactory?.Invoke(bookFile, moveToCanonicalAuthorFolder) ?? new BookFileMovePlan
                {
                    CanOrganize = true,
                    SourceAuthorFolderPath = "/books/Joe",
                    DestinationAuthorFolderPath = "/books/Joe",
                    DestinationPath = DestinationFactory?.Invoke(bookFile) ?? bookFile.Path
                };
            }

            public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null)
            {
                MovedFileIds.Add(bookFile.Id);
                ExecutedPlans.Add(plan);
                var destination = plan.DestinationPath;
                if (destination != null)
                {
                    Disk?.MoveFileTo(bookFile.Path, destination, "MoveBookFile");
                    bookFile.Path = destination;
                }

                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, NzbDrone.Core.Parser.Model.LocalBook localBook) => throw new NotImplementedException();
            public BookFile CopyBookFile(BookFile bookFile, NzbDrone.Core.Parser.Model.LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, NzbDrone.Core.Parser.Model.LocalBook localBook) => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private sealed class NullRootFolderWatchingService : IRootFolderWatchingService
        {
            public void ReportFileSystemChangeBeginning(params string[] paths)
            {
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFolders { get; } = new(PathEqualityComparer.Instance);
            public HashSet<string> ExistingFiles { get; } = new(PathEqualityComparer.Instance);
            public List<string> RemovedEmptySubfolders { get; } = new();
            public List<string> DeletedFolders { get; } = new();
            public List<string> Operations { get; } = new();
            public bool FailFolderMoves { get; set; }

            public void AddFile(string path)
            {
                ExistingFiles.Add(path);
                for (var folder = Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                {
                    ExistingFolders.Add(folder);
                }
            }

            public void MoveFileTo(string source, string destination, string operation)
            {
                Operations.Add($"{operation} {Path.GetFileName(source)}");
                if (ExistingFiles.Remove(source))
                {
                    AddFile(destination);
                }
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FolderExists))
                {
                    return ExistingFolders.Contains((string)args[0]);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    return ExistingFiles.Contains((string)args[0]);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetDirectories))
                {
                    var parent = (string)args[0];
                    return ExistingFolders.Where(f => Path.GetDirectoryName(f).PathEquals(parent)).ToList();
                }

                if (targetMethod?.Name == nameof(IDiskProvider.CreateFolder))
                {
                    var folder = (string)args[0];
                    Operations.Add($"CreateFolder {folder}");
                    for (; !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                    {
                        ExistingFolders.Add(folder);
                    }

                    return null;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.MoveFile))
                {
                    var destination = (string)args[1];
                    if (ExistingFiles.Contains(destination))
                    {
                        throw new FileAlreadyExistsException("File already exists", destination);
                    }

                    MoveFileTo((string)args[0], destination, "MoveFile");
                    return null;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.MoveFolder))
                {
                    var source = (string)args[0];
                    var destination = (string)args[1];
                    if (FailFolderMoves)
                    {
                        throw new IOException("Cross-device link");
                    }

                    Operations.Add($"MoveFolder {source} -> {destination}");
                    foreach (var file in ExistingFiles.Where(f => source.IsParentPath(f)).ToList())
                    {
                        ExistingFiles.Remove(file);
                        AddFile(Path.Combine(destination, Path.GetRelativePath(source, file)));
                    }

                    ExistingFolders.RemoveWhere(f => f.PathEquals(source) || source.IsParentPath(f));
                    ExistingFolders.Add(destination);
                    return null;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.RemoveEmptySubfolders))
                {
                    RemovedEmptySubfolders.Add((string)args[0]);
                    return null;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetFiles))
                {
                    var folder = (string)args[0];
                    var recursive = (bool)args[1];
                    return ExistingFiles
                        .Where(f => recursive ? folder.IsParentPath(f) : Path.GetDirectoryName(f).PathEquals(folder))
                        .ToList();
                }

                if (targetMethod?.Name == nameof(IDiskProvider.DeleteFolder))
                {
                    var folder = (string)args[0];
                    DeletedFolders.Add(folder);
                    ExistingFolders.Remove(folder);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private static BookFile BookFile(int id, string path, Quality quality, string mediaType)
        {
            return new BookFile
            {
                Id = id,
                Path = path,
                EditionId = 10,
                Quality = new QualityModel(quality),
                MediaType = mediaType,
                CalibreId = 0
            };
        }

        private static RenameBookFileService CreateService(
            Author author,
            StubMediaFileService mediaFileService,
            RecordingMoveBookFiles mover,
            RecordingEventAggregator eventAggregator,
            IDiskProvider diskProvider = null)
        {
            return new RenameBookFileService(
                new StubAuthorService { Author = author },
                mediaFileService,
                mover,
                eventAggregator,
                diskProvider ?? DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                new NullRootFolderWatchingService(),
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_not_publish_rename_events_when_mover_leaves_path_unchanged()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var mediaFileService = new StubMediaFileService
            {
                Files = new List<BookFile>
                {
                    BookFile(1, "/books/Joe/file.epub", Quality.EPUB, "ebook")
                }
            };

            var eventAggregator = new RecordingEventAggregator();
            var mover = new RecordingMoveBookFiles();
            var service = CreateService(author, mediaFileService, mover, eventAggregator);

            service.Execute(new RenameFilesCommand(author.Id, new List<int> { 1 }));

            Assert.That(mediaFileService.Updated.Select(f => f.Id), Is.EqualTo(new[] { 1 }));
            Assert.That(eventAggregator.Events.OfType<BookFileRenamedEvent>(), Is.Empty);
            Assert.That(eventAggregator.Events.OfType<AuthorRenamedEvent>(), Is.Empty);
        }

        [Test]
        public void should_scope_bulk_rename_to_requested_media_type()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var mediaFileService = new StubMediaFileService
            {
                Files = new List<BookFile>
                {
                    BookFile(1, "/books/Joe/audio.mp3", Quality.MP3, "audiobook"),
                    BookFile(2, "/books/Joe/book.epub", Quality.EPUB, "ebook")
                }
            };

            var eventAggregator = new RecordingEventAggregator();
            var mover = new RecordingMoveBookFiles
            {
                DestinationFactory = file => file.Path + ".renamed"
            };

            var service = CreateService(author, mediaFileService, mover, eventAggregator);

            service.Execute(new RenameAuthorCommand
            {
                AuthorIds = new List<int> { author.Id },
                MediaType = "ebook"
            });

            Assert.That(mover.MovedFileIds, Is.EqualTo(new[] { 2 }));
            Assert.That(eventAggregator.Events.OfType<BookFileRenamedEvent>().Single().BookFile.Id, Is.EqualTo(2));
            Assert.That(eventAggregator.Events.OfType<AuthorRenamedEvent>().Single().RenamedFiles.Single().BookFile.Id, Is.EqualTo(2));
        }

        [Test]
        public void should_keep_unprovable_preview_row_visible_but_disabled()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var file = BookFile(1, "/books/file.epub", Quality.EPUB, "ebook");
            var mediaFileService = new StubMediaFileService { Files = new List<BookFile> { file } };
            var mover = new RecordingMoveBookFiles
            {
                PlanFactory = (_, _) => BookFileMovePlan.Skipped("The current author folder cannot be determined.")
            };
            var service = CreateService(author, mediaFileService, mover, new RecordingEventAggregator());

            var preview = service.GetRenamePreviews(author.Id).Single();

            Assert.That(preview.BookFileId, Is.EqualTo(file.Id));
            Assert.That(preview.CanOrganize, Is.False);
            Assert.That(preview.Reason, Is.EqualTo("The current author folder cannot be determined."));
            Assert.That(preview.NewPath, Is.EqualTo(file.Path));
        }

        [Test]
        public void should_expose_real_book_and_edition_ids_and_keep_book_preview_track_in_place()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var file = BookFile(1, "/books/Joe/Book/file.epub", Quality.EPUB, "ebook");
            file.EditionId = 22;
            file.Edition = new Edition
            {
                Id = 22,
                BookId = 21,
                Book = new Book { Id = 21, AuthorId = author.Id }
            };
            var mediaFileService = new StubMediaFileService { Files = new List<BookFile> { file } };
            var mover = new RecordingMoveBookFiles
            {
                PlanFactory = (_, canonical) => new BookFileMovePlan
                {
                    CanOrganize = true,
                    DestinationPath = canonical
                        ? "/books/Joe Abercrombie/Book/file.epub"
                        : "/books/Joe/Book/organized.epub"
                }
            };
            var service = CreateService(author, mediaFileService, mover, new RecordingEventAggregator());

            var preview = service.GetRenamePreviews(author.Id, bookId: 21).Single();

            Assert.That(preview.BookId, Is.EqualTo(21));
            Assert.That(preview.EditionId, Is.EqualTo(22));
            Assert.That(preview.NewPath, Is.EqualTo("/books/Joe/Book/organized.epub"));
            Assert.That(mover.CanonicalRequests, Is.EqualTo(new[] { false }));
        }

        [Test]
        public void should_request_canonical_destination_for_author_preview_only_when_opted_in()
        {
            var author = new Author { Id = 1, Name = "Joe Abercrombie" };
            var file = BookFile(1, "/books/Joe/Book/file.epub", Quality.EPUB, "ebook");
            var mediaFileService = new StubMediaFileService { Files = new List<BookFile> { file } };
            var mover = new RecordingMoveBookFiles
            {
                PlanFactory = (bookFile, canonical) => new BookFileMovePlan
                {
                    CanOrganize = true,
                    DestinationPath = canonical
                        ? "/books/Joe Abercrombie/Book/file.epub"
                        : "/books/Joe/Book/renamed.epub"
                }
            };
            var service = CreateService(author, mediaFileService, mover, new RecordingEventAggregator());

            var keepPreview = service.GetRenamePreviews(author.Id, "ebook").Single();
            var canonicalPreview = service.GetRenamePreviews(author.Id, "ebook", true).Single();

            Assert.That(keepPreview.NewPath, Is.EqualTo("/books/Joe/Book/renamed.epub"));
            Assert.That(canonicalPreview.NewPath, Is.EqualTo("/books/Joe Abercrombie/Book/file.epub"));
            Assert.That(mover.CanonicalRequests, Is.EqualTo(new[] { false, true }));
        }

        [Test]
        public void should_execute_the_exact_precomputed_plan_and_update_applicable_stored_paths()
        {
            var sourceAuthorFolder = "/books/George R. R. Martin";
            var canonicalAuthorFolder = "/books/George R.R. Martin";
            var author = new Author
            {
                Id = 1,
                Name = "George R.R. Martin",
                Path = sourceAuthorFolder,
                AudiobookPath = sourceAuthorFolder,
                EbookPath = "/ebooks/George R.R. Martin"
            };
            var file = BookFile(1, sourceAuthorFolder + "/Wild Cards/file.mp3", Quality.MP3, "audiobook");
            var mediaFileService = new StubMediaFileService { Files = new List<BookFile> { file } };
            var plan = new BookFileMovePlan
            {
                CanOrganize = true,
                SourceAuthorFolderPath = sourceAuthorFolder,
                DestinationAuthorFolderPath = canonicalAuthorFolder,
                DestinationPath = Path.Combine(canonicalAuthorFolder, "Wild Cards", "file.mp3"),
                ShouldUpdateStoredAuthorPath = true
            };
            var mover = new RecordingMoveBookFiles { PlanFactory = (_, _) => plan };
            var service = CreateService(author, mediaFileService, mover, new RecordingEventAggregator());

            service.Execute(new RenameFilesCommand(author.Id, new List<int> { file.Id }, true));

            Assert.That(mover.ExecutedPlans.Single(), Is.SameAs(plan));
            Assert.That(file.Path, Is.EqualTo(plan.DestinationPath));
            Assert.That(mediaFileService.Updated.Single(), Is.SameAs(file));
            Assert.That(mediaFileService.Updated.Single().Path, Is.EqualTo(plan.DestinationPath));
            Assert.That(author.AudiobookPath, Is.EqualTo(canonicalAuthorFolder));
            Assert.That(author.Path, Is.EqualTo(canonicalAuthorFolder));
            Assert.That(author.EbookPath, Is.EqualTo("/ebooks/George R.R. Martin"));
        }

        [Test]
        public void should_not_update_ebook_path_when_colocation_overrides_canonical_request()
        {
            var sourceAuthorFolder = "/mixed/George R. R. Martin";
            var author = new Author
            {
                Id = 1,
                Name = "George R.R. Martin",
                Path = sourceAuthorFolder,
                EbookPath = sourceAuthorFolder
            };
            var file = BookFile(1, sourceAuthorFolder + "/Wild Cards/original.epub", Quality.EPUB, "ebook");
            var mediaFileService = new StubMediaFileService { Files = new List<BookFile> { file } };
            var plan = new BookFileMovePlan
            {
                CanOrganize = true,
                SourceAuthorFolderPath = sourceAuthorFolder,
                DestinationAuthorFolderPath = sourceAuthorFolder,
                DestinationPath = sourceAuthorFolder + "/Wild Cards/file.epub",
                ShouldUpdateStoredAuthorPath = false
            };
            var mover = new RecordingMoveBookFiles { PlanFactory = (_, _) => plan };
            var service = CreateService(author, mediaFileService, mover, new RecordingEventAggregator());

            service.Execute(new RenameFilesCommand(author.Id, new List<int> { file.Id }, true));

            Assert.That(mover.CanonicalRequests, Is.EqualTo(new[] { true }));
            Assert.That(file.Path, Is.EqualTo(plan.DestinationPath));
            Assert.That(author.EbookPath, Is.EqualTo(sourceAuthorFolder));
            Assert.That(author.Path, Is.EqualTo(sourceAuthorFolder));
        }

        [Test]
        public void should_remove_only_empty_directories_bounded_by_the_actual_source_author_folder()
        {
            var rootFolder = @"C:\books".AsOsAgnostic();
            var sourceAuthorFolder = Path.Combine(rootFolder, "George R. R. Martin");
            var sourceBookFolder = Path.Combine(sourceAuthorFolder, "Wild Cards");
            var canonicalAuthorFolder = Path.Combine(rootFolder, "George R.R. Martin");
            var author = new Author
            {
                Id = 1,
                Name = "George R.R. Martin",
                Path = sourceAuthorFolder,
                AudiobookPath = sourceAuthorFolder
            };
            var file = BookFile(1, Path.Combine(sourceBookFolder, "file.mp3"), Quality.MP3, "audiobook");
            var mediaFileService = new StubMediaFileService { Files = new List<BookFile> { file } };
            var plan = new BookFileMovePlan
            {
                CanOrganize = true,
                SourceAuthorFolderPath = sourceAuthorFolder,
                DestinationAuthorFolderPath = canonicalAuthorFolder,
                DestinationPath = Path.Combine(canonicalAuthorFolder, "Wild Cards", "file.mp3"),
                ShouldUpdateStoredAuthorPath = true
            };
            var mover = new RecordingMoveBookFiles { PlanFactory = (_, _) => plan };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var diskProxy = (DiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFolders.Add(sourceAuthorFolder);
            diskProxy.ExistingFolders.Add(sourceBookFolder);
            var service = CreateService(author, mediaFileService, mover, new RecordingEventAggregator(), diskProvider);

            service.Execute(new RenameFilesCommand(author.Id, new List<int> { file.Id }, true));

            Assert.That(diskProxy.RemovedEmptySubfolders, Is.EqualTo(new[] { sourceBookFolder, sourceAuthorFolder }));
            Assert.That(diskProxy.DeletedFolders, Is.EqualTo(new[] { sourceBookFolder, sourceAuthorFolder }));
            Assert.That(diskProxy.DeletedFolders, Does.Not.Contain(rootFolder));
        }

        private const string HobbFolder = "/audiobooks/Robin Hobb";
        private const string OldBookFolder = HobbFolder + "/L'Assassin Royal/L'apprenti assassin";
        private const string NewBookFolder = HobbFolder + "/L'Assassin royal/01 - L'apprenti assassin";

        private static (RenameBookFileService Service, DiskProviderProxy Disk, RecordingMoveBookFiles Mover, RecordingEventAggregator Events, StubMediaFileService Files)
            CreateAudiobookFolderScenario(List<BookFile> files, Func<BookFile, string> destinationFactory)
        {
            var author = new Author { Id = 1, Name = "Robin Hobb", Path = HobbFolder, AudiobookPath = HobbFolder };
            var mediaFileService = new StubMediaFileService { Files = files };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            foreach (var file in files)
            {
                disk.AddFile(file.Path);
            }

            var mover = new RecordingMoveBookFiles
            {
                Disk = disk,
                PlanFactory = (file, _) => new BookFileMovePlan
                {
                    CanOrganize = true,
                    SourceAuthorFolderPath = HobbFolder,
                    DestinationAuthorFolderPath = HobbFolder,
                    DestinationPath = destinationFactory(file)
                }
            };
            var events = new RecordingEventAggregator();

            return (CreateService(author, mediaFileService, mover, events, diskProvider), disk, mover, events, mediaFileService);
        }

        private static List<BookFile> TwoTrackAudiobook()
        {
            return new List<BookFile>
            {
                BookFile(1, OldBookFolder + "/01 Prologue.mp3", Quality.MP3, "audiobook"),
                BookFile(2, OldBookFolder + "/02 Le nouveau.mp3", Quality.MP3, "audiobook")
            };
        }

        [Test]
        public void should_move_the_whole_audiobook_folder_so_untracked_sidecars_follow()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(NewBookFolder, Path.GetFileName(f.Path)));
            scenario.Disk.AddFile(OldBookFolder + "/cover.jpg");
            scenario.Disk.AddFile(OldBookFolder + "/metadata.json");

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1, 2 }));

            Assert.That(scenario.Disk.Operations, Is.EqualTo(new[] { $"CreateFolder {HobbFolder}/L'Assassin royal", $"MoveFolder {OldBookFolder} -> {NewBookFolder}" }));
            Assert.That(scenario.Mover.MovedFileIds, Is.Empty);
            Assert.That(files.Select(f => f.Path), Is.EqualTo(new[] { NewBookFolder + "/01 Prologue.mp3", NewBookFolder + "/02 Le nouveau.mp3" }));
            Assert.That(scenario.Disk.ExistingFiles, Does.Contain(NewBookFolder + "/cover.jpg"));
            Assert.That(scenario.Disk.ExistingFiles, Does.Contain(NewBookFolder + "/metadata.json"));
            Assert.That(scenario.Files.Updated.Select(f => f.Id), Is.EqualTo(new[] { 1, 2 }));

            var renamed = scenario.Events.Events.OfType<AuthorRenamedEvent>().Single().RenamedFiles;
            Assert.That(renamed.Select(r => r.PreviousPath), Is.EqualTo(new[] { OldBookFolder + "/01 Prologue.mp3", OldBookFolder + "/02 Le nouveau.mp3" }));
            Assert.That(scenario.Events.Events.OfType<BookFileRenamedEvent>().Count(), Is.EqualTo(2));
        }

        [Test]
        public void should_still_rename_a_file_whose_name_changes_inside_the_moved_folder()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(NewBookFolder, f.Id == 1 ? "01 - Prologue.mp3" : Path.GetFileName(f.Path)));

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1, 2 }));

            Assert.That(scenario.Disk.Operations, Does.Contain($"MoveFolder {OldBookFolder} -> {NewBookFolder}"));
            Assert.That(scenario.Mover.MovedFileIds, Is.EqualTo(new[] { 1 }));
            Assert.That(files[0].Path, Is.EqualTo(NewBookFolder + "/01 - Prologue.mp3"));
            Assert.That(files[1].Path, Is.EqualTo(NewBookFolder + "/02 Le nouveau.mp3"));
            Assert.That(scenario.Events.Events.OfType<AuthorRenamedEvent>().Single().RenamedFiles.Single(r => r.BookFile.Id == 1).PreviousPath,
                Is.EqualTo(OldBookFolder + "/01 Prologue.mp3"));
        }

        [Test]
        public void should_move_sidecars_before_audio_when_destination_folder_already_exists()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(NewBookFolder, Path.GetFileName(f.Path)));
            scenario.Disk.AddFile(OldBookFolder + "/cover.jpg");
            scenario.Disk.AddFile(OldBookFolder + "/metadata.json");
            scenario.Disk.AddFile(NewBookFolder + "/metadata.json");

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1, 2 }));

            Assert.That(scenario.Disk.Operations, Is.EqualTo(new[]
            {
                "MoveFile cover.jpg",
                "MoveBookFile 01 Prologue.mp3",
                "MoveBookFile 02 Le nouveau.mp3"
            }));
            Assert.That(scenario.Disk.ExistingFiles, Does.Contain(NewBookFolder + "/cover.jpg"));
            Assert.That(scenario.Disk.ExistingFiles, Does.Contain(OldBookFolder + "/metadata.json"), "an existing destination file is never overwritten");
        }

        [Test]
        public void should_leave_the_folder_alone_when_another_book_file_stays_behind()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(NewBookFolder, Path.GetFileName(f.Path)));
            scenario.Disk.AddFile(OldBookFolder + "/cover.jpg");

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1 }));

            Assert.That(scenario.Disk.Operations, Is.EqualTo(new[] { "MoveBookFile 01 Prologue.mp3" }));
            Assert.That(scenario.Disk.ExistingFiles, Does.Contain(OldBookFolder + "/cover.jpg"));
        }

        [Test]
        public void should_leave_the_folder_alone_when_it_holds_untracked_media()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(NewBookFolder, Path.GetFileName(f.Path)));
            scenario.Disk.AddFile(OldBookFolder + "/bonus.mp3");
            scenario.Disk.AddFile(OldBookFolder + "/cover.jpg");

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1, 2 }));

            Assert.That(scenario.Mover.MovedFileIds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(scenario.Disk.Operations, Has.None.StartsWith("MoveFolder").And.None.StartsWith("MoveFile "));
        }

        [Test]
        public void should_leave_the_folder_alone_when_files_split_across_destinations()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(f.Id == 1 ? NewBookFolder : NewBookFolder + " (2)", Path.GetFileName(f.Path)));
            scenario.Disk.AddFile(OldBookFolder + "/cover.jpg");

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1, 2 }));

            Assert.That(scenario.Mover.MovedFileIds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(scenario.Disk.ExistingFiles, Does.Contain(OldBookFolder + "/cover.jpg"));
        }

        [Test]
        public void should_fall_back_to_moving_files_one_by_one_when_the_folder_move_fails()
        {
            var files = TwoTrackAudiobook();
            var scenario = CreateAudiobookFolderScenario(files, f => Path.Combine(NewBookFolder, Path.GetFileName(f.Path)));
            scenario.Disk.FailFolderMoves = true;

            scenario.Service.Execute(new RenameFilesCommand(1, new List<int> { 1, 2 }));

            Assert.That(scenario.Mover.MovedFileIds, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(files.Select(f => f.Path), Is.EqualTo(new[] { NewBookFolder + "/01 Prologue.mp3", NewBookFolder + "/02 Le nouveau.mp3" }));
        }

        [Test]
        public void should_report_boundary_skips_separately()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 2,
                AttemptedCount = 1,
                RenamedCount = 1,
                BoundarySkippedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 1 of 2 files for Joe Abercrombie; 1 skipped because the current author folder could not be determined."));
        }

        [Test]
        public void should_format_all_success_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 10,
                AttemptedCount = 10,
                RenamedCount = 10
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 10 of 10 files for Joe Abercrombie."));
        }

        [Test]
        public void should_format_partial_collision_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 10,
                AttemptedCount = 10,
                RenamedCount = 5,
                CollisionSkippedCount = 5
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 5 of 10 files for Joe Abercrombie; 5 skipped because destination already exists."));
        }

        [Test]
        public void should_format_singular_collision_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 1,
                AttemptedCount = 1,
                CollisionSkippedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 0 of 1 file for Joe Abercrombie; 1 skipped because destination already exists."));
        }

        [Test]
        public void should_format_already_in_place_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 2,
                AttemptedCount = 2,
                AlreadyInPlaceCount = 2
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 0 of 2 files for Joe Abercrombie; 2 already in place."));
        }

        [Test]
        public void should_format_singular_already_in_place_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 1,
                AttemptedCount = 1,
                AlreadyInPlaceCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 0 of 1 file for Joe Abercrombie; 1 already in place."));
        }

        [Test]
        public void should_format_failed_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 1,
                AttemptedCount = 1,
                FailedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 0 of 1 file for Joe Abercrombie; 1 failed."));
        }

        [Test]
        public void should_format_not_eligible_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 10,
                AttemptedCount = 9,
                RenamedCount = 4,
                CollisionSkippedCount = 5
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 4 of 10 files for Joe Abercrombie; 5 skipped because destination already exists; 1 not eligible for organize."));
        }

        [Test]
        public void should_format_mixed_result_message()
        {
            var result = new RenameFilesResult
            {
                SelectedCount = 11,
                AttemptedCount = 11,
                RenamedCount = 5,
                CollisionSkippedCount = 3,
                AlreadyInPlaceCount = 2,
                FailedCount = 1
            };

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("Organized 5 of 11 files for Joe Abercrombie; 3 skipped because destination already exists; 2 already in place; 1 failed."));
        }

        [Test]
        public void should_format_zero_selected_message()
        {
            var result = new RenameFilesResult();

            var message = RenameBookFileService.FormatRenameResultMessage(result, "Joe Abercrombie");

            Assert.That(message, Is.EqualTo("No files selected to organize for Joe Abercrombie."));
        }
    }
}
