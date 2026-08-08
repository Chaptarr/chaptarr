using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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

            public void DeleteFolder(string path)
            {
                DeletedFolders.Add(path);
            }

            public void DeleteFile(string path, string subfolder = "")
            {
                DeletedFiles.Add(path);
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
    }
}
