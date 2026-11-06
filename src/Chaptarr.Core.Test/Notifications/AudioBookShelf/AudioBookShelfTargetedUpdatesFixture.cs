using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public void cover_event_push_should_not_overwrite_item_metadata()
        {
            var proxy = BuildPushProxy();
            var subject = CreatePushSubject(proxy);

            subject.PushBooksCovers(BuildPushableBook());

            Assert.That(proxy.MetadataUpdates, Is.Empty);
        }

        [Test]
        public void library_edit_push_should_still_send_item_metadata()
        {
            var proxy = BuildPushProxy();
            var subject = CreatePushSubject(proxy);

            subject.PushBooksMetadata(BuildPushableBook());

            Assert.That(proxy.MetadataUpdates, Is.EqualTo(new[] { "item-1" }));
        }

        [Test]
        public void automatic_push_should_leave_items_with_an_ignore_tag_untouched()
        {
            var proxy = BuildPushProxy("processed");
            var subject = CreatePushSubject(proxy, "Processed");

            subject.PushBooksMetadata(BuildPushableBook());

            Assert.That(proxy.MetadataUpdates, Is.Empty);
            Assert.That(proxy.CoverUpdates, Is.Empty);
        }

        [Test]
        public void automatic_push_should_still_update_items_without_an_ignore_tag()
        {
            var proxy = BuildPushProxy("Fantasy");
            var subject = CreatePushSubject(proxy, "Processed");

            subject.PushBooksMetadata(BuildPushableBook());

            Assert.That(proxy.MetadataUpdates, Is.EqualTo(new[] { "item-1" }));
        }

        [TestCase(false, 0)]
        [TestCase(true, 1)]
        public void forwarded_edit_should_reach_an_ignored_item_only_when_manual(bool manual, int expectedUpdates)
        {
            var proxy = BuildPushProxy("Processed");
            var subject = CreatePushSubject(proxy, "Processed");
            var (book, files) = BuildPushableBook().Single();

            subject.PushExternalLibraryEdit(book, files, new ExternalLibraryEditPayload
            {
                Description = "Edited",
                CoverUrl = "http://covers/cover.jpg",
                Manual = manual
            });

            Assert.That(proxy.MetadataUpdates, Has.Count.EqualTo(expectedUpdates));
            Assert.That(proxy.CoverUpdates, Has.Count.EqualTo(expectedUpdates));
        }

        [Test]
        public void audiobookshelf_keeper_should_block_every_push_to_audiobookshelf()
        {
            var proxy = BuildPushProxy();
            var subject = CreatePushSubject(proxy);
            var settings = (AudioBookShelfSettings)subject.Definition.Settings;
            settings.PushLibraryEdits = true;
            settings.MetadataKeeper = (int)AudioBookShelfMetadataKeeper.AudioBookShelf;
            var (book, files) = BuildPushableBook().Single();

            subject.PushBooksMetadata(BuildPushableBook());
            subject.PushBooksCovers(BuildPushableBook());
            subject.PushExternalLibraryEdit(book, files, new ExternalLibraryEditPayload
            {
                Description = "Edited",
                CoverUrl = "http://covers/cover.jpg",
                Manual = true
            });

            Assert.That(subject.AcceptsExternalLibraryEdits, Is.False);
            Assert.That(proxy.MetadataUpdates, Is.Empty);
            Assert.That(proxy.CoverUpdates, Is.Empty);
        }

        [Test]
        public void chaptarr_keeper_should_keep_accepting_library_edits()
        {
            var proxy = BuildPushProxy();
            var subject = CreatePushSubject(proxy);
            ((AudioBookShelfSettings)subject.Definition.Settings).PushLibraryEdits = true;

            Assert.That(subject.AcceptsExternalLibraryEdits, Is.True);
        }

        [Test]
        public void edition_sync_should_pin_each_mapped_item_folder_with_its_asin()
        {
            var proxy = BuildPushProxy();
            proxy.Items[0].FolderId = "folder-audio";
            proxy.Items[0].Media.Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000BLADE1" };
            proxy.Items.Add(new AudioBookShelfLibraryItemSummary
            {
                Id = "item-missing",
                RelPath = "Joe Abercrombie/Gone",
                FolderId = "folder-audio",
                IsMissing = true,
                Media = new AudioBookShelfLibraryItemMedia { Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000GONE01" } }
            });
            proxy.Items.Add(new AudioBookShelfLibraryItemSummary
            {
                Id = "item-other-folder",
                RelPath = "Someone/Elsewhere",
                FolderId = "folder-other",
                Media = new AudioBookShelfLibraryItemMedia { Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000ELSE01" } }
            });

            var pins = new RecordingEditionPinService();
            var subject = CreatePushSubject(proxy, pins);
            ((AudioBookShelfSettings)subject.Definition.Settings).MetadataKeeper = (int)AudioBookShelfMetadataKeeper.AudioBookShelf;

            var outcomes = subject.SyncEditionsFromAudioBookShelf();

            Assert.That(pins.Calls, Is.EqualTo(new[]
            {
                (System.IO.Path.Combine("/audiobooks", "Joe Abercrombie", "The Blade Itself"), "audiobook", "B000BLADE1")
            }));
            Assert.That(outcomes[AudioBookShelfEditionPinOutcome.Pinned], Is.EqualTo(1));
        }

        [Test]
        public void edition_sync_should_do_nothing_when_chaptarr_keeps_the_metadata()
        {
            var proxy = BuildPushProxy();
            proxy.Items[0].Media.Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000BLADE1" };
            var pins = new RecordingEditionPinService();
            var subject = CreatePushSubject(proxy, pins);

            var outcomes = subject.SyncEditionsFromAudioBookShelf();

            Assert.That(pins.Calls, Is.Empty);
            Assert.That(outcomes, Is.Empty);
        }

        [Test]
        public void library_item_list_should_read_the_asin_folder_and_missing_flag()
        {
            // Shape of GET /api/libraries/{id}/items?minified=1 (LibraryItem.toOldJSONMinified).
            const string json = @"{""id"":""li_1"",""folderId"":""fol_1"",""relPath"":""Blaise Corvin/Nora Hazard/1 Mitigating Risk"",""isMissing"":true,""mediaType"":""book"",
                ""media"":{""tags"":[""Processed""],""metadata"":{""title"":""Mitigating Risk"",""asin"":""B07CN8FDM8"",""isbn"":null}}}";

            var item = NzbDrone.Common.Serializer.Json.Deserialize<AudioBookShelfLibraryItemSummary>(json);

            Assert.That(item.FolderId, Is.EqualTo("fol_1"));
            Assert.That(item.IsMissing, Is.True);
            Assert.That(item.Media.Metadata.Asin, Is.EqualTo("B07CN8FDM8"));
            Assert.That(item.Media.Tags, Is.EqualTo(new[] { "Processed" }));
        }

        [Test]
        public void pin_after_import_should_only_use_the_item_from_the_folders_own_library()
        {
            var proxy = new FakeAudioBookShelfProxy();
            proxy.ItemsByLibrary["library-audio"] = new List<AudioBookShelfLibraryItemSummary>
            {
                new AudioBookShelfLibraryItemSummary
                {
                    Id = "audio-item", RelPath = "Blaise Corvin/Mitigating Risk", FolderId = "folder-audio",
                    Media = new AudioBookShelfLibraryItemMedia { Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B0AUDIBLE1" } }
                }
            };
            proxy.ItemsByLibrary["library-ebook"] = new List<AudioBookShelfLibraryItemSummary>
            {
                new AudioBookShelfLibraryItemSummary
                {
                    Id = "ebook-item", RelPath = "Blaise Corvin/Mitigating Risk", FolderId = "folder-ebook",
                    Media = new AudioBookShelfLibraryItemMedia { Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B0KINDLE01" } }
                }
            };

            var mappings = new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping { RootFolderId = 1, MediaType = "audiobook", LibraryId = "library-audio", LibraryFolderId = "folder-audio", LibraryFolderPath = "/abs/audio" },
                new AudioBookShelfLibraryMapping { RootFolderId = 2, MediaType = "ebook", LibraryId = "library-ebook", LibraryFolderId = "folder-ebook", LibraryFolderPath = "/abs/ebook" }
            };

            var pins = new RecordingEditionPinService();
            var subject = CreateSubject(proxy, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/ebooks", FolderType = FolderType.Ebook }
            }, mappings, pins);
            var settings = (AudioBookShelfSettings)subject.Definition.Settings;
            settings.MetadataKeeper = (int)AudioBookShelfMetadataKeeper.AudioBookShelf;

            subject.PinFolders(settings, mappings, new List<string> { "/audiobooks/Blaise Corvin/Mitigating Risk" }, null);

            Assert.That(pins.Calls, Is.EqualTo(new[] { ("/audiobooks/Blaise Corvin/Mitigating Risk", "audiobook", "B0AUDIBLE1") }));
        }

        [Test]
        public void book_scoped_sync_should_pin_the_item_holding_the_books_part_folders_once()
        {
            var proxy = BuildPushProxy();
            proxy.Items[0].FolderId = "folder-audio";
            proxy.Items[0].Media.Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000BLADE1" };
            proxy.Items.Add(new AudioBookShelfLibraryItemSummary
            {
                Id = "item-2", RelPath = "Joe Abercrombie/Before They Are Hanged", FolderId = "folder-audio",
                Media = new AudioBookShelfLibraryItemMedia { Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000HANGED" } }
            });

            var pins = new RecordingEditionPinService();
            var subject = CreatePushSubject(proxy, pins);
            ((AudioBookShelfSettings)subject.Definition.Settings).MetadataKeeper = (int)AudioBookShelfMetadataKeeper.AudioBookShelf;

            var files = DispatchProxy.Create<IMediaFileService, BookFilesProxy>();
            ((BookFilesProxy)(object)files).Files = new List<BookFile>
            {
                new BookFile { Path = "/audiobooks/Joe Abercrombie/The Blade Itself/CD1/part1.mp3" },
                new BookFile { Path = "/audiobooks/Joe Abercrombie/The Blade Itself/CD2/part2.mp3" }
            };
            var factory = DispatchProxy.Create<INotificationFactory, NotificationFactoryProxy>();
            ((NotificationFactoryProxy)(object)factory).Providers = new List<INotification> { subject };

            new AudioBookShelfEditionSyncService(factory, files, LogManager.GetLogger("test"))
                .Execute(new AudioBookShelfEditionSyncCommand { BookIds = new List<int> { 5 } });

            Assert.That(pins.Calls, Is.EqualTo(new[]
            {
                (System.IO.Path.Combine("/audiobooks", "Joe Abercrombie", "The Blade Itself"), "audiobook", "B000BLADE1")
            }));
            Assert.That(((BookFilesProxy)(object)files).RequestedBookIds, Is.EqualTo(new[] { 5 }));
        }

        [Test]
        public void scoped_sync_should_do_nothing_when_chaptarr_keeps_the_metadata()
        {
            var proxy = BuildPushProxy();
            proxy.Items[0].Media.Metadata = new AudioBookShelfLibraryItemBookMetadata { Asin = "B000BLADE1" };
            var pins = new RecordingEditionPinService();
            var subject = CreatePushSubject(proxy, pins);

            var outcomes = subject.SyncEditionsForFolders(new List<string> { "/audiobooks/Joe Abercrombie/The Blade Itself" });

            Assert.That(pins.Calls, Is.Empty);
            Assert.That(outcomes, Is.Empty);
        }

        private class BookFilesProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new();
            public List<int> RequestedBookIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMediaFileService.GetFilesByBooks))
                {
                    RequestedBookIds.AddRange((List<int>)args[0]);
                    return Files;
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod.Name}");
            }
        }

        private class NotificationFactoryProxy : DispatchProxy
        {
            public List<INotification> Providers { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(INotificationFactory.GetAvailableProviders))
                {
                    return Providers;
                }

                throw new NotImplementedException($"Test proxy does not implement INotificationFactory.{targetMethod.Name}");
            }
        }

        private class RecordingEditionPinService : IAudioBookShelfEditionPinService
        {
            public List<(string Folder, string MediaType, string Asin)> Calls { get; } = new();

            public AudioBookShelfEditionPinOutcome PinFolder(string folder, string mediaType, string asin, ISet<int> connectionTags = null)
            {
                Calls.Add((folder, mediaType, asin));
                return AudioBookShelfEditionPinOutcome.Pinned;
            }
        }

        private static FakeAudioBookShelfProxy BuildPushProxy(params string[] itemTags)
        {
            return new FakeAudioBookShelfProxy
            {
                Libraries = new List<AudioBookShelfLibrary>
                {
                    BuildLibrary("library-audio", "folder-audio", "/abs/audio", disableWatcher: false)
                },
                Items = new List<AudioBookShelfLibraryItemSummary>
                {
                    new AudioBookShelfLibraryItemSummary
                    {
                        Id = "item-1",
                        RelPath = "Joe Abercrombie/The Blade Itself",
                        Media = new AudioBookShelfLibraryItemMedia { Tags = itemTags.ToList() }
                    }
                }
            };
        }

        private static NzbDrone.Core.Notifications.AudioBookShelf.AudioBookShelf CreatePushSubject(FakeAudioBookShelfProxy proxy, params string[] ignoreTags)
        {
            return CreatePushSubject(proxy, null, ignoreTags);
        }

        private static NzbDrone.Core.Notifications.AudioBookShelf.AudioBookShelf CreatePushSubject(FakeAudioBookShelfProxy proxy, IAudioBookShelfEditionPinService editionPinService, params string[] ignoreTags)
        {
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
            }, editionPinService);

            ((AudioBookShelfSettings)subject.Definition.Settings).IgnoreTags = ignoreTags;
            return subject;
        }

        private static List<(Book Book, List<BookFile> Files)> BuildPushableBook()
        {
            return new List<(Book Book, List<BookFile> Files)>
            {
                (new Book { Title = "The Blade Itself", MediaType = BookMediaType.Audiobook },
                 new List<BookFile>
                 {
                     new BookFile
                     {
                         Path = "/audiobooks/Joe Abercrombie/The Blade Itself/The Blade Itself.m4b",
                         MediaType = "audiobook"
                     }
                 })
            };
        }

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

        private static NzbDrone.Core.Notifications.AudioBookShelf.AudioBookShelf CreateSubject(FakeAudioBookShelfProxy proxy, List<RootFolder> rootFolders, List<AudioBookShelfLibraryMapping> mappings, IAudioBookShelfEditionPinService editionPinService = null)
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
                bookService: null,
                editionService: null,
                editionPinService: editionPinService,
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

            public List<AudioBookShelfLibraryItemSummary> Items { get; set; } = new List<AudioBookShelfLibraryItemSummary>();
            public List<string> MetadataUpdates { get; } = new List<string>();

            public Dictionary<string, List<AudioBookShelfLibraryItemSummary>> ItemsByLibrary { get; } = new();

            public List<AudioBookShelfLibraryItemSummary> GetLibraryItems(AudioBookShelfSettings settings, string libraryId) =>
                ItemsByLibrary.TryGetValue(libraryId, out var items) ? items : Items;
            public void ScanItem(AudioBookShelfSettings settings, string itemId) { }
            public void UpdateItemMetadata(AudioBookShelfSettings settings, string itemId, AudioBookShelfItemMetadata metadata)
            {
                MetadataUpdates.Add(itemId);
            }
            public List<string> CoverUpdates { get; } = new List<string>();
            public void UpdateItemCover(AudioBookShelfSettings settings, string itemId, string coverPath)
            {
                CoverUpdates.Add(itemId);
            }

            public void UploadItemCover(AudioBookShelfSettings settings, string itemId, byte[] image, string fileName)
            {
                CoverUpdates.Add(itemId);
            }

            public void PurgeCoverCache(AudioBookShelfSettings settings) { }
            public void RemoveItemsWithIssues(AudioBookShelfSettings settings, string libraryId) { }
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
