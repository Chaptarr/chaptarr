using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    /// <summary>
    /// A download whose every file is a format the profile never allowed is a bad grab, not a broken
    /// import: retrying re-reads the same files and reaches the same answer. It has to be failable so
    /// the release is blocklisted and replaced, instead of sitting in the queue forever. These pin
    /// the classifier that separates that case from every other import failure.
    /// </summary>
    [TestFixture]
    public class CompletedDownloadServicePayloadMismatchFixture
    {
        [Test]
        public void should_detect_a_payload_with_no_profile_allowed_format()
        {
            var results = new List<ImportResult>
            {
                BuildResult(Quality.MOBI, QualityGateRejection("Quality 'MOBI' not allowed by profile 'Ebook'")),
                BuildResult(Quality.PDF, QualityGateRejection("Quality 'PDF' not allowed by profile 'Ebook'"))
            };

            Assert.That(CompletedDownloadService.IsProfileDisallowedPayload(results), Is.True);
        }

        [Test]
        public void should_not_fail_a_download_that_imported_something()
        {
            var results = new List<ImportResult>
            {
                BuildResult(Quality.EPUB),
                BuildResult(Quality.MOBI, QualityGateRejection("Quality 'MOBI' not allowed by profile 'Ebook'"))
            };

            Assert.That(CompletedDownloadService.IsProfileDisallowedPayload(results), Is.False);
        }

        [Test]
        public void should_not_fail_when_any_file_failed_for_another_reason()
        {
            // Mixed causes mean the payload is not provably wrong — that stays a blocked import the
            // user can retry after fixing whatever else went wrong.
            var results = new List<ImportResult>
            {
                BuildResult(Quality.MOBI, QualityGateRejection("Quality 'MOBI' not allowed by profile 'Ebook'")),
                BuildResult(Quality.EPUB, new Rejection("Author not found"))
            };

            Assert.That(CompletedDownloadService.IsProfileDisallowedPayload(results), Is.False);
        }

        [Test]
        public void should_not_fail_on_transient_failures_with_no_file_context()
        {
            var results = new List<ImportResult>();

            Assert.That(CompletedDownloadService.IsProfileDisallowedPayload(results), Is.False);
        }

        [Test]
        public void should_not_fail_when_the_rejection_is_uncategorised()
        {
            // The evaluation-error path in the import gate rejects with the default category on
            // purpose: an exception while reading the profile must never blocklist a release.
            var results = new List<ImportResult>
            {
                BuildResult(Quality.MOBI, new Rejection("Quality not allowed by profile"))
            };

            Assert.That(CompletedDownloadService.IsProfileDisallowedPayload(results), Is.False);
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_import_the_allowed_format_and_ignore_the_others_without_blocking()
        {
            // The whole point of the report: a bundle lands, one format is wanted, the rest are
            // rejected by the gate — and the download must still complete.
            var harness = new Harness();
            harness.ImportResults = new List<ImportResult>
            {
                BuildResult(Quality.EPUB),
                BuildResult(Quality.MOBI, QualityGateRejection("Quality 'MOBI' not allowed by profile 'Ebook'")),
                BuildResult(Quality.PDF, QualityGateRejection("Quality 'PDF' not allowed by profile 'Ebook'"))
            };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(harness.FailedDownloadService.Failures, Is.Empty);
            Assert.That(harness.EventAggregator.Events, Has.Some.InstanceOf<DownloadCompletedEvent>());
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_fail_the_download_when_no_downloaded_format_is_allowed()
        {
            var harness = new Harness();
            harness.ImportResults = new List<ImportResult>
            {
                BuildResult(Quality.MOBI, QualityGateRejection("Quality 'MOBI' not allowed by profile 'Ebook'"))
            };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.FailedDownloadService.Failures, Has.Count.EqualTo(1));
            Assert.That(harness.FailedDownloadService.Failures[0].Reason, Does.Contain("MOBI"));
            Assert.That(harness.TrackedDownload.State, Is.Not.EqualTo(TrackedDownloadState.ImportBlocked));
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_import_a_forced_grab_whose_format_the_profile_would_not_allow()
        {
            // A forced grab never reaches the gate (ImportApprovedBooks.ApplyQualityProfileGate skips
            // it — see ImportApprovedBooksQualityGateFixture), so the file imports and the download
            // completes. There is no path where a forced grab is failed for its format.
            var harness = new Harness();
            harness.TrackedDownload.DownloadItem.DownloadForced = true;
            harness.ImportResults = new List<ImportResult> { BuildResult(Quality.MOBI) };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(harness.FailedDownloadService.Failures, Is.Empty);
        }

        private sealed class Harness
        {
            public RecordingEventAggregator EventAggregator { get; } = new();
            public RecordingFailedDownloadService FailedDownloadService { get; } = new();
            public TrackedDownload TrackedDownload { get; }
            public CompletedDownloadService Subject { get; }

            public List<ImportResult> ImportResults
            {
                get => _importService.Results;
                set => _importService.Results = value;
            }

            private readonly RecordingDownloadedBooksImportService _importService = new();

            public Harness()
            {
                var author = new Author { Id = 7, Name = "Katee Robert" };
                var book = new Book { Id = 901, AuthorId = 7, Title = "Learn My Lesson", MediaType = BookMediaType.Ebook, AnyEditionOk = true };

                TrackedDownload = new TrackedDownload
                {
                    DownloadClient = 1,
                    State = TrackedDownloadState.ImportPending,
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "learn-my-lesson",
                        Title = "Learn My Lesson, epub, please...thanks - Learn My Lesson.mobi",
                        OutputPath = new OsPath("/downloads/learn-my-lesson")
                    },
                    RemoteBook = new RemoteBook
                    {
                        Author = author,
                        Books = new List<Book> { book },
                        ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MOBI) }
                    }
                };

                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new()
                    {
                        DownloadId = "learn-my-lesson",
                        EventType = EntityHistoryEventType.Grabbed,
                        AuthorId = 7,
                        BookId = 901,
                        Date = DateTime.UtcNow.AddMinutes(-10)
                    }
                };

                Subject = new CompletedDownloadService(
                    EventAggregator,
                    historyService,
                    new StubProvideImportItemService("/downloads/learn-my-lesson"),
                    _importService,
                    new PassthroughDownloadImportModeResolver(),
                    new StubTrackedDownloadAlreadyImported(),
                    FailedDownloadService,
                    LogManager.GetCurrentClassLogger(),
                    NoopDownloadClientFileSnapshotService.Instance);
            }
        }

        private sealed class RecordingFailedDownloadService : IFailedDownloadService
        {
            public List<(TrackedDownload Download, string Reason)> Failures { get; } = new();

            public void MarkAsFailed(TrackedDownload trackedDownload, string reason, bool skipRedownload = false)
            {
                Failures.Add((trackedDownload, reason));
                trackedDownload.State = TrackedDownloadState.DownloadFailed;
            }

            public void MarkAsFailed(int historyId, bool skipRedownload = false) => throw new NotImplementedException();
            public void MarkAsFailed(string downloadId, bool skipRedownload = false) => throw new NotImplementedException();
            public void Check(TrackedDownload trackedDownload) => throw new NotImplementedException();
            public void ProcessFailed(TrackedDownload trackedDownload) => throw new NotImplementedException();
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

        private sealed class StubProvideImportItemService : IProvideImportItemService
        {
            private readonly OsPath _outputPath;

            public StubProvideImportItemService(string outputPath)
            {
                _outputPath = new OsPath(outputPath);
            }

            public DownloadClientItem ProvideImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                var clone = item.Clone();
                clone.OutputPath = _outputPath;
                return clone;
            }
        }

        private sealed class RecordingDownloadedBooksImportService : IDownloadedBooksImportService
        {
            public List<ImportResult> Results { get; set; } = new();

            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => Results;

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => throw new NotImplementedException();
        }

        private sealed class PassthroughDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, false);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem) => false;
        }

        private sealed class StubTrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
        {
            public bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems) => false;
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> HistoryItems { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    var downloadId = (string)args[0];
                    return HistoryItems.Where(h => h.DownloadId == downloadId).ToList();
                }

                if (targetMethod?.Name == nameof(IHistoryService.MostRecentForDownloadId))
                {
                    return HistoryItems.OrderByDescending(h => h.Date).FirstOrDefault();
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        private static Rejection QualityGateRejection(string reason)
        {
            return new Rejection(reason, RejectionType.Permanent, canBypass: false, category: "Quality", severity: 3);
        }

        private static ImportResult BuildResult(Quality quality, Rejection rejection = null)
        {
            // Imported files carry the book they landed on — that is what completion is verified
            // against. Gated files never get that far.
            var decision = new ImportDecision<LocalBook>(new LocalBook
            {
                Path = $"/downloads/book.{quality.Name.ToLowerInvariant()}",
                Quality = new QualityModel(quality),
                Book = rejection == null
                    ? new Book { Id = 901, AuthorId = 7, Title = "Learn My Lesson", MediaType = BookMediaType.Ebook }
                    : null,
                Author = rejection == null ? new Author { Id = 7, Name = "Katee Robert" } : null
            });

            if (rejection == null)
            {
                return new ImportResult(decision);
            }

            decision.Reject(rejection);
            return new ImportResult(decision, rejection.Reason);
        }
    }
}
