using System;
using System.Collections.Generic;
using System.IO;
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
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class CompletedDownloadServiceQualityProfileFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void other_import_failures_should_remain_for_manual_review(bool mixed)
        {
            var harness = new Harness();
            harness.ImportResults = new List<ImportResult>
            {
                BuildResult(Quality.MOBI, new Rejection("Quality not allowed by profile"))
            };
            if (mixed)
            {
                harness.ImportResults.Add(BuildResult(Quality.PDF, QualityGateRejection("Quality 'PDF' not allowed by profile 'Ebook'")));
            }

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(message => message.Messages),
                Has.Member("Quality not allowed by profile"));
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
        }

        [Test]
        public void no_files_should_keep_waiting_without_blocklisting()
        {
            var harness = new Harness();
            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportPending));
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
        }

        [Test]
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
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
            Assert.That(harness.EventAggregator.Events, Has.Some.InstanceOf<DownloadCompletedEvent>());
        }

        [Test]
        public void should_warn_when_no_downloaded_format_is_allowed()
        {
            var harness = new Harness();
            harness.ImportResults = new List<ImportResult>
            {
                BuildResult(Quality.MOBI, QualityGateRejection("Quality 'MOBI' not allowed by profile 'Ebook'"))
            };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(message => message.Messages),
                Is.EqualTo(new[] { harness.TrackedDownload.StatusMessages[0].Messages.Single(), "Quality 'MOBI' not allowed by profile 'Ebook'" }));
            Assert.That(harness.TrackedDownload.StatusMessages, Has.Length.EqualTo(2));
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
            Assert.That(harness.EventAggregator.Events, Has.One.InstanceOf<BookImportIncompleteEvent>());
        }

        [Test]
        public void skipped_import_keeps_the_download_in_queue_without_blocklisting()
        {
            var harness = new Harness();
            harness.TrackedDownload.DownloadItem.DownloadForced = true;
            var decision = new ImportDecision<LocalBook>(new LocalBook
            {
                Path = Path.Combine(Harness.DownloadRoot, "book.m4b"),
                Book = harness.TrackedDownload.RemoteBook.Books.Single(),
                Quality = new QualityModel(Quality.M4B)
            });
            const string reason = "Not an upgrade. Existing files were kept.";
            harness.ImportResults = new List<ImportResult> { new(decision, reason) };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(message => message.Messages), Does.Contain(reason));
            Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
            Assert.That(harness.EventAggregator.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
            Assert.That(harness.EventAggregator.Events.OfType<BookImportIncompleteEvent>().Count(), Is.EqualTo(1));
        }

        internal sealed class Harness
        {
            internal static readonly string DownloadRoot = @"C:\downloads\learn-my-lesson".AsOsAgnostic();

            public RecordingEventAggregator EventAggregator { get; } = new();
            public TrackedDownload TrackedDownload { get; }
            public CompletedDownloadService Subject { get; }

            public List<ImportResult> ImportResults
            {
                get => _importService.Results;
                set => _importService.Results = value;
            }

            private readonly RecordingDownloadedBooksImportService _importService = new();

            public Harness(Func<DownloadClientItem, List<ImportResult>> import = null)
            {
                _importService.Import = import;
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
                        OutputPath = new OsPath(DownloadRoot)
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
                    new StubProvideImportItemService(DownloadRoot),
                    _importService,
                    new PassthroughDownloadImportModeResolver(),
                    new TrackedDownloadAlreadyImported(LogManager.GetCurrentClassLogger()),
                    LogManager.GetCurrentClassLogger(),
                    NoopDownloadClientFileSnapshotService.Instance);
            }
        }

        internal sealed class RecordingEventAggregator : IEventAggregator
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
            public Func<DownloadClientItem, List<ImportResult>> Import { get; set; }

            public List<ImportResult> ProcessIncludedMedia(DownloadClientItem item, NzbDrone.Core.Parser.Model.RemoteBook book,
                List<NzbDrone.Core.History.EntityHistory> history) => new();

            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => Import?.Invoke(downloadClientItem) ?? Results;

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => throw new NotImplementedException();
        }

        private sealed class PassthroughDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, false);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem, int fallbackIndexerId = 0, bool includeUnmanaged = true) => false;
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
            // The production quality gate runs after matching and retains the matched book.
            var decision = new ImportDecision<LocalBook>(new LocalBook
            {
                Path = Path.Combine(Harness.DownloadRoot, $"book.{quality.Name.ToLowerInvariant()}"),
                Quality = new QualityModel(quality),
                Book = new Book { Id = 901, AuthorId = 7, Title = "Learn My Lesson", MediaType = BookMediaType.Ebook },
                Author = new Author { Id = 7, Name = "Katee Robert" }
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
