using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Download
{
    public partial class CompletedDownloadWaitingFixture
    {
        [Test]
        public void expected_import_wait_has_a_reason_without_a_warning(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                var path = tracked.DownloadItem.FilePaths.Single();
                File.WriteAllText(path, "ready source");
                var book = tracked.RemoteBook.Books.Single();
                counters.ImportResults = new List<ImportResult>
                {
                    new(new ImportDecision<LocalBook>(new LocalBook
                    {
                        Path = path, Book = book, Edition = book.Editions.Single(), Author = tracked.RemoteBook.Author,
                        Quality = new QualityModel(mediaType == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB)
                    }), ImportResultType.Pending, "Waiting for the conversion to finish")
                };
                subject.Import(tracked);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
                Assert.That(tracked.Status, Is.EqualTo(TrackedDownloadStatus.Ok));
                Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                    Has.Some.Contains("Waiting for the conversion to finish"));
            }, mediaType: mediaType);
        }

        [Test]
        public void other_media_arriving_first_waits_then_imports_when_target_files_arrive(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                foreach (var path in tracked.DownloadItem.FilePaths) File.Delete(path);
                var companion = Path.Combine(tracked.DownloadItem.OutputPath.FullPath,
                    mediaType == BookMediaType.Audiobook ? "Dune.epub" : "Dune.m4b");
                File.WriteAllText(companion, "fixture");
                tracked.DownloadItem.FilePaths.Add(companion);

                subject.Import(tracked);

                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
                Assert.That(counters.IncompleteEvents, Is.Zero);
                Assert.That(counters.FailedDownloads, Is.Zero);
                Assert.That(File.Exists(companion), Is.True);
                Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                    Has.Some.Contains("reported by the download client yet"));

                var target = tracked.DownloadItem.FilePaths.Single(path => path != companion);
                File.WriteAllText(target, "arrived");
                var book = tracked.RemoteBook.Books.Single();
                counters.ImportSource(target);
                counters.ImportResults = new List<ImportResult>
                {
                    new(new ImportDecision<LocalBook>(new LocalBook
                    {
                        Path = target, Book = book, Edition = book.Editions.Single(),
                        Author = tracked.RemoteBook.Author,
                        Quality = new QualityModel(
                            mediaType == BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB)
                    }))
                };

                subject.Import(tracked);

                Assert.Multiple(() =>
                {
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                    Assert.That(tracked.StatusMessages, Is.Empty);
                    Assert.That(counters.IncompleteEvents, Is.Zero);
                    Assert.That(counters.FailedDownloads, Is.Zero);
                    Assert.That(counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting").EventType,
                        Is.EqualTo(DownloadHistoryEventType.DownloadImported));
                    Assert.That(File.Exists(target), Is.True);
                    Assert.That(File.Exists(companion), Is.True);
                });
            }, mediaType: mediaType);
        }

        [Test]
        public void folder_payload_should_stop_for_wrong_books_but_wait_for_no_books(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool diskInventory,
            [Values("other-media", "empty", "nfo", "partial", "mac-junk")] string contents)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                foreach (var original in tracked.DownloadItem.FilePaths) File.Delete(original);
                var otherExtension = mediaType == BookMediaType.Audiobook ? ".epub" : ".m4b";
                var name = contents switch
                {
                    "other-media" => "Dune" + otherExtension,
                    "nfo" => "Dune.nfo",
                    "partial" => "Dune" + otherExtension + ".partial~",
                    "mac-junk" => "._Dune" + otherExtension,
                    _ => null
                };
                var path = name == null ? null : Path.Combine(tracked.DownloadItem.OutputPath.FullPath, name);
                if (path != null) File.WriteAllText(path, "fixture");
                if (!diskInventory)
                {
                    tracked.DownloadItem.FilePaths = path == null ? new() : new() { path };
                }
                tracked.DownloadItem.CanBeRemoved = true;
                tracked.IsTrackable = true;
                subject.Import(tracked);
                var calls = new RemovalCalls();
                var hub = new DownloadEventHub(subject,
                    Proxy<IProvideDownloadClient>((_, _) => new RecordingSabnzbd(calls)),
                    Proxy<IDownloadImportModeResolver>((_, _) => false), LogManager.GetCurrentClassLogger());
                hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                var wrongMedia = contents == "other-media";
                Assert.Multiple(() =>
                {
                    Assert.That(tracked.State, Is.EqualTo(wrongMedia ? TrackedDownloadState.ImportBlocked : TrackedDownloadState.ImportPending));
                    Assert.That(counters.FailedDownloads, Is.Zero);
                    Assert.That(calls.RemoveItem, Is.Zero);
                    Assert.That(calls.DeleteItemData, Is.Zero);
                    Assert.That(tracked.DownloadItem.Removed, Is.False);
                    if (path != null) Assert.That(File.Exists(path), Is.True);
                    Assert.That(counters.IncompleteEvents, Is.EqualTo(wrongMedia ? 1 : 0));
                });
                if (wrongMedia)
                {
                    var side = mediaType == BookMediaType.Audiobook ? "an audiobook" : "an ebook";
                    var otherSide = mediaType == BookMediaType.Audiobook ? "ebook" : "audiobook";
                    Assert.That(tracked.StatusMessages.First().Messages.First(),
                        Is.EqualTo($"Grabbed Dune as {side}, but it only contains {otherSide} files ({otherExtension[1..].ToUpperInvariant()}: {name}). Use Manual Import to import them anyway, or remove the download."));
                    counters.Queue.Handle(new TrackedDownloadUpdatedEvent(tracked));
                    Assert.That(counters.Queue.GetQueue(false).Single().ErrorMessage,
                        Is.EqualTo(tracked.StatusMessages.First().Messages.First()));
                    Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages)
                        .Count(message => message == tracked.StatusMessages.First().Messages.First()), Is.EqualTo(1));
                    Assert.That(counters.DownloadHistory.GetLatestDownloadHistoryItem("waiting").EventType,
                        Is.EqualTo(DownloadHistoryEventType.DownloadImportIncomplete));
                }
            }, diskInventory: diskInventory, mediaType: mediaType);
        }

        [Test]
        public void final_unsupported_or_junk_file_blocks_but_partial_file_waits(
            [Values(false, true)] bool postgres, [Values("Dune.nfo", "Dune.mkv", "Dune.m4b.partial~", "._Dune.mp3")] string name)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                var path = Path.Combine(tracked.DownloadItem.OutputPath.FullPath, name);
                File.WriteAllText(path, "fixture");
                tracked.DownloadItem.OutputPath = new NzbDrone.Common.Disk.OsPath(path);
                tracked.DownloadItem.FilePaths = new() { path };
                subject.Import(tracked);
                var finalPayload = name != "Dune.m4b.partial~";
                Assert.That(tracked.State, Is.EqualTo(finalPayload ? TrackedDownloadState.ImportBlocked : TrackedDownloadState.ImportPending));
                Assert.That(counters.IncompleteEvents, Is.EqualTo(finalPayload ? 1 : 0));
                Assert.That(counters.FailedDownloads, Is.Zero);
                Assert.That(File.Exists(path), Is.True);
            });
        }

        [Test]
        public void visible_target_transfer_waits_without_an_authoritative_file_list(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            Run(postgres, 1, 0, (subject, tracked, counters) =>
            {
                foreach (var path in tracked.DownloadItem.FilePaths) File.Delete(path);
                var folder = tracked.DownloadItem.OutputPath.FullPath;
                var targetExtension = mediaType == BookMediaType.Audiobook ? ".m4b" : ".epub";
                var otherExtension = mediaType == BookMediaType.Audiobook ? ".epub" : ".m4b";
                var partial = Path.Combine(folder, "Dune" + targetExtension + ".partial~");
                var companion = Path.Combine(folder, "Dune" + otherExtension);
                File.WriteAllText(partial, "copy in progress");
                File.WriteAllText(companion, "complete companion");
                subject.Import(tracked);
                Assert.Multiple(() =>
                {
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
                    Assert.That(counters.IncompleteEvents, Is.Zero);
                    Assert.That(counters.FailedDownloads, Is.Zero);
                    Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains(partial));
                    Assert.That(File.ReadAllText(partial), Is.EqualTo("copy in progress"));
                    Assert.That(File.ReadAllText(companion), Is.EqualTo("complete companion"));
                });
            }, diskInventory: true, mediaType: mediaType);
        }

        [Test]
        public void profile_change_should_not_claim_the_release_was_grabbed_as_a_different_format()
        {
            var profile = new QualityProfile
            {
                Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.M4B, Allowed = true } }
            };
            var message = GrabbedFormatMismatch.Message("Dune", new[] { Path.Combine(Path.GetTempPath(), "Dune.mp3") }, BookMediaType.Audiobook, Quality.MP3, profile);
            Assert.That(message, Does.StartWith("Grabbed Dune as MP3, but its MP3 files are not allowed by the quality profile"));
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void mismatch_without_release_quality_should_name_the_allowed_formats(BookMediaType mediaType)
        {
            var allowed = mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB;
            var rejected = mediaType == BookMediaType.Audiobook ? "mp3" : "mobi";
            var profile = new QualityProfile
            {
                Items = new List<QualityProfileQualityItem> { new() { Quality = allowed, Allowed = true } }
            };
            var message = GrabbedFormatMismatch.Message("Dune", new[] { Path.Combine(Path.GetTempPath(), "Dune." + rejected) }, mediaType, profile: profile);
            Assert.That(message, Does.StartWith($"Grabbed Dune as {allowed.Name}, but it only contains {rejected.ToUpperInvariant()} files"));
        }
    }
}
