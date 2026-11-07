using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Grimmory;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Notifications.Grimmory
{
    [TestFixture]
    public class GrimmoryPushServiceFixture
    {
        private const long EbookLibraryId = 3;
        private const long AudiobookLibraryId = 4;

        [SetUp]
        public void Setup()
        {
            GrimmoryPushRegistry.Clear();
        }

        public class StubProxy : DispatchProxy
        {
            public Dictionary<string, Func<object[], object>> Handlers { get; } = new Dictionary<string, Func<object[], object>>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handlers.TryGetValue(targetMethod.Name, out var handler))
                {
                    return handler(args);
                }

                throw new NotImplementedException($"Stub does not handle {targetMethod.Name}");
            }
        }

        private static T Stub<T>(out StubProxy stub)
        {
            var proxy = DispatchProxy.Create<T, StubProxy>();
            stub = (StubProxy)(object)proxy;
            return proxy;
        }

        private class FakeGrimmoryProxy : IGrimmoryProxy
        {
            public Dictionary<string, GrimmoryBook> BooksByPath { get; } = new Dictionary<string, GrimmoryBook>(StringComparer.OrdinalIgnoreCase);
            public List<(long BookId, Dictionary<string, object> Metadata)> MetadataUpdates { get; } = new List<(long, Dictionary<string, object>)>();
            public List<(long BookId, string FileName)> CoverUploads { get; } = new List<(long, string)>();
            public Exception CoverUploadFailure { get; set; }

            public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings) => new List<GrimmoryLibrary>();
            public void RefreshLibrary(GrimmorySettings settings, long libraryId) { }

            public GrimmoryBook FindBookByPath(GrimmorySettings settings, long libraryId, string relativePath, bool bypassCache = false)
            {
                return BooksByPath.TryGetValue(relativePath.Replace('\\', '/'), out var book) ? book : null;
            }

            public void UpdateBookMetadata(GrimmorySettings settings, long bookId, Dictionary<string, object> metadata) => MetadataUpdates.Add((bookId, metadata));
            public void UploadBookCover(GrimmorySettings settings, GrimmoryBook book, byte[] image, string fileName)
            {
                if (CoverUploadFailure != null)
                {
                    throw CoverUploadFailure;
                }

                CoverUploads.Add((book.Id, fileName));
            }

            public byte[] GetBookCover(GrimmorySettings settings, GrimmoryBook book) => null;
            public string BuildCoverUrl(GrimmorySettings settings, GrimmoryBook book) => $"http://grimmory/cover/{book.Id}";
            public ValidationFailure Test(GrimmorySettings settings) => null;
        }

        private class TestEditTarget : NotificationBase<GrimmorySettings>, IExternalLibraryEditTarget
        {
            public List<(Book Book, ExternalLibraryEditPayload Payload)> Pushes { get; } = new List<(Book, ExternalLibraryEditPayload)>();

            public override string Name => "TestTarget";
            public override string Link => string.Empty;
            public bool AcceptsExternalLibraryEdits => true;

            public void PushExternalLibraryEdit(Book book, List<BookFile> files, ExternalLibraryEditPayload payload)
            {
                Pushes.Add((book, payload));
            }

            public override ValidationResult Test()
            {
                return new ValidationResult();
            }
        }

        private class Context
        {
            public FakeGrimmoryProxy Proxy;
            public GrimmoryPushService Service;
            public List<Command> PushedCommands = new List<Command>();
            public GrimmorySettings Settings;
            public TestEditTarget Target;
        }

        private static Context CreateContext(bool pushMetadata = true, bool pushCovers = true, string coverPath = null)
        {
            var context = new Context();
            var proxy = new FakeGrimmoryProxy();
            context.Proxy = proxy;

            var settings = new GrimmorySettings
            {
                Url = "http://grimmory:6060",
                Username = "chaptarr",
                Password = "secret",
                EbookLibraryId = EbookLibraryId,
                AudiobookLibraryId = AudiobookLibraryId,
                PushMetadata = pushMetadata,
                PushCovers = pushCovers
            };
            context.Settings = settings;

            var commandQueue = Stub<IManageCommandQueue>(out var commandStub);
            commandStub.Handlers["Push"] = args =>
            {
                context.PushedCommands.Add((Command)args[0]);
                return null;
            };

            var provider = new NzbDrone.Core.Notifications.Grimmory.Grimmory(proxy, commandQueue, null, new CacheManager(), LogManager.GetLogger("test"))
            {
                Definition = new NotificationDefinition { Id = 1, Name = "Grimmory", Settings = settings }
            };

            context.Target = new TestEditTarget
            {
                Definition = new NotificationDefinition { Id = 2, Name = "TestTarget", Settings = new GrimmorySettings() }
            };

            var factory = Stub<INotificationFactory>(out var factoryStub);
            factoryStub.Handlers["GetAvailableProviders"] = _ => new List<INotification> { provider, context.Target };

            var book = new Book
            {
                Id = 10,
                AuthorId = 20,
                Title = "Assassin's Apprentice",
                Overview = "A royal bastard trains as an assassin.",
                MediaType = BookMediaType.Ebook,
                Genres = new List<string> { "fantasy" }
            };

            var bookService = Stub<IBookService>(out var bookStub);
            bookStub.Handlers["GetBook"] = args => (int)args[0] == 10 ? book : null;

            var authorService = Stub<IAuthorService>(out var authorStub);
            authorStub.Handlers["GetAuthor"] = _ => new Author { Id = 20, Name = "Robin Hobb" };

            var edition = new Edition
            {
                Id = 30,
                BookId = 10,
                Title = "Assassin's Apprentice",
                Overview = "Edition overview.",
                Publisher = "Voyager",
                Language = "eng",
                Isbn13 = "9780007562252",
                Monitored = true,
                Images = new List<NzbDrone.Core.MediaCover.MediaCover> { new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "http://x/cover.jpg") }
            };

            var editionService = Stub<IEditionService>(out var editionStub);
            editionStub.Handlers["GetEditionsByBook"] = _ => new List<Edition> { edition };

            var mediaFileService = Stub<IMediaFileService>(out var mediaFileStub);
            mediaFileStub.Handlers["GetFilesByBook"] = _ => new List<BookFile>
            {
                new BookFile { Id = 40, EditionId = 30, Path = @"C:\books\Robin Hobb\Assassin's Apprentice\Assassin's Apprentice.epub".AsOsAgnostic(), MediaType = "ebook" }
            };

            var rootFolderService = Stub<IRootFolderService>(out var rootStub);
            rootStub.Handlers["GetBestRootFolder"] = _ => new RootFolder { Id = 1, Path = @"C:\books".AsOsAgnostic() };

            var coverMapper = Stub<IMapCoversToLocal>(out var coverStub);
            coverStub.Handlers["GetCoverPath"] = _ => coverPath ?? @"C:\nonexistent\cover.jpg".AsOsAgnostic();

            context.Service = new GrimmoryPushService(
                factory,
                proxy,
                bookService,
                authorService,
                editionService,
                mediaFileService,
                rootFolderService,
                coverMapper,
                commandQueue,
                new CacheManager(),
                LogManager.GetLogger("GrimmoryPushServiceFixture"));

            return context;
        }

        private static GrimmoryBook GrimmoryBookAt(string relativePath, long id = 100)
        {
            var slash = relativePath.LastIndexOf('/');

            return new GrimmoryBook
            {
                Id = id,
                LibraryId = EbookLibraryId,
                PrimaryFile = new GrimmoryBookFile
                {
                    FileSubPath = slash > 0 ? relativePath.Substring(0, slash) : string.Empty,
                    FileName = relativePath.Substring(slash + 1)
                }
            };
        }

        [Test]
        public void should_push_metadata_with_locks_to_matched_book()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title", "description", "authors", "identifiers", "tags" }
            });

            Assert.That(context.Proxy.MetadataUpdates, Has.Count.EqualTo(1));

            var (bookId, metadata) = context.Proxy.MetadataUpdates[0];

            Assert.Multiple(() =>
            {
                Assert.That(bookId, Is.EqualTo(100));
                Assert.That(metadata["title"], Is.EqualTo("Assassin's Apprentice"));
                Assert.That(metadata["titleLocked"], Is.True);
                Assert.That(metadata["description"], Is.EqualTo("Edition overview."));
                Assert.That(metadata["authors"], Is.EqualTo(new List<string> { "Robin Hobb" }));
                Assert.That(metadata["authorsLocked"], Is.True);
                Assert.That(metadata["categories"], Is.EqualTo(new List<string> { "fantasy" }));
                Assert.That(metadata["categoriesLocked"], Is.True);
                Assert.That(metadata["isbn13"], Is.EqualTo("9780007562252"));
                Assert.That(metadata["isbn13Locked"], Is.True);
                Assert.That(metadata.ContainsKey("publisher"), Is.False);
            });
        }

        [Test]
        public void should_record_push_in_registry_for_echo_suppression()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title" }
            });

            Assert.That(GrimmoryPushRegistry.WasRecentlyPushed(10), Is.True);
            Assert.That(GrimmoryPushRegistry.WasRecentlyPushed(11), Is.False);
        }

        [Test]
        public void should_not_record_push_in_registry_when_nothing_was_sent()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "cover" }
            });

            Assert.That(GrimmoryPushRegistry.WasRecentlyPushed(10), Is.False);
        }

        [Test]
        public void should_lock_the_audiobook_cover_when_grimmorys_primary_file_is_an_audiobook()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                var grimmoryBook = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");
                grimmoryBook.PrimaryFile.BookType = "AUDIOBOOK";
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = grimmoryBook;

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 },
                    Fields = new List<string> { "cover" }
                });

                Assert.That(context.Proxy.CoverUploads, Has.Count.EqualTo(1));
                Assert.That(context.Proxy.MetadataUpdates[0].Metadata.Keys, Is.EqualTo(new[] { "audiobookCoverLocked" }));
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        [Test]
        public void should_leave_a_locked_audiobook_cover_alone()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                var grimmoryBook = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");
                grimmoryBook.PrimaryFile.BookType = "AUDIOBOOK";
                grimmoryBook.Metadata = new GrimmoryBookMetadata { AudiobookCoverLocked = true };
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = grimmoryBook;

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 },
                    Fields = new List<string> { "cover" }
                });

                Assert.That(context.Proxy.CoverUploads, Is.Empty);
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        [Test]
        public void should_leave_a_locked_cover_alone()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                var grimmoryBook = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");
                grimmoryBook.Metadata = new GrimmoryBookMetadata { CoverLocked = true };
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = grimmoryBook;

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 },
                    Fields = new List<string> { "cover" }
                });

                Assert.That(context.Proxy.CoverUploads, Is.Empty);
                Assert.That(context.Proxy.MetadataUpdates, Is.Empty);
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        [Test]
        public void should_mirror_push_to_other_edit_targets()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 },
                    Fields = new List<string> { "description", "publisher", "tags", "cover" }
                });

                Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

                var payload = context.Target.Pushes[0].Payload;

                Assert.Multiple(() =>
                {
                    Assert.That(payload.Description, Is.EqualTo("Edition overview."));
                    Assert.That(payload.Publisher, Is.EqualTo("Voyager"));
                    Assert.That(payload.Genres, Is.EqualTo(new List<string> { "fantasy" }));
                    Assert.That(payload.CoverBytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
                    Assert.That(payload.Title, Is.Null);
                });
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        private const string ApprenticePath = "Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub";

        private static Context CreateIgnoringContext()
        {
            var context = CreateContext();
            context.Settings.IgnoreTags = new[] { "Processed" };

            var grimmoryBook = GrimmoryBookAt(ApprenticePath);
            grimmoryBook.Metadata = new GrimmoryBookMetadata { Tags = new List<string> { "Fantasy", "processed" } };
            context.Proxy.BooksByPath[ApprenticePath] = grimmoryBook;

            return context;
        }

        [Test]
        public void automatic_push_should_skip_a_book_with_an_ignore_tag_but_still_mirror()
        {
            var context = CreateIgnoringContext();

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title", "description" }
            });

            Assert.Multiple(() =>
            {
                Assert.That(context.Proxy.MetadataUpdates, Is.Empty);
                Assert.That(GrimmoryPushRegistry.WasRecentlyPushed(10), Is.False);
                Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));
                Assert.That(context.Target.Pushes[0].Payload.Manual, Is.False);
            });
        }

        [Test]
        public void manual_push_should_update_a_book_with_an_ignore_tag()
        {
            var context = CreateIgnoringContext();
            var command = new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title", "description" }
            };
            command.Trigger = CommandTrigger.Manual;

            context.Service.Execute(command);

            Assert.Multiple(() =>
            {
                Assert.That(context.Proxy.MetadataUpdates, Has.Count.EqualTo(1));
                Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));
                Assert.That(context.Target.Pushes[0].Payload.Manual, Is.True);
            });
        }

        [Test]
        public void automatic_push_should_still_update_books_without_an_ignore_tag()
        {
            var context = CreateContext();
            context.Settings.IgnoreTags = new[] { "Processed" };
            context.Proxy.BooksByPath[ApprenticePath] = GrimmoryBookAt(ApprenticePath);

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title" }
            });

            Assert.That(context.Proxy.MetadataUpdates, Has.Count.EqualTo(1));
        }

        [TestCase(false, 0)]
        [TestCase(true, 1)]
        public void forwarded_edit_should_reach_a_book_with_an_ignore_tag_only_when_manual(bool manual, int expectedUpdates)
        {
            var context = CreateIgnoringContext();

            var rootFolderService = Stub<IRootFolderService>(out var rootStub);
            rootStub.Handlers["GetBestRootFolder"] = _ => new RootFolder { Id = 1, Path = @"C:\books".AsOsAgnostic() };

            var provider = new NzbDrone.Core.Notifications.Grimmory.Grimmory(context.Proxy, Stub<IManageCommandQueue>(out _), rootFolderService, new CacheManager(), LogManager.GetLogger("test"))
            {
                Definition = new NotificationDefinition { Id = 1, Name = "Grimmory", Settings = context.Settings }
            };

            provider.PushExternalLibraryEdit(
                new Book { Id = 10, Title = "Assassin's Apprentice", MediaType = BookMediaType.Ebook },
                new List<BookFile> { new BookFile { Path = @"C:\books\Robin Hobb\Assassin's Apprentice\Assassin's Apprentice.epub".AsOsAgnostic(), MediaType = "ebook" } },
                new ExternalLibraryEditPayload { Description = "Edited", Manual = manual });

            Assert.That(context.Proxy.MetadataUpdates, Has.Count.EqualTo(expectedUpdates));
        }

        [Test]
        public void should_not_mirror_push_when_nothing_was_pushed_to_grimmory()
        {
            var context = CreateContext();

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title" }
            });

            Assert.That(context.Target.Pushes, Is.Empty);
        }

        [Test]
        public void should_skip_when_book_not_found_in_grimmory()
        {
            var context = CreateContext();

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "title" }
            });

            Assert.That(context.Proxy.MetadataUpdates, Is.Empty);
        }

        [Test]
        public void should_upload_cover_when_cover_field_selected_and_file_exists()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 },
                    Fields = new List<string> { "cover" }
                });

                Assert.That(context.Proxy.CoverUploads, Has.Count.EqualTo(1));
                Assert.That(context.Proxy.MetadataUpdates, Has.Count.EqualTo(1));
                Assert.That(context.Proxy.MetadataUpdates[0].Metadata.Keys, Is.EqualTo(new[] { "coverLocked" }));
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        [Test]
        public void should_still_push_metadata_when_grimmory_rejects_the_cover()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                context.Proxy.CoverUploadFailure = new InvalidOperationException("500 An unexpected error occurred.");
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 }
                });

                Assert.That(context.Proxy.CoverUploads, Is.Empty);
                Assert.That(context.Proxy.MetadataUpdates, Has.Count.EqualTo(1));
                Assert.That(context.Proxy.MetadataUpdates[0].Metadata.Keys, Does.Contain("title"));
                Assert.That(context.Proxy.MetadataUpdates[0].Metadata.Keys, Does.Not.Contain("coverLocked"));
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        [Test]
        public void should_still_mirror_to_other_targets_when_grimmory_rejects_a_cover_only_push()
        {
            var coverFile = Path.GetTempFileName();
            File.WriteAllBytes(coverFile, new byte[] { 1, 2, 3 });

            try
            {
                var context = CreateContext(coverPath: coverFile);
                context.Proxy.CoverUploadFailure = new InvalidOperationException("500 An unexpected error occurred.");
                context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

                context.Service.Execute(new PushGrimmoryMetadataCommand
                {
                    BookIds = new List<int> { 10 },
                    Fields = new List<string> { "cover" }
                });

                Assert.That(context.Proxy.MetadataUpdates, Is.Empty);
                Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));
            }
            finally
            {
                File.Delete(coverFile);
            }
        }

        [Test]
        public void should_skip_cover_upload_when_cover_file_missing()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath["Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub"] = GrimmoryBookAt("Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub");

            context.Service.Execute(new PushGrimmoryMetadataCommand
            {
                BookIds = new List<int> { 10 },
                Fields = new List<string> { "cover" }
            });

            Assert.That(context.Proxy.CoverUploads, Is.Empty);
        }

        [Test]
        public void should_queue_push_command_for_book_scoped_cover_event()
        {
            var context = CreateContext();

            context.Service.Handle(new MediaCoversUpdatedEvent(new Book { Id = 10 }));

            Assert.That(context.PushedCommands.OfType<PushGrimmoryMetadataCommand>().Count(), Is.EqualTo(1));

            var command = context.PushedCommands.OfType<PushGrimmoryMetadataCommand>().Single();
            Assert.That(command.BookIds, Is.EqualTo(new List<int> { 10 }));
            Assert.That(command.Fields, Does.Contain("cover"));
            Assert.That(command.Fields, Does.Contain("title"));
        }

        [Test]
        public void should_not_queue_push_for_author_scoped_cover_event()
        {
            var context = CreateContext();

            context.Service.Handle(new MediaCoversUpdatedEvent(new Author { Id = 20 }));

            Assert.That(context.PushedCommands, Is.Empty);
        }

        [Test]
        public void should_not_queue_push_when_toggles_disabled()
        {
            var context = CreateContext(pushMetadata: false, pushCovers: false);

            context.Service.Handle(new MediaCoversUpdatedEvent(new Book { Id = 10 }));

            Assert.That(context.PushedCommands, Is.Empty);
        }

        [Test]
        public void should_dedupe_repeated_cover_events_for_same_book()
        {
            var context = CreateContext();

            context.Service.Handle(new MediaCoversUpdatedEvent(new Book { Id = 10 }));
            context.Service.Handle(new MediaCoversUpdatedEvent(new Book { Id = 10 }));

            Assert.That(context.PushedCommands, Has.Count.EqualTo(1));
        }

        [Test]
        public void toggle_fields_should_reflect_settings()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GrimmoryPushService.ToggleFields(new GrimmorySettings { PushCovers = true }), Is.EqualTo(new List<string> { "cover" }));
                Assert.That(GrimmoryPushService.ToggleFields(new GrimmorySettings { PushMetadata = true }), Does.Not.Contain("cover"));
                Assert.That(GrimmoryPushService.ToggleFields(new GrimmorySettings()), Is.Empty);
            });
        }
    }
}
