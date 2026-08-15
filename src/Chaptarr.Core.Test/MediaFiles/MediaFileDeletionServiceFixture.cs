using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileDeletionServiceFixture
    {
        private sealed class RecordingRecycleBinProvider : IRecycleBinProvider
        {
            public List<string> DeletedFolders { get; } = new();
            public List<string> DeletedFiles { get; } = new();
            public List<string> DeletedFileSubfolders { get; } = new();

            public void DeleteFolder(string path)
            {
                DeletedFolders.Add(path);
            }

            public void DeleteFile(string path, string subfolder = "")
            {
                DeletedFiles.Add(path);
                DeletedFileSubfolders.Add(subfolder);
            }
            public void Empty() => throw new NotImplementedException();
            public void Cleanup() => throw new NotImplementedException();
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(params RootFolder[] rootFolders)
            {
                _rootFolders = rootFolders?.Where(r => r != null).ToList() ?? new List<RootFolder>();
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => throw new NotImplementedException();
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();

            public RootFolder GetBestRootFolder(string path)
            {
                return GetBestRootFolder(path, _rootFolders);
            }

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return allRootFolders
                    .Where(r => r.Path.PathEquals(path) || r.Path.IsParentPath(path))
                    .OrderByDescending(r => r.Path?.Length ?? 0)
                    .FirstOrDefault();
            }

            public string GetBestRootFolderPath(string path) => throw new NotImplementedException();
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => throw new NotImplementedException();
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class FileExistsOnlyDiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFiles { get; } = new(PathEqualityComparer.Instance);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (string.Equals(targetMethod?.Name, nameof(IDiskProvider.FileExists), StringComparison.Ordinal) &&
                    args?.Length == 1 &&
                    args[0] is string filePath)
                {
                    return ExistingFiles.Contains(filePath);
                }

                if (string.Equals(targetMethod?.Name, nameof(IDiskProvider.FileExistsCanonical), StringComparison.Ordinal) &&
                    args?.Length == 1 &&
                    args[0] is string canonicalFilePath)
                {
                    return ExistingFiles.Contains(canonicalFilePath);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class StaleTrackedPathDiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    return true;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.FileExistsCanonical))
                {
                    return false;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class RecordingMediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Deleted { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.Delete))
                {
                    Deleted.Add((BookFile)args[0]);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod?.Name}");
            }
        }

        /// <summary>
        /// Records the folder operations the cleanup performs. RemoveEmptySubfolders only removes
        /// CHILDREN of the folder it is handed, so which folder it is called on is the whole point.
        /// </summary>
        private class FolderCleanupDiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFolders { get; } = new(PathEqualityComparer.Instance);
            public HashSet<string> ExistingFiles { get; } = new(PathEqualityComparer.Instance);
            public HashSet<string> FoldersWithFiles { get; } = new(PathEqualityComparer.Instance);
            public List<string> SubfolderCleanups { get; } = new();
            public List<string> DeletedFolders { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FolderExists):
                        return ExistingFolders.Contains((string)args[0]);

                    case nameof(IDiskProvider.RemoveEmptySubfolders):
                        SubfolderCleanups.Add((string)args[0]);
                        return null;

                    case nameof(IDiskProvider.GetFiles):
                        return FoldersWithFiles.Contains((string)args[0])
                            ? new[] { Path.Combine((string)args[0], "something.m4b") }
                            : Array.Empty<string>();

                    case nameof(IDiskProvider.DeleteFolder):
                        DeletedFolders.Add((string)args[0]);
                        return null;

                    case nameof(IDiskProvider.FileExists):
                    case nameof(IDiskProvider.FileExistsCanonical):
                        return ExistingFiles.Contains((string)args[0]);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class DeleteEmptyFoldersConfigProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_DeleteEmptyFolders")
                {
                    return true;
                }

                throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }

        /// <summary>
        /// Drives <see cref="MediaFileDeletionService.DeleteTrackFile(Author, BookFile)"/>. Routing is decided
        /// by which configured root actually contains the file, so the interesting knobs are the configured
        /// roots, which folders exist, and which roots have any children.
        /// </summary>
        private class TrackFileDeleteDiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFolders { get; } = new(PathEqualityComparer.Instance);
            public HashSet<string> ExistingFiles { get; } = new(PathEqualityComparer.Instance);
            public HashSet<string> FoldersWithSubfolders { get; } = new(PathEqualityComparer.Instance);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FolderExists):
                        return ExistingFolders.Contains((string)args[0]);

                    case nameof(IDiskProvider.GetDirectories):
                        return FoldersWithSubfolders.Contains((string)args[0])
                            ? new[] { Path.Combine(((string)args[0]).TrimEnd(Path.DirectorySeparatorChar), "Some Author") }
                            : Array.Empty<string>();

                    case nameof(IDiskProvider.GetParentFolder):
                        return Path.GetDirectoryName(((string)args[0]).TrimEnd(Path.DirectorySeparatorChar));

                    case nameof(IDiskProvider.FileExists):
                    case nameof(IDiskProvider.FileExistsCanonical):
                        return ExistingFiles.Contains((string)args[0]);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private sealed class TrackFileDeleteSubject
        {
            public MediaFileDeletionService Service { get; init; }
            public TrackFileDeleteDiskProviderProxy Disk { get; init; }
            public RecordingRecycleBinProvider RecycleBin { get; init; }
            public RecordingMediaFileServiceProxy MediaFiles { get; init; }
            public RecordingEventAggregator Events { get; init; }
        }

        private static TrackFileDeleteSubject BuildTrackFileDeleteSubject(params RootFolder[] rootFolders)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, TrackFileDeleteDiskProviderProxy>();
            var diskProxy = (TrackFileDeleteDiskProviderProxy)(object)diskProvider;
            var mediaFileService = DispatchProxy.Create<IMediaFileService, RecordingMediaFileServiceProxy>();
            var recycleBinProvider = new RecordingRecycleBinProvider();
            var eventAggregator = new RecordingEventAggregator();

            // Every configured root exists and has children unless a test says otherwise.
            foreach (var root in rootFolders)
            {
                diskProxy.ExistingFolders.Add(root.Path);
                diskProxy.FoldersWithSubfolders.Add(root.Path);
            }

            var service = new MediaFileDeletionService(
                diskProvider,
                recycleBinProvider,
                mediaFileService,
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                eventAggregator,
                new StubRootFolderService(rootFolders),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            return new TrackFileDeleteSubject
            {
                Service = service,
                Disk = diskProxy,
                RecycleBin = recycleBinProvider,
                MediaFiles = (RecordingMediaFileServiceProxy)(object)mediaFileService,
                Events = eventAggregator
            };
        }

        private static (MediaFileDeletionService Service, FolderCleanupDiskProviderProxy Disk) BuildFolderCleanupSubject(params string[] authorRoots)
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, FolderCleanupDiskProviderProxy>();
            var diskProxy = (FolderCleanupDiskProviderProxy)(object)diskProvider;

            foreach (var root in authorRoots)
            {
                diskProxy.ExistingFolders.Add(root);
            }

            var service = new MediaFileDeletionService(
                diskProvider,
                new RecordingRecycleBinProvider(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, DeleteEmptyFoldersConfigProxy>(),
                new RecordingEventAggregator(),
                new StubRootFolderService(),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            return (service, diskProxy);
        }

        [Test]
        public void should_delete_every_file_and_replica_and_remove_the_folder_on_whole_book_delete()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Jim Butcher",
                Path = "/audiobooks/Jim Butcher",
                AudiobookPath = "/audiobooks/Jim Butcher",
                EbookPath = "/ebooks/Jim Butcher"
            };

            var bookFolder = "/ebooks/Jim Butcher/Captains Fury";
            var colocatedFolder = "/audiobooks/Jim Butcher/Captains Fury";

            var diskProvider = DispatchProxy.Create<IDiskProvider, FolderCleanupDiskProviderProxy>();
            var diskProxy = (FolderCleanupDiskProviderProxy)(object)diskProvider;
            diskProxy.ExistingFolders.Add(author.EbookPath);
            diskProxy.ExistingFolders.Add(author.AudiobookPath);
            diskProxy.ExistingFolders.Add(bookFolder);
            diskProxy.ExistingFolders.Add(colocatedFolder);
            diskProxy.ExistingFiles.Add(bookFolder + "/Captains Fury.epub");
            diskProxy.ExistingFiles.Add(colocatedFolder + "/Captains Fury.epub");

            var recycleBinProvider = new RecordingRecycleBinProvider();

            var service = new MediaFileDeletionService(
                diskProvider,
                recycleBinProvider,

                // The row purge for this same event runs concurrently, so a query here can come back
                // empty and silently leave the files on disk. The snapshot must be used instead.
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, DeleteEmptyFoldersConfigProxy>(),
                new RecordingEventAggregator(),
                new StubRootFolderService(new RootFolder { Id = 1, Path = "/ebooks" }, new RootFolder { Id = 2, Path = "/audiobooks" }),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            var book = new Book
            {
                Id = 5792,
                AuthorId = author.Id,
                Author = author,
                Title = "Captain's Fury",
                MediaType = BookMediaType.Ebook,
                BookFiles = new List<BookFile>
                {
                    new()
                    {
                        Id = 1,
                        Path = bookFolder + "/Captains Fury.epub",
                        Author = author,
                        ReplicaPaths = new List<string> { colocatedFolder + "/Captains Fury.epub" }
                    }
                }
            };

            service.HandleAsync(new BookDeletedEvent(book, deleteFiles: true, addImportListExclusion: false));

            Assert.That(recycleBinProvider.DeletedFiles, Does.Contain(bookFolder + "/Captains Fury.epub"));
            Assert.That(recycleBinProvider.DeletedFiles, Does.Contain(colocatedFolder + "/Captains Fury.epub"),
                "colocated replicas are only cleaned from the per-file event, which whole-book deletion never publishes");

            // Both roots are swept, so the emptied book folders are candidates for removal.
            Assert.That(diskProxy.SubfolderCleanups, Does.Contain(author.EbookPath));
            Assert.That(diskProxy.SubfolderCleanups, Does.Contain(author.AudiobookPath));
            Assert.That(diskProxy.DeletedFolders, Does.Contain(author.EbookPath));
        }

        [Test]
        public void should_clean_the_book_folder_from_its_parent_so_it_can_actually_be_removed()
        {
            // The emptied book folder can only be deleted as a CHILD of the author folder; cleaning
            // the book folder itself leaves it standing forever.
            var author = new Author
            {
                Id = 1,
                Name = "Jim Butcher",
                Path = "/audiobooks/Jim Butcher",
                AudiobookPath = "/audiobooks/Jim Butcher"
            };

            var (service, disk) = BuildFolderCleanupSubject(author.AudiobookPath);
            var bookFolder = "/audiobooks/Jim Butcher/Captains Fury";
            disk.ExistingFolders.Add(bookFolder);
            disk.FoldersWithFiles.Add(author.AudiobookPath);

            service.Handle(new BookFileDeletedEvent(
                new BookFile { Id = 1, Path = bookFolder + "/Captains Fury.m4b", Author = author },
                DeleteMediaFileReason.Manual));

            Assert.That(disk.SubfolderCleanups, Does.Contain(author.AudiobookPath),
                "the author folder must be swept, otherwise the emptied book folder is never a candidate");
        }

        [Test]
        public void should_remove_the_author_folder_when_nothing_is_left_under_it()
        {
            var author = new Author
            {
                Id = 1,
                Name = "Jim Butcher",
                Path = "/audiobooks/Jim Butcher",
                AudiobookPath = "/audiobooks/Jim Butcher"
            };

            var (service, disk) = BuildFolderCleanupSubject(author.AudiobookPath);

            service.Handle(new BookFileDeletedEvent(
                new BookFile { Id = 1, Path = "/audiobooks/Jim Butcher/Captains Fury/Captains Fury.m4b", Author = author },
                DeleteMediaFileReason.Manual));

            Assert.That(disk.DeletedFolders, Does.Contain(author.AudiobookPath));
        }

        [Test]
        public void should_never_walk_into_the_other_media_types_root()
        {
            // Deleting an ebook must not touch the audiobook tree, and vice versa.
            var author = new Author
            {
                Id = 1,
                Name = "Jim Butcher",
                Path = "/audiobooks/Jim Butcher",
                AudiobookPath = "/audiobooks/Jim Butcher",
                EbookPath = "/ebooks/Jim Butcher"
            };

            var (service, disk) = BuildFolderCleanupSubject(author.AudiobookPath, author.EbookPath);
            var ebookFolder = "/ebooks/Jim Butcher/Captains Fury";
            disk.ExistingFolders.Add(ebookFolder);
            disk.FoldersWithFiles.Add(author.EbookPath);

            service.Handle(new BookFileDeletedEvent(
                new BookFile { Id = 1, Path = ebookFolder + "/Captains Fury.epub", Author = author },
                DeleteMediaFileReason.Manual));

            Assert.That(disk.SubfolderCleanups, Does.Contain(author.EbookPath));
            Assert.That(disk.SubfolderCleanups, Does.Not.Contain(author.AudiobookPath));
            Assert.That(disk.DeletedFolders, Does.Not.Contain(author.AudiobookPath));
        }

        [Test]
        public void should_refuse_deleting_configured_root_folder_on_author_delete()
        {
            var recycleBinProvider = new RecordingRecycleBinProvider();
            var eventAggregator = new RecordingEventAggregator();

            var rootFolderService = new StubRootFolderService(new RootFolder
            {
                Path = "/data/media/books",
                FolderType = FolderType.Mixed
            });

            var service = new MediaFileDeletionService(
                DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>(),
                recycleBinProvider,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                eventAggregator,
                rootFolderService,
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                Path = "/data/media/books"
            };

            service.HandleAsync(new AuthorDeletedEvent(author, deleteFiles: true, addImportListExclusion: false));

            Assert.That(recycleBinProvider.DeletedFolders, Is.Empty);
            Assert.That(eventAggregator.Events.OfType<DeleteCompletedEvent>(), Is.Not.Empty);
        }

        [Test]
        public void should_not_throw_and_should_refuse_deleting_parent_of_root_folder_on_author_delete()
        {
            var recycleBinProvider = new RecordingRecycleBinProvider();
            var eventAggregator = new RecordingEventAggregator();

            var rootFolderService = new StubRootFolderService(new RootFolder
            {
                Path = "/data/media/books",
                FolderType = FolderType.Mixed
            });

            var service = new MediaFileDeletionService(
                DispatchProxy.Create<IDiskProvider, ThrowingProxy<IDiskProvider>>(),
                recycleBinProvider,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                eventAggregator,
                rootFolderService,
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Id = 1,
                Name = "Test Author",
                Path = "/data/media"
            };

            Assert.DoesNotThrow(() =>
                service.HandleAsync(new AuthorDeletedEvent(author, deleteFiles: true, addImportListExclusion: false)));

            Assert.That(recycleBinProvider.DeletedFolders, Is.Empty);
            Assert.That(eventAggregator.Events.OfType<DeleteCompletedEvent>(), Is.Not.Empty);
        }

        [Test]
        public void should_delete_managed_ebook_replica_files_on_bookfile_delete_event_even_on_upgrade()
        {
            var recycleBinProvider = new RecordingRecycleBinProvider();
            var eventAggregator = new RecordingEventAggregator();

            var rootFolderService = new StubRootFolderService(new RootFolder
            {
                Path = "/data/media/books",
                FolderType = FolderType.Mixed
            });

            var diskProvider = DispatchProxy.Create<IDiskProvider, FileExistsOnlyDiskProviderProxy>();
            var diskProxy = (FileExistsOnlyDiskProviderProxy)(object)diskProvider;

            var replica1 = "/data/media/books/Test Author/Test Book - Narrator/Test Book.pdf";
            var replica2 = "/data/media/books/Test Author/Test Book - Narrator 2/Test Book.pdf";

            diskProxy.ExistingFiles.Add(replica1);
            diskProxy.ExistingFiles.Add(replica2);

            var service = new MediaFileDeletionService(
                diskProvider,
                recycleBinProvider,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                eventAggregator,
                rootFolderService,
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            var bookFile = new BookFile
            {
                Path = "/data/media/books/Test Author/Test Book/Test Book.pdf",
                ReplicaPaths = new List<string> { replica1, replica2 }
            };

            service.Handle(new BookFileDeletedEvent(bookFile, DeleteMediaFileReason.Upgrade));

            Assert.That(recycleBinProvider.DeletedFiles, Does.Contain(replica1));
            Assert.That(recycleBinProvider.DeletedFiles, Does.Contain(replica2));
        }

        [Test]
        public void should_not_delete_a_loose_only_managed_ebook_replica_match()
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, StaleTrackedPathDiskProviderProxy>();
            var recycleBinProvider = new RecordingRecycleBinProvider();
            var service = new MediaFileDeletionService(
                diskProvider,
                recycleBinProvider,
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                new RecordingEventAggregator(),
                new StubRootFolderService(),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            var bookFile = new BookFile
            {
                Path = "/books/Philosopher's Stone/Philosopher's Stone.epub",
                ReplicaPaths = new List<string> { "/books/Philosopher’s Stone/Philosopher’s Stone.epub" }
            };

            Assert.DoesNotThrow(() => service.Handle(new BookFileDeletedEvent(bookFile, DeleteMediaFileReason.Upgrade)));
            Assert.That(recycleBinProvider.DeletedFiles, Is.Empty);
        }

        [Test]
        public void should_remove_stale_row_without_deleting_a_loose_apostrophe_match()
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, StaleTrackedPathDiskProviderProxy>();
            var mediaFileService = DispatchProxy.Create<IMediaFileService, RecordingMediaFileServiceProxy>();
            var mediaProxy = (RecordingMediaFileServiceProxy)(object)mediaFileService;
            var recycleBinProvider = new RecordingRecycleBinProvider();
            var eventAggregator = new RecordingEventAggregator();
            var bookFile = new BookFile
            {
                Id = 1,
                Path = "/books/Philosopher’s Stone.m4b"
            };

            var service = new MediaFileDeletionService(
                diskProvider,
                recycleBinProvider,
                mediaFileService,
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IConfigService, ThrowingProxy<IConfigService>>(),
                eventAggregator,
                new StubRootFolderService(),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            Assert.DoesNotThrow(() => service.DeleteTrackFile(bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(mediaProxy.Deleted, Is.EqualTo(new[] { bookFile }));
                Assert.That(recycleBinProvider.DeletedFiles, Is.Empty);
                Assert.That(eventAggregator.Events.OfType<DeleteCompletedEvent>(), Has.Exactly(1).Items);
            });
        }

        [Test]
        public void should_route_the_delete_through_the_root_that_actually_contains_the_file()
        {
            // Issue #32: the author's stored path is under the audiobook root, the file is under the
            // ebook root. Routing by author path threw NotParentException and nothing was deleted.
            var audiobookRoot = new RootFolder { Id = 1, Path = @"C:\spicyaudiobooks".AsOsAgnostic() };
            var ebookRoot = new RootFolder { Id = 2, Path = @"C:\spicybooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(audiobookRoot, ebookRoot);

            var author = new Author
            {
                Id = 1,
                Name = "Navessa Allen",
                Path = @"C:\spicyaudiobooks\Navessa Allen".AsOsAgnostic(),
                AudiobookRootFolderPath = audiobookRoot.Path,
                EbookRootFolderPath = ebookRoot.Path
            };

            var filePath = @"C:\spicybooks\Navessa Allen\Ladies of Infamy\Scandal (2014)\scandal.epub".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "ebook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            Assert.DoesNotThrow(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(subject.RecycleBin.DeletedFiles, Is.EqualTo(new[] { filePath }));

                // The recycle subfolder is relative to the root, so the root's own name is not in it.
                Assert.That(subject.RecycleBin.DeletedFileSubfolders,
                    Is.EqualTo(new[] { @"Navessa Allen\Ladies of Infamy\Scandal (2014)".AsOsAgnostic() }));
                Assert.That(subject.RecycleBin.DeletedFileSubfolders.Single(), Does.Not.Contain("spicybooks"));

                Assert.That(subject.MediaFiles.Deleted, Is.EqualTo(new[] { bookFile }));
                Assert.That(subject.Events.Events.OfType<DeleteCompletedEvent>(), Has.Exactly(1).Items);
            });
        }

        [Test]
        public void should_calculate_the_recycle_subfolder_from_a_root_stored_with_a_trailing_separator()
        {
            // Root folder paths are stored verbatim and can carry a trailing separator.
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks\audiobooks\".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var author = new Author
            {
                Id = 1,
                Name = "Matt Dinniman",
                Path = @"C:\audiobooks\audiobooks\Matt Dinniman".AsOsAgnostic(),
                AudiobookRootFolderPath = root.Path
            };

            var filePath = @"C:\audiobooks\audiobooks\Matt Dinniman\Dungeon Crawler Carl\dcc.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            Assert.DoesNotThrow(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.That(subject.RecycleBin.DeletedFileSubfolders,
                Is.EqualTo(new[] { @"Matt Dinniman\Dungeon Crawler Carl".AsOsAgnostic() }));
        }

        [Test]
        public void should_not_treat_a_name_prefixed_folder_as_the_containing_root()
        {
            // "/audiobooks" must not claim a file living in "/audiobooksXYZ".
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var author = new Author { Id = 1, Name = "Blake Crouch", Path = @"C:\audiobooks\Blake Crouch".AsOsAgnostic() };
            var filePath = @"C:\audiobooksXYZ\Blake Crouch\Recursion\recursion.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            var exception = Assert.Throws<NzbDroneClientException>(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(subject.RecycleBin.DeletedFiles, Is.Empty);
                Assert.That(subject.MediaFiles.Deleted, Is.Empty);
            });
        }

        [Test]
        public void should_refuse_and_keep_the_row_when_no_configured_root_contains_the_file()
        {
            // Without a containing root we cannot tell a removed root-folder setting from an
            // unavailable mount, and cannot choose between Calibre and recycle-bin handling.
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var author = new Author { Id = 1, Name = "Gillian Flynn", Path = @"C:\audiobooks\Gillian Flynn".AsOsAgnostic() };
            var filePath = @"C:\elsewhere\Gillian Flynn\Gone Girl\gone-girl.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            var exception = Assert.Throws<NzbDroneClientException>(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(exception.Message, Is.EqualTo($"Book file ({filePath}) is not inside any configured root folder."));
                Assert.That(subject.RecycleBin.DeletedFiles, Is.Empty);
                Assert.That(subject.MediaFiles.Deleted, Is.Empty);
                Assert.That(subject.Events.Events.OfType<DeleteCompletedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void should_refuse_an_unmapped_file_outside_every_configured_root_rather_than_guess_calibre()
        {
            // The unmapped overload skips the routing method entirely, so the same refusal has to live
            // in the deletion core. Falling through would silently pick non-Calibre recycle handling.
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var filePath = @"C:\somewhere-else\loose\stray.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            var exception = Assert.Throws<NzbDroneClientException>(() => subject.Service.DeleteTrackFile(bookFile, "Unmapped_Files"));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(exception.Message, Is.EqualTo($"Book file ({filePath}) is not inside any configured root folder."));
                Assert.That(subject.RecycleBin.DeletedFiles, Is.Empty);
                Assert.That(subject.MediaFiles.Deleted, Is.Empty);
            });
        }

        [Test]
        public void should_use_an_empty_recycle_subfolder_for_a_file_sitting_directly_in_the_root()
        {
            // The file's parent IS the root. GetRelativePath treats equal paths as unrelated, so without
            // the PathEquals guard this throws NotParentException and surfaces as a 500.
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var author = new Author { Id = 1, Name = "Caro Burke", Path = @"C:\audiobooks\Caro Burke".AsOsAgnostic() };
            var filePath = @"C:\audiobooks\loose.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            Assert.DoesNotThrow(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(subject.RecycleBin.DeletedFiles, Is.EqualTo(new[] { filePath }));
                Assert.That(subject.RecycleBin.DeletedFileSubfolders, Is.EqualTo(new[] { string.Empty }));
                Assert.That(subject.MediaFiles.Deleted, Is.EqualTo(new[] { bookFile }));
            });
        }

        [Test]
        public void should_refuse_when_the_containing_root_is_missing_from_disk()
        {
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            // The mount is gone: the root is still configured but no longer on disk.
            subject.Disk.ExistingFolders.Clear();

            var author = new Author { Id = 1, Name = "Grady Hendrix", Path = @"C:\audiobooks\Grady Hendrix".AsOsAgnostic() };
            var filePath = @"C:\audiobooks\Grady Hendrix\Horrorstor\horrorstor.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            var exception = Assert.Throws<NzbDroneClientException>(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(exception.Message, Is.EqualTo($"Root folder ({root.Path}) doesn't exist."));
                Assert.That(subject.RecycleBin.DeletedFiles, Is.Empty);
                Assert.That(subject.MediaFiles.Deleted, Is.Empty);
            });
        }

        [Test]
        public void should_refuse_when_the_containing_root_is_empty()
        {
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            // Root exists but has no children — the classic remounted-empty-share shape.
            subject.Disk.FoldersWithSubfolders.Clear();

            var author = new Author { Id = 1, Name = "Jessica Park", Path = @"C:\audiobooks\Jessica Park".AsOsAgnostic() };
            var filePath = @"C:\audiobooks\Jessica Park\Flat-Out Love\flat-out-love.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            var exception = Assert.Throws<NzbDroneClientException>(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(exception.Message, Is.EqualTo($"Root folder ({root.Path}) is empty."));
                Assert.That(subject.RecycleBin.DeletedFiles, Is.Empty);
                Assert.That(subject.MediaFiles.Deleted, Is.Empty);
            });
        }

        [Test]
        public void should_remove_the_stale_row_when_the_tracked_file_is_gone_under_a_healthy_root()
        {
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var author = new Author { Id = 1, Name = "Fiona Cole", Path = @"C:\audiobooks\Fiona Cole".AsOsAgnostic() };
            var filePath = @"C:\audiobooks\Fiona Cole\Enamor\enamor.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            // File is not on disk; the row still needs to go.

            Assert.DoesNotThrow(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(subject.RecycleBin.DeletedFiles, Is.Empty);
                Assert.That(subject.MediaFiles.Deleted, Is.EqualTo(new[] { bookFile }));
                Assert.That(subject.Events.Events.OfType<DeleteCompletedEvent>(), Has.Exactly(1).Items);
            });
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("audiobook")]
        [TestCase("EBOOK")]
        public void should_follow_the_physical_path_regardless_of_the_stored_media_type(string mediaType)
        {
            // Media type is not evidence of containment: it can be blank, stale, differently cased, or
            // simply wrong for where the file actually sits.
            var audiobookRoot = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var ebookRoot = new RootFolder { Id = 2, Path = @"C:\ebooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(audiobookRoot, ebookRoot);

            var author = new Author
            {
                Id = 1,
                Name = "Jim Butcher",
                Path = @"C:\audiobooks\Jim Butcher".AsOsAgnostic(),
                AudiobookRootFolderPath = audiobookRoot.Path,
                EbookRootFolderPath = ebookRoot.Path
            };

            var filePath = @"C:\ebooks\Jim Butcher\Captains Fury\captains-fury.epub".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = mediaType, Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            Assert.DoesNotThrow(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(subject.RecycleBin.DeletedFiles, Is.EqualTo(new[] { filePath }));
                Assert.That(subject.RecycleBin.DeletedFileSubfolders,
                    Is.EqualTo(new[] { @"Jim Butcher\Captains Fury".AsOsAgnostic() }));
                Assert.That(subject.MediaFiles.Deleted, Is.EqualTo(new[] { bookFile }));
            });
        }

        [Test]
        public void should_delete_and_publish_completion_when_the_author_folder_is_missing()
        {
            // The author folder is gone but the file is still on disk under a healthy root. The old
            // author-folder branch skipped the disk delete entirely and never published completion.
            var root = new RootFolder { Id = 1, Path = @"C:\audiobooks".AsOsAgnostic() };
            var subject = BuildTrackFileDeleteSubject(root);

            var author = new Author
            {
                Id = 1,
                Name = "Annie Anderson",
                Path = @"C:\audiobooks\Annie Anderson".AsOsAgnostic(),
                AudiobookRootFolderPath = root.Path
            };

            var filePath = @"C:\audiobooks\Loose Files\stray.m4b".AsOsAgnostic();
            var bookFile = new BookFile { Id = 1, EditionId = 1, MediaType = "audiobook", Path = filePath };

            subject.Disk.ExistingFiles.Add(filePath);

            Assert.DoesNotThrow(() => subject.Service.DeleteTrackFile(author, bookFile));

            Assert.Multiple(() =>
            {
                Assert.That(subject.RecycleBin.DeletedFiles, Is.EqualTo(new[] { filePath }));
                Assert.That(subject.RecycleBin.DeletedFileSubfolders, Is.EqualTo(new[] { "Loose Files" }));
                Assert.That(subject.MediaFiles.Deleted, Is.EqualTo(new[] { bookFile }));
                Assert.That(subject.Events.Events.OfType<DeleteCompletedEvent>(), Has.Exactly(1).Items);
            });
        }
    }
}
