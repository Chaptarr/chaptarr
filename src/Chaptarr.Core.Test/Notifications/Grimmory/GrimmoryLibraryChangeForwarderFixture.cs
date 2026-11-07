using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Grimmory;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Notifications.Grimmory
{
    [TestFixture]
    public class GrimmoryLibraryChangeForwarderFixture
    {
        private const long EbookLibraryId = 3;
        private const string RelativePath = "Robin Hobb/Assassin's Apprentice/Assassin's Apprentice.epub";
        private const string AudiobookFolder = "Assassin's Apprentice (Unabridged)";

        [SetUp]
        public void Setup()
        {
            GrimmoryPushRegistry.Clear();
            GrimmoryPushRegistry.EchoShadow = TimeSpan.Zero;
        }

        [TearDown]
        public void TearDown()
        {
            GrimmoryPushRegistry.EchoShadow = TimeSpan.FromSeconds(15);
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

        private class ScriptedGrimmoryProxy : IGrimmoryProxy
        {
            public Dictionary<string, GrimmoryBook> BooksByPath { get; } = new Dictionary<string, GrimmoryBook>(StringComparer.OrdinalIgnoreCase);
            public List<(long BookId, Dictionary<string, object> Metadata)> MetadataUpdates { get; } = new List<(long, Dictionary<string, object>)>();

            public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings) => new List<GrimmoryLibrary>();
            public void RefreshLibrary(GrimmorySettings settings, long libraryId) { }

            public GrimmoryBook FindBookByPath(GrimmorySettings settings, long libraryId, string relativePath, bool bypassCache = false)
            {
                return BooksByPath.TryGetValue(relativePath.Replace('\\', '/'), out var book) ? book : null;
            }

            public void UpdateBookMetadata(GrimmorySettings settings, long bookId, Dictionary<string, object> metadata) => MetadataUpdates.Add((bookId, metadata));
            public void UploadBookCover(GrimmorySettings settings, GrimmoryBook book, byte[] image, string fileName) { }
            public byte[] GetBookCover(GrimmorySettings settings, GrimmoryBook book) => new byte[] { 9 };
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
            public ScriptedGrimmoryProxy Proxy;
            public ScriptedGrimmoryProxy SiblingProxy;
            public GrimmoryLibraryChangeForwarder Forwarder;
            public TestEditTarget Target;
            public string SidecarPath;
            public string CoverSidecarPath;
        }

        private static GrimmoryBook BuildGrimmoryBook()
        {
            var slash = RelativePath.LastIndexOf('/');

            return new GrimmoryBook
            {
                Id = 100,
                LibraryId = EbookLibraryId,
                PrimaryFile = new GrimmoryBookFile
                {
                    FileSubPath = RelativePath.Substring(0, slash),
                    FileName = RelativePath.Substring(slash + 1)
                },
                Metadata = new GrimmoryBookMetadata
                {
                    Title = "Assassin's Apprentice",
                    Description = "Edited in Grimmory.",
                    Publisher = "Voyager",
                    SeriesName = "Farseer",
                    SeriesNumber = 1,
                    Language = "eng",
                    Isbn13 = "9780007562252",
                    Categories = new List<string> { "fantasy" }
                }
            };
        }

        private static string TrackRelativePath(string folder) => $"Robin Hobb/{folder}/Assassin's Apprentice (001).mp3";

        private static Context CreateContext(int targetDefinitionId = 2, bool withSibling = false, string audiobookFolder = null)
        {
            var folderAudiobook = audiobookFolder != null;
            var context = new Context();
            var proxy = new ScriptedGrimmoryProxy();
            context.Proxy = proxy;

            var settings = new GrimmorySettings
            {
                Url = "http://grimmory:6060",
                Username = "chaptarr",
                Password = "secret",
                EbookLibraryId = EbookLibraryId,
                AudiobookLibraryId = EbookLibraryId,
                ForwardEdits = true
            };

            var commandQueue = Stub<IManageCommandQueue>(out var commandStub);
            commandStub.Handlers["Push"] = _ => null;

            var rootPath = @"C:\books".AsOsAgnostic();
            var authorDir = Path.Combine(rootPath, "Robin Hobb");
            var bookDir = Path.Combine(authorDir, audiobookFolder ?? "Assassin's Apprentice");
            var bookFilePath = Path.Combine(bookDir, folderAudiobook ? "Assassin's Apprentice (001).mp3" : "Assassin's Apprentice.epub");
            var sidecarDir = folderAudiobook ? authorDir : bookDir;
            var sidecarBaseName = audiobookFolder ?? "Assassin's Apprentice";
            context.SidecarPath = Path.Combine(sidecarDir, sidecarBaseName + ".metadata.json");
            context.CoverSidecarPath = Path.Combine(sidecarDir, sidecarBaseName + ".cover.jpg");

            var bookFile = new BookFile { Id = 40, EditionId = 30, Path = bookFilePath, MediaType = folderAudiobook ? "audiobook" : "ebook" };

            var rootFolderService = Stub<IRootFolderService>(out var rootStub);
            rootStub.Handlers["All"] = _ => new List<RootFolder> { new RootFolder { Id = 1, Path = rootPath } };
            rootStub.Handlers["GetBestRootFolder"] = _ => new RootFolder { Id = 1, Path = rootPath };

            var source = new NzbDrone.Core.Notifications.Grimmory.Grimmory(proxy, commandQueue, rootFolderService, new CacheManager(), LogManager.GetLogger("test"))
            {
                Definition = new NotificationDefinition { Id = 1, Name = "Grimmory", Settings = settings }
            };

            var target = new TestEditTarget
            {
                Definition = new NotificationDefinition { Id = targetDefinitionId, Name = "TestTarget", Settings = new GrimmorySettings() }
            };
            context.Target = target;

            var providers = new List<INotification> { source, target };

            if (withSibling)
            {
                var siblingProxy = new ScriptedGrimmoryProxy();
                context.SiblingProxy = siblingProxy;

                var siblingSettings = new GrimmorySettings
                {
                    Url = "http://grimmory-b:6060",
                    Username = "chaptarr",
                    Password = "secret",
                    EbookLibraryId = EbookLibraryId,
                    PushMetadata = true
                };

                providers.Add(new NzbDrone.Core.Notifications.Grimmory.Grimmory(siblingProxy, commandQueue, rootFolderService, new CacheManager(), LogManager.GetLogger("test"))
                {
                    Definition = new NotificationDefinition { Id = 3, Name = "Grimmory B", Settings = siblingSettings }
                });
            }

            var factory = Stub<INotificationFactory>(out var factoryStub);
            factoryStub.Handlers["GetAvailableProviders"] = _ => providers;

            var mediaFileService = Stub<IMediaFileService>(out var mediaFileStub);
            mediaFileStub.Handlers["GetFilesWithBasePath"] = args => ((string)args[0]).IsParentPath(bookFilePath)
                ? new List<BookFile> { bookFile }
                : new List<BookFile>();
            mediaFileStub.Handlers["GetFilesByBook"] = _ => new List<BookFile> { bookFile };

            var editionService = Stub<IEditionService>(out var editionStub);
            editionStub.Handlers["GetEdition"] = args => (int)args[0] == 30 ? new Edition { Id = 30, BookId = 10 } : null;

            var bookService = Stub<IBookService>(out var bookStub);
            bookStub.Handlers["GetBook"] = args => (int)args[0] == 10
                ? new Book { Id = 10, Title = "Assassin's Apprentice", MediaType = folderAudiobook ? BookMediaType.Audiobook : BookMediaType.Ebook }
                : null;

            context.Forwarder = new GrimmoryLibraryChangeForwarder(
                factory,
                proxy,
                rootFolderService,
                mediaFileService,
                editionService,
                bookService,
                LogManager.GetLogger("GrimmoryLibraryChangeForwarderFixture"));

            return context;
        }

        [Test]
        public void should_forward_edit_signalled_by_metadata_sidecar()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            var (book, payload) = context.Target.Pushes[0];

            Assert.Multiple(() =>
            {
                Assert.That(book.Id, Is.EqualTo(10));
                Assert.That(payload.Title, Is.EqualTo("Assassin's Apprentice"));
                Assert.That(payload.Description, Is.EqualTo("Edited in Grimmory."));
                Assert.That(payload.SeriesName, Is.EqualTo("Farseer"));
                Assert.That(payload.Identifiers["isbn"], Is.EqualTo("9780007562252"));
                Assert.That(payload.CoverBytes, Is.Not.Null);
            });

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_forward_edit_signalled_by_cover_sidecar()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            context.Forwarder.QueueSidecar(context.CoverSidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_dedupe_metadata_and_cover_sidecars_for_same_book()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.QueueSidecar(context.CoverSidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_not_forward_sidecar_written_after_chaptarrs_own_push()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            GrimmoryPushRegistry.RecordPush(10);

            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Is.Empty);

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_forward_edit_made_after_push_echo_was_consumed()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            GrimmoryPushRegistry.RecordPush(10);

            // Grimmory rewriting the sidecar in response to Chaptarr's own push - suppressed.
            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();
            Assert.That(context.Target.Pushes, Is.Empty);

            // A person's edit right after - the push entry is spent, so this forwards.
            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();
            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_forward_edit_coalesced_into_the_same_batch_as_a_push_echo()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            GrimmoryPushRegistry.RecordPush(10);

            // The push's echo and a person's edit land inside one debounce window: the echo
            // is dropped at arrival, so the edit still comes out of the shared batch.
            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_resolve_sidecar_written_beside_a_folder_audiobook()
        {
            var context = CreateContext(audiobookFolder: AudiobookFolder);
            context.Proxy.BooksByPath[TrackRelativePath(AudiobookFolder)] = BuildGrimmoryBook();

            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));
            Assert.That(context.Target.Pushes[0].Book.MediaType, Is.EqualTo(BookMediaType.Audiobook));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_resolve_folder_audiobook_sidecar_trimmed_at_the_folders_last_dot()
        {
            var context = CreateContext(audiobookFolder: "Assassin's Apprentice Vol. 1");
            context.Proxy.BooksByPath[TrackRelativePath("Assassin's Apprentice Vol. 1")] = BuildGrimmoryBook();

            var trimmedSidecar = Path.Combine(Path.GetDirectoryName(context.SidecarPath), "Assassin's Apprentice Vol.metadata.json");

            context.Forwarder.QueueSidecar(trimmedSidecar);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Has.Count.EqualTo(1));

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_ignore_sidecar_without_matching_book_file()
        {
            var context = CreateContext();
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            context.Forwarder.QueueSidecar(Path.Combine(@"C:\books".AsOsAgnostic(), "Unknown", "Unknown.metadata.json"));
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Is.Empty);

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_forward_edit_to_sibling_grimmory_connection()
        {
            var context = CreateContext(withSibling: true);
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();
            context.SiblingProxy.BooksByPath[RelativePath] = new GrimmoryBook
            {
                Id = 500,
                LibraryId = EbookLibraryId,
                PrimaryFile = BuildGrimmoryBook().PrimaryFile
            };

            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.SiblingProxy.MetadataUpdates, Has.Count.EqualTo(1));

            var (bookId, metadata) = context.SiblingProxy.MetadataUpdates[0];

            Assert.Multiple(() =>
            {
                Assert.That(bookId, Is.EqualTo(500));
                Assert.That(metadata["description"], Is.EqualTo("Edited in Grimmory."));
                Assert.That(metadata["seriesName"], Is.EqualTo("Farseer"));
                Assert.That(context.Proxy.MetadataUpdates, Is.Empty, "the source instance must not receive its own edit back");
            });

            context.Forwarder.Dispose();
        }

        [Test]
        public void should_skip_target_sharing_the_sources_definition()
        {
            var context = CreateContext(targetDefinitionId: 1);
            context.Proxy.BooksByPath[RelativePath] = BuildGrimmoryBook();

            context.Forwarder.QueueSidecar(context.SidecarPath);
            context.Forwarder.ForwardPending();

            Assert.That(context.Target.Pushes, Is.Empty);

            context.Forwarder.Dispose();
        }
    }
}
