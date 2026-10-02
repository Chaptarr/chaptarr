using System;
using System.Collections.Generic;
using System.Reflection;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Grimmory;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Notifications.Grimmory
{
    [TestFixture]
    public class GrimmoryFixture
    {
        private const long EbookLibraryId = 10;
        private const long AudiobookLibraryId = 20;

        [Test]
        public void definition_should_be_enabled_by_push_or_forward_toggles_without_any_trigger()
        {
            Assert.Multiple(() =>
            {
                Assert.That(new NotificationDefinition { Implementation = "Grimmory", Settings = new GrimmorySettings() }.Enable, Is.False);
                Assert.That(new NotificationDefinition { Implementation = "Grimmory", Settings = new GrimmorySettings { PushMetadata = true } }.Enable, Is.True);
                Assert.That(new NotificationDefinition { Implementation = "Grimmory", Settings = new GrimmorySettings { PushCovers = true } }.Enable, Is.True);
                Assert.That(new NotificationDefinition { Implementation = "Grimmory", Settings = new GrimmorySettings { ForwardEdits = true } }.Enable, Is.True);
            });
        }

        [Test]
        public void should_not_refresh_at_event_time_and_refresh_on_process_queue()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnReleaseImport(BuildImport(BookMediaType.Ebook));

            Assert.That(proxy.RefreshedLibraryIds, Is.Empty);
            Assert.That(subject.HasPendingQueue, Is.True);

            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EqualTo(new List<long> { EbookLibraryId }));
            Assert.That(subject.HasPendingQueue, Is.False);
        }

        [Test]
        public void should_notify_on_library_imports_only_when_pushing()
        {
            var subject = CreateSubject(new FakeGrimmoryProxy());
            var settings = (GrimmorySettings)subject.Definition.Settings;

            Assert.That(subject.NotifyOnLibraryImports, Is.False);

            settings.PushMetadata = true;
            Assert.That(subject.NotifyOnLibraryImports, Is.True);

            settings.PushMetadata = false;
            settings.PushCovers = true;
            Assert.That(subject.NotifyOnLibraryImports, Is.True);
        }

        [Test]
        public void should_dedupe_multiple_events_into_single_refresh()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnReleaseImport(BuildImport(BookMediaType.Ebook));
            subject.OnReleaseImport(BuildImport(BookMediaType.Ebook));
            subject.OnBookFileDelete(new BookFileDeleteMessage
            {
                Book = new Book { MediaType = BookMediaType.Ebook },
                BookFile = new BookFile { MediaType = "ebook" }
            });

            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EqualTo(new List<long> { EbookLibraryId }));
        }

        [Test]
        public void should_route_audiobook_events_to_audiobook_library()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnReleaseImport(BuildImport(BookMediaType.Audiobook));
            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EqualTo(new List<long> { AudiobookLibraryId }));
        }

        [Test]
        public void should_skip_events_for_unconfigured_library()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy, audiobookLibraryId: 0);

            subject.OnReleaseImport(BuildImport(BookMediaType.Audiobook));

            Assert.That(subject.HasPendingQueue, Is.False);

            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.Empty);
        }

        [Test]
        public void should_refresh_both_libraries_for_mixed_renames()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnRename(new Author { Name = "Robin Hobb" }, new List<RenamedBookFile>
            {
                new RenamedBookFile { BookFile = new BookFile { MediaType = "ebook" } },
                new RenamedBookFile { BookFile = new BookFile { MediaType = "audiobook" } }
            });

            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EquivalentTo(new List<long> { EbookLibraryId, AudiobookLibraryId }));
        }

        [Test]
        public void should_determine_media_type_from_quality_when_not_set_on_file()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnRename(new Author { Name = "Robin Hobb" }, new List<RenamedBookFile>
            {
                new RenamedBookFile { BookFile = new BookFile { MediaType = null, Quality = new QualityModel(Quality.EPUB) } }
            });

            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EqualTo(new List<long> { EbookLibraryId }));
        }

        [Test]
        public void should_not_queue_book_delete_without_deleted_files()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnBookDelete(new BookDeleteMessage(new Book { MediaType = BookMediaType.Ebook }, false));

            Assert.That(subject.HasPendingQueue, Is.False);
        }

        [Test]
        public void should_queue_both_configured_libraries_on_author_delete()
        {
            var proxy = new FakeGrimmoryProxy();
            var subject = CreateSubject(proxy);

            subject.OnAuthorDelete(new AuthorDeleteMessage(new Author { Name = "Robin Hobb" }, true));
            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EquivalentTo(new List<long> { EbookLibraryId, AudiobookLibraryId }));
        }

        [Test]
        public void should_throw_and_still_refresh_remaining_when_one_library_fails()
        {
            var proxy = new FakeGrimmoryProxy();
            proxy.FailingLibraryIds.Add(EbookLibraryId);

            var subject = CreateSubject(proxy);

            subject.OnReleaseImport(BuildImport(BookMediaType.Ebook));
            subject.OnReleaseImport(BuildImport(BookMediaType.Audiobook));

            Assert.Throws<InvalidOperationException>(() => subject.ProcessQueue());
            Assert.That(proxy.RefreshedLibraryIds, Is.EqualTo(new List<long> { AudiobookLibraryId }));
        }

        [Test]
        public void should_queue_again_after_process_queue_failure()
        {
            var proxy = new FakeGrimmoryProxy();
            proxy.FailingLibraryIds.Add(EbookLibraryId);

            var subject = CreateSubject(proxy);

            subject.OnReleaseImport(BuildImport(BookMediaType.Ebook));
            Assert.Throws<InvalidOperationException>(() => subject.ProcessQueue());

            proxy.FailingLibraryIds.Clear();

            subject.OnReleaseImport(BuildImport(BookMediaType.Ebook));
            subject.ProcessQueue();

            Assert.That(proxy.RefreshedLibraryIds, Is.EqualTo(new List<long> { EbookLibraryId }));
        }

        private static BookDownloadMessage BuildImport(BookMediaType mediaType)
        {
            var fileMediaType = mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";

            return new BookDownloadMessage
            {
                Author = new Author { Name = "Robin Hobb" },
                Book = new Book { Title = "Assassin's Apprentice", MediaType = mediaType },
                BookFiles = new List<BookFile>
                {
                    new BookFile { MediaType = fileMediaType }
                }
            };
        }

        private static NzbDrone.Core.Notifications.Grimmory.Grimmory CreateSubject(FakeGrimmoryProxy proxy,
                                                                                    long ebookLibraryId = EbookLibraryId,
                                                                                    long audiobookLibraryId = AudiobookLibraryId)
        {
            var settings = new GrimmorySettings
            {
                Url = "http://grimmory:6060",
                Username = "chaptarr",
                Password = "secret",
                EbookLibraryId = ebookLibraryId,
                AudiobookLibraryId = audiobookLibraryId
            };

            return new NzbDrone.Core.Notifications.Grimmory.Grimmory(
                proxy,
                DispatchProxy.Create<IManageCommandQueue, InertCommandQueueProxy>(),
                null,
                new CacheManager(),
                LogManager.GetLogger("GrimmoryFixture"))
            {
                Definition = new NotificationDefinition { Settings = settings }
            };
        }

        public class InertCommandQueueProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return null;
            }
        }

        private class FakeGrimmoryProxy : IGrimmoryProxy
        {
            public List<GrimmoryLibrary> Libraries { get; set; } = new List<GrimmoryLibrary>();
            public List<long> RefreshedLibraryIds { get; } = new List<long>();
            public HashSet<long> FailingLibraryIds { get; } = new HashSet<long>();

            public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings)
            {
                return Libraries;
            }

            public void RefreshLibrary(GrimmorySettings settings, long libraryId)
            {
                if (FailingLibraryIds.Contains(libraryId))
                {
                    throw new InvalidOperationException($"Refresh failed for library {libraryId}");
                }

                RefreshedLibraryIds.Add(libraryId);
            }

            public ValidationFailure Test(GrimmorySettings settings)
            {
                return null;
            }

            public GrimmoryBook FindBookByPath(GrimmorySettings settings, long libraryId, string relativePath, bool bypassCache = false) => null;
            public void UpdateBookMetadata(GrimmorySettings settings, long bookId, Dictionary<string, object> metadata) { }
            public void UploadBookCover(GrimmorySettings settings, GrimmoryBook book, byte[] image, string fileName) { }
            public byte[] GetBookCover(GrimmorySettings settings, GrimmoryBook book) => null;
            public string BuildCoverUrl(GrimmorySettings settings, GrimmoryBook book) => string.Empty;
        }
    }
}
