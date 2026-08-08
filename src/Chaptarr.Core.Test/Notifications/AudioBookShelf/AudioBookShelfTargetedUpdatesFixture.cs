using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfTargetedUpdatesFixture
    {
        [Test]
        public void should_send_targeted_add_for_mapped_import()
        {
            var proxy = new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-audio", "folder-audio", "/abs/audio", disableWatcher: false)
                }
            };

            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook }
            }, new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "library-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                }
            });

            subject.OnReleaseImport(new BookDownloadMessage
            {
                Author = new Author { Name = "Joe Abercrombie", AudiobookRootFolderPath = "/audiobooks" },
                Book = new Book { Title = "The Blade Itself", MediaType = BookMediaType.Audiobook },
                BookFiles = new List<BookFile>
                {
                    new BookFile
                    {
                        Path = "/audiobooks/Joe Abercrombie/The Blade Itself/The Blade Itself.m4b",
                        MediaType = "audiobook"
                    }
                }
            });

            Assert.That(proxy.WatcherUpdates, Has.Count.EqualTo(1));
            Assert.That(proxy.WatcherUpdates[0].LibraryId, Is.EqualTo("library-audio"));
            Assert.That(proxy.WatcherUpdates[0].Path, Is.EqualTo("/abs/audio/Joe Abercrombie/The Blade Itself/The Blade Itself.m4b"));
            Assert.That(proxy.WatcherUpdates[0].Type, Is.EqualTo("add"));
            Assert.That(proxy.ScanLibraryIds, Is.Empty);
        }

        [Test]
        public void should_fallback_to_full_scan_when_watcher_is_disabled()
        {
            var proxy = new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-audio", "folder-audio", "/abs/audio", disableWatcher: true)
                }
            };

            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook }
            }, new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "library-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                }
            });

            subject.OnReleaseImport(new BookDownloadMessage
            {
                Author = new Author { Name = "Joe Abercrombie", AudiobookRootFolderPath = "/audiobooks" },
                Book = new Book { Title = "Best Served Cold", MediaType = BookMediaType.Audiobook },
                BookFiles = new List<BookFile>
                {
                    new BookFile
                    {
                        Path = "/audiobooks/Joe Abercrombie/Best Served Cold/Best Served Cold.m4b",
                        MediaType = "audiobook"
                    }
                }
            });

            Assert.That(proxy.WatcherUpdates, Is.Empty);
            Assert.That(proxy.ScanLibraryIds, Is.EqualTo(new[] { "library-audio" }));
        }

        [Test]
        public void should_throw_when_fallback_scan_fails()
        {
            var proxy = new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-audio", "folder-audio", "/abs/audio", disableWatcher: true)
                },
                ScanFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "library-audio"
                }
            };

            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook }
            }, new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "library-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                }
            });

            var exception = Assert.Throws<InvalidOperationException>(() => subject.OnReleaseImport(new BookDownloadMessage
            {
                Author = new Author { Name = "Joe Abercrombie", AudiobookRootFolderPath = "/audiobooks" },
                Book = new Book { Title = "Best Served Cold", MediaType = BookMediaType.Audiobook },
                BookFiles = new List<BookFile>
                {
                    new BookFile
                    {
                        Path = "/audiobooks/Joe Abercrombie/Best Served Cold/Best Served Cold.m4b",
                        MediaType = "audiobook"
                    }
                }
            }));

            Assert.That(exception.Message, Does.Contain("library-audio"));
            Assert.That(proxy.WatcherUpdates, Is.Empty);
            Assert.That(proxy.ScanLibraryIds, Is.EqualTo(new[] { "library-audio" }));
        }

        [Test]
        public void should_send_targeted_rename_when_staying_in_same_library_folder()
        {
            var proxy = new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-audio", "folder-audio", "/abs/audio", disableWatcher: false)
                }
            };

            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook }
            }, new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "library-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                }
            });

            subject.OnRename(new Author { Name = "Joe Abercrombie" }, new List<RenamedBookFile>
            {
                new RenamedBookFile
                {
                    PreviousPath = "/audiobooks/Joe Abercrombie/Before They Are Hanged/old-name.m4b",
                    BookFile = new BookFile
                    {
                        Path = "/audiobooks/Joe Abercrombie/Before They Are Hanged/new-name.m4b",
                        MediaType = "audiobook"
                    }
                }
            });

            Assert.That(proxy.WatcherUpdates, Has.Count.EqualTo(1));
            Assert.That(proxy.WatcherUpdates[0].Type, Is.EqualTo("rename"));
            Assert.That(proxy.WatcherUpdates[0].Path, Is.EqualTo("/abs/audio/Joe Abercrombie/Before They Are Hanged/new-name.m4b"));
            Assert.That(proxy.WatcherUpdates[0].OldPath, Is.EqualTo("/abs/audio/Joe Abercrombie/Before They Are Hanged/old-name.m4b"));
            Assert.That(proxy.ScanLibraryIds, Is.Empty);
        }

        [Test]
        public void should_split_cross_library_rename_into_unlink_and_add()
        {
            var proxy = new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-a", "folder-a", "/abs/audio-a", disableWatcher: false),
                    BuildLibrary("library-b", "folder-b", "/abs/audio-b", disableWatcher: false)
                }
            };

            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks-a", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/audiobooks-b", FolderType = FolderType.Audiobook }
            }, new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "library-a",
                    LibraryFolderId = "folder-a",
                    LibraryFolderPath = "/abs/audio-a"
                },
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 2,
                    MediaType = "audiobook",
                    LibraryId = "library-b",
                    LibraryFolderId = "folder-b",
                    LibraryFolderPath = "/abs/audio-b"
                }
            });

            subject.OnRename(new Author { Name = "Joe Abercrombie" }, new List<RenamedBookFile>
            {
                new RenamedBookFile
                {
                    PreviousPath = "/audiobooks-a/Joe Abercrombie/The Heroes/The Heroes.m4b",
                    BookFile = new BookFile
                    {
                        Path = "/audiobooks-b/Joe Abercrombie/The Heroes/The Heroes.m4b",
                        MediaType = "audiobook"
                    }
                }
            });

            Assert.That(proxy.WatcherUpdates.Select(x => (x.LibraryId, x.Type, x.Path)).ToArray(), Is.EqualTo(new[]
            {
                ("library-a", "unlink", "/abs/audio-a/Joe Abercrombie/The Heroes/The Heroes.m4b"),
                ("library-b", "add", "/abs/audio-b/Joe Abercrombie/The Heroes/The Heroes.m4b")
            }));
            Assert.That(proxy.ScanLibraryIds, Is.Empty);
        }

        [Test]
        public void should_reuse_cached_libraries_across_events()
        {
            var proxy = new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-audio", "folder-audio", "/abs/audio", disableWatcher: false)
                }
            };

            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook }
            }, new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "library-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                }
            });

            for (var i = 0; i < 3; i++)
            {
                subject.OnReleaseImport(new BookDownloadMessage
                {
                    Author = new Author { Name = "Joe Abercrombie", AudiobookRootFolderPath = "/audiobooks" },
                    Book = new Book { Title = $"Book {i}", MediaType = BookMediaType.Audiobook },
                    BookFiles = new List<BookFile>
                    {
                        new BookFile
                        {
                            Path = $"/audiobooks/Joe Abercrombie/Book {i}/Book {i}.m4b",
                            MediaType = "audiobook"
                        }
                    }
                });
            }

            Assert.That(proxy.WatcherUpdates, Has.Count.EqualTo(3));
            Assert.That(proxy.GetLibrariesCallCount, Is.EqualTo(1));
        }

        private static NzbDrone.Core.Notifications.AudioBookShelf.AudioBookShelf CreateSubject(FakeAudioBookShelfProxy proxy, List<RootFolder> rootFolders, List<AudioBookShelfLibraryMapping> mappings)
        {
            var settings = new AudioBookShelfSettings
            {
                Host = "abs",
                Port = 13378,
                ApiKey = "test"
            };

            settings.SetLibraryMappings(mappings);

            return new NzbDrone.Core.Notifications.AudioBookShelf.AudioBookShelf(
                proxy,
                httpClient: null,
                pendingProviderSecretService: new PendingProviderSecretService(new CacheManager()),
                cacheManager: new CacheManager(),
                rootFolderService: new FakeRootFolderService(rootFolders),
                logger: LogManager.GetLogger("AudioBookShelfTargetedUpdatesFixture"))
            {
                Definition = new NotificationDefinition
                {
                    Settings = settings
                }
            };
        }

        private static AudioBookShelfLibrary BuildLibrary(string libraryId, string folderId, string folderPath, bool disableWatcher)
        {
            return new AudioBookShelfLibrary
            {
                Id = libraryId,
                Name = libraryId,
                MediaType = "book",
                Settings = new AudioBookShelfLibrarySettings
                {
                    DisableWatcher = disableWatcher,
                    AudiobooksOnly = false
                },
                Folders = new List<AudioBookShelfLibraryFolder>
                {
                    new AudioBookShelfLibraryFolder
                    {
                        Id = folderId,
                        FullPath = folderPath,
                        LibraryId = libraryId
                    }
                }
            };
        }

        private class FakeAudioBookShelfProxy : IAudioBookShelfProxy
        {
            public List<AudioBookShelfLibrary> Libraries { get; set; } = new List<AudioBookShelfLibrary>();
            public List<string> ScanLibraryIds { get; } = new List<string>();
            public HashSet<string> ScanFailures { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<WatcherUpdateCall> WatcherUpdates { get; } = new List<WatcherUpdateCall>();
            public int GetLibrariesCallCount { get; private set; }

            public void ScanLibrary(AudioBookShelfSettings settings)
            {
                ScanLibraryIds.Add("__all__");
            }

            public void ScanLibrary(AudioBookShelfSettings settings, string libraryId)
            {
                ScanLibraryIds.Add(libraryId);

                if (ScanFailures.Contains(libraryId))
                {
                    throw new Exception("scan failed");
                }
            }

            public void UpdateWatchedPath(AudioBookShelfSettings settings, string libraryId, string path, string type, string oldPath = null)
            {
                WatcherUpdates.Add(new WatcherUpdateCall
                {
                    LibraryId = libraryId,
                    Path = path,
                    Type = type,
                    OldPath = oldPath
                });
            }

            public FluentValidation.Results.ValidationFailure Test(AudioBookShelfSettings settings)
            {
                return null;
            }

            public List<AudioBookShelfLibrary> GetLibraries(AudioBookShelfSettings settings)
            {
                GetLibrariesCallCount++;
                return Libraries;
            }
        }

        private class FakeRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public FakeRootFolderService(List<RootFolder> rootFolders)
            {
                _rootFolders = rootFolders ?? new List<RootFolder>();
            }

            public List<RootFolder> All()
            {
                return _rootFolders;
            }

            public List<RootFolder> AllWithSpaceStats()
            {
                return _rootFolders;
            }

            public RootFolder Add(RootFolder rootFolder)
            {
                throw new NotImplementedException();
            }

            public RootFolder Update(RootFolder rootFolder)
            {
                throw new NotImplementedException();
            }

            public void Remove(int id)
            {
                throw new NotImplementedException();
            }

            public RootFolder Get(int id)
            {
                return _rootFolders.FirstOrDefault(r => r.Id == id);
            }

            public List<RootFolder> AllForTag(int tagId)
            {
                throw new NotImplementedException();
            }

            public RootFolder GetBestRootFolder(string path)
            {
                return GetBestRootFolder(path, _rootFolders);
            }

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return (allRootFolders ?? _rootFolders)
                    .Where(r => r.Path.PathEquals(path) || r.Path.IsParentPath(path))
                    .OrderByDescending(r => r.Path.Length)
                    .FirstOrDefault();
            }

            public string GetBestRootFolderPath(string path)
            {
                return GetBestRootFolder(path)?.Path;
            }

            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders)
            {
                return GetBestRootFolder(path, allRootFolders)?.Path;
            }
        }

        private class WatcherUpdateCall
        {
            public string LibraryId { get; set; }
            public string Path { get; set; }
            public string Type { get; set; }
            public string OldPath { get; set; }
        }
    }
}
