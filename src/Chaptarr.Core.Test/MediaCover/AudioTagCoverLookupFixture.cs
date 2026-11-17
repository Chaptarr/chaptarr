using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NzbDrone.Core.MediaCover.Commands;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class AudioTagCoverLookupFixture
    {
        private sealed class UnloadedBook : NzbDrone.Core.Datastore.LazyLoaded<Book>
        {
            public override void LazyLoad() => throw new AssertionException("Reading local artwork must not load a book or its edition catalog");
        }

        private sealed class CoverSnapshotFixture : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            private readonly LogFactory _logs = new();
            public readonly NLog.Targets.MemoryTarget Warnings = new() { Layout = "${level}|${message}" };
            public readonly MediaCoverService Service;
            public readonly Edition OriginalEdition;
            public readonly Edition ReplacementEdition;
            public readonly Book Book;
            public readonly byte[] Original = ImageBytes(new Rgb24(20, 30, 200));
            public readonly byte[] Replacement = ImageBytes(new Rgb24(200, 30, 20));
            public Action BeforeRead;
            public Action AfterReceiptRead;
            public Action BeforeReceiptRead;
            public Action BeforeDownload;
            public int Downloads;
            public readonly DeferredCoverService Deferred;
            public string ReceiptPath => Path.Combine(Path.GetDirectoryName(Service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png")), "cover-metadata.json");

            public CoverSnapshotFixture(NzbDrone.Core.Http.IHttpServiceHealth health = null)
            {
                Directory.CreateDirectory(_directory);
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, Warnings);
                _logs.Configuration = logging;
                var logger = _logs.GetLogger("CoverSnapshot");
                health ??= new NzbDrone.Core.Http.HttpServiceHealth(logger);
                Deferred = new DeferredCoverService(logger, health);
                var realDisk = RealDiskProvider.Create(logger);
                var disk = Proxy<IDiskProvider>((method, args) =>
                {
                    if (method.Name == nameof(IDiskProvider.OpenReadStream)) BeforeRead?.Invoke();
                    if (method.Name == nameof(IDiskProvider.ReadAllText)) BeforeReceiptRead?.Invoke();
                    var result = method.Invoke(realDisk, args);
                    if (method.Name == nameof(IDiskProvider.ReadAllText)) AfterReceiptRead?.Invoke();
                    return result;
                });
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    if (method.Name != nameof(IHttpClient.Get)) throw new AssertionException(method.Name);
                    Downloads++;
                    BeforeDownload?.Invoke();
                    return new HttpResponse((HttpRequest)args[0], new HttpHeader { ContentType = "image/png" }, Replacement);
                });
                Service = new MediaCoverService(null, Proxy<IImageResizer>((_, _) => null), null, http, disk, Proxy<IAppFolderInfo>((_, _) => _directory), Proxy<IConfigService>((method, _) => method.Name switch
                    {
                        "get_MetadataServerUrl" => "https://images.example",
                        "get_CoverDownloadScope" => CoverDownloadScope.AllBooks,
                        _ => throw new AssertionException(method.Name)
                    }), null, null, Deferred, logger, imageHostHealth: health);
                OriginalEdition = Edition(3, "hc:edition:123", "original.png");
                ReplacementEdition = Edition(4, "hc:edition:456", "replacement.png");
                Book = new Book { Id = 2, Title = "Cover snapshot", Editions = new List<Edition> { ReplacementEdition } };
                OriginalEdition.Book = new Book { Id = 2, Editions = new List<Edition> { OriginalEdition } };
                ReplacementEdition.Book = Book;
                var path = Service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, Original);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "cover-metadata.json"), new
                {
                    SelectedEdition = new { EditionProviderId = OriginalEdition.ForeignEditionId, CoverUrl = OriginalEdition.Images[0].Url, DownloadedAt = DateTime.UtcNow }
                }.ToJson());
            }

            private static Edition Edition(int id, string providerId, string name) => new()
            {
                Id = id, BookId = 2, Monitored = true, ForeignEditionId = providerId,
                Images = new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, "https://images.example/" + name) }
            };

            private static byte[] ImageBytes(Rgb24 color)
            {
                using var image = new Image<Rgb24>(2, 2, color);
                using var data = new MemoryStream();
                image.SaveAsPng(data);
                return data.ToArray();
            }

            public void Replace() => Service.EnsureBookCoversForImport(Book);
            public void Dispose() { _logs.Dispose(); Directory.Delete(_directory, true); }
        }


        [Test]
        public void request_claimed_while_an_older_download_runs_is_requeued()
        {
            using var h = new CoverSnapshotFixture();
            h.BeforeDownload = () => h.Service.EnsureBookCoversForImport(h.Book);
            h.Replace();
            Assert.That(h.Downloads, Is.EqualTo(1));
            Assert.That(h.Deferred.TakePendingBookIds(), Is.EqualTo(new[] { h.Book.Id }));
        }

        [TestCase("http://127.0.0.1/unsafe.png")]
        [TestCase("not-an-absolute-url")]
        public void unsafe_or_invalid_book_cover_stays_quiet(string url)
        {
            using var h = new CoverSnapshotFixture();
            h.ReplacementEdition.Images[0].Url = url;
            h.Replace();
            Assert.That(h.Downloads, Is.Zero);
            Assert.That(h.Warnings.Logs, Is.Empty);
            Assert.That(h.Deferred.GetPendingBookIds(), Is.Empty);
        }

        [TestCase(500)]
        [TestCase(404)]
        public void failed_image_response_does_not_pause_other_urls_or_requeue(int code)
        {
            var health = new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger());
            using var h = new CoverSnapshotFixture(health);
            h.BeforeDownload = () =>
            {
                var request = new HttpRequest("https://images.example/broken.png");
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)code));
            };
            h.Replace();
            Assert.That(h.Deferred.GetPendingBookIds(), Is.Empty);
            h.ReplacementEdition.Images[0].Url = "https://images.example/working.png";
            h.BeforeDownload = null;
            h.Replace();
            Assert.That(h.Downloads, Is.EqualTo(2));
            Assert.That(h.Service.ReadLocalBookCover(h.ReplacementEdition).Data, Is.EqualTo(h.Replacement));
        }

        [Test]
        public void succeeding_fallback_is_not_left_in_the_retry_queue()
        {
            var health = new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger());
            using var g = new CoverSnapshotFixture(health);
            health.ReportFailure("image:https://other.example", new WebException("Fixture timeout", WebExceptionStatus.Timeout));
            g.ReplacementEdition.Images.Insert(0, new(MediaCoverTypes.Cover, "https://other.example/broken.png"));
            g.Replace();
            Assert.That(g.Downloads, Is.EqualTo(1));
            Assert.That(g.Deferred.GetPendingBookIds(), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_older_cover_attempt_preserves_a_new_import_mark(bool fails)
        {
            using var h = new CoverSnapshotFixture();
            h.BeforeDownload = () =>
            {
                h.Deferred.IsCoverDownloadDeferred = true;
                h.Deferred.MarkBookForCoverDownload(h.Book.Id);
                h.Deferred.IsCoverDownloadDeferred = false;
                if (fails) throw new WebException("Fixture timeout", WebExceptionStatus.Timeout);
            };
            h.Replace();
            Assert.That(h.Deferred.TakePendingBookIds(), Is.EqualTo(new[] { h.Book.Id }),
                "The new mark must remain ready even if the old request pauses its origin");
        }

        [Test]
        public void timed_out_probe_rotates_behind_every_unattempted_book()
        {
            var health = new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger());
            using var h = new CoverSnapshotFixture(health);
            var books = Enumerable.Range(40, 4).Select(id => new Book { Id = id, Title = "Pending cover", Editions = new List<Edition>
            {
                new() { Id = id, ForeignEditionId = "hc:edition:" + id, Monitored = true,
                    Images = new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, "https://images.example/" + id + ".png") } }
            } }).ToList();
            var requests = 0;
            h.BeforeDownload = () => { requests++; throw new WebException("Fixture timeout", WebExceptionStatus.Timeout); };
            h.Service.EnsureBookCoversForBooks(books);
            Assert.That(requests, Is.EqualTo(1));
            var attempts = new List<int>();
            var service = Proxy<IBookService>((_, args) =>
            {
                var ids = ((IEnumerable<int>)args[0]).ToArray(); attempts.Add(ids[0]);
                return ids.Select(id => books.SingleOrDefault(book => book.Id == id)).Where(book => book != null).ToList();
            });
            var flush = new DeferredCoverDownloadService(h.Deferred, service, null, h.Service, LogManager.GetCurrentClassLogger());
            for (var i = 0; i < 4; i++)
            {
                health.GetStatus("image:https://images.example").RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
                flush.Execute(new RetryBookCoversCommand());
            }
            Assert.That(attempts, Is.EqualTo(new[] { 41, 42, 43, 40 }));
            Assert.That(requests, Is.EqualTo(5));
            h.Deferred.IsCoverDownloadDeferred = true;
            flush.Execute(new RetryBookCoversCommand());
            Assert.That(attempts, Has.Count.EqualTo(4));
        }

        [Test]
        public void failed_receipt_read_is_not_cached_until_restart()
        {
            using var fixture = new CoverSnapshotFixture();
            fixture.BeforeReceiptRead = () => throw new IOException("Fixture receipt is locked");
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
            fixture.BeforeReceiptRead = null;
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Data, Is.EqualTo(fixture.Original));
        }

        [TestCase(0)]
        [TestCase(503)]
        public void image_outage_keeps_same_edition_cover_and_deferred_flush_recovers(int statusCode)
        {
            var health = new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger());
            using var fixture = new CoverSnapshotFixture(health);
            fixture.ReplacementEdition.ForeignEditionId = fixture.OriginalEdition.ForeignEditionId;
            var path = fixture.Service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png");
            fixture.BeforeDownload = () =>
            {
                if (statusCode == 0) throw new WebException("Fixture timeout", WebExceptionStatus.Timeout);
                var request = new HttpRequest("https://images.example/replacement.png");
                throw new HttpException(request, new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), (HttpStatusCode)statusCode));
            };
            fixture.Replace();
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(fixture.Original));
            Assert.That(fixture.Deferred.GetPendingBookIds(), Is.EqualTo(new[] { 2 }));
            var reads = 0;
            var books = Proxy<IBookService>((method, _) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBookService.GetExistingBooks))); reads++;
                return new List<Book> { fixture.Book };
            });
            var flush = new DeferredCoverDownloadService(fixture.Deferred, books, null, fixture.Service, LogManager.GetCurrentClassLogger());
            flush.Execute(new RetryBookCoversCommand());
            Assert.That(fixture.Downloads, Is.EqualTo(1), "The background path also honors the paused host");
            Assert.That(reads, Is.Zero, "A paused tick must not hydrate books");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(fixture.Original), "Check the book whose new URL actually failed");
            Assert.That(fixture.Deferred.GetPendingBookIds(), Is.EqualTo(new[] { 2 }), "A skipped flush must not lose work");
            health.GetStatus("image:https://images.example").RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            fixture.BeforeDownload = null;
            flush.Execute(new RetryBookCoversCommand());
            Assert.That(fixture.Downloads, Is.EqualTo(2));
            Assert.That(fixture.Deferred.GetPendingBookIds(), Is.Empty);
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.ReplacementEdition).Data, Is.EqualTo(fixture.Replacement));
            flush.Execute(new RetryBookCoversCommand());
            Assert.That(reads, Is.EqualTo(1), "An empty scheduled retry does not read the catalog");
        }

        [Test]
        public void corrupt_book_receipt_is_parsed_only_once_until_a_publication()
        {
            using var fixture = new CoverSnapshotFixture();
            File.WriteAllText(fixture.ReceiptPath, "{ broken");
            var reads = 0; fixture.BeforeReceiptRead = () => reads++;
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
            Assert.That(reads, Is.EqualTo(1));
            fixture.Replace();
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.ReplacementEdition).Data, Is.EqualTo(fixture.Replacement));
        }

        [Test]
        public void local_cover_read_never_blocks_writer_or_returns_another_editions_snapshot()
        {
            using var fixture = new CoverSnapshotFixture();
            fixture.BeforeRead = fixture.Replace;
            var snapshot = fixture.Service.ReadLocalBookCover(fixture.OriginalEdition);
            Assert.That(fixture.Downloads, Is.EqualTo(1), "The tag reader must not hold up the cover writer");
            Assert.That(snapshot.Ready, Is.False, "The receipt changed while opening the image");
            fixture.BeforeRead = null;
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.ReplacementEdition).Data, Is.EqualTo(fixture.Replacement));
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
        }

        [Test]
        public void receipt_cache_miss_cannot_replace_a_newer_published_receipt()
        {
            using var fixture = new CoverSnapshotFixture();
            fixture.AfterReceiptRead = () =>
            {
                fixture.AfterReceiptRead = null;
                fixture.Replace();
            };
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.ReplacementEdition).Data, Is.EqualTo(fixture.Replacement));
        }

        [Test]
        public void in_flight_cover_is_unavailable_without_failing_the_tag_operation()
        {
            using var fixture = new CoverSnapshotFixture();
            var called = false;
            fixture.BeforeDownload = () =>
            {
                called = true;
                Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
            };
            fixture.Replace();
            Assert.That(called, Is.True);
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.ReplacementEdition).Data, Is.EqualTo(fixture.Replacement));
        }

        [Test]
        public void unreadable_cover_is_unavailable_and_a_later_read_still_works()
        {
            using var fixture = new CoverSnapshotFixture();
            fixture.BeforeRead = () => throw new IOException("Fixture read failed");
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Ready, Is.False);
            fixture.BeforeRead = null;
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Data, Is.EqualTo(fixture.Original));
        }

        [Test]
        public void cover_read_reuses_the_file_edition_without_loading_the_book_catalog()
        {
            using var fixture = new CoverSnapshotFixture();
            fixture.OriginalEdition.LazyBook = new UnloadedBook();
            Assert.That(fixture.Service.ReadLocalBookCover(fixture.OriginalEdition).Data, Is.EqualTo(fixture.Original));
        }

        [TestCase("match", true)]
        [TestCase("unmonitored", false)]
        [TestCase("other-edition", false)]
        [TestCase("other-url", false)]
        [TestCase("no-receipt", false)]
        [TestCase("no-original", false)]
        [TestCase("empty-original", false)]
        [TestCase("rendition-only", false)]
        [TestCase("no-cover", true)]
        [TestCase("placeholder", false)]
        [TestCase("null-images", false)]
        [TestCase("no-cover-other-receipt", false)]
        public void local_cover_requires_this_monitored_edition_receipt_and_usable_original(string scenario, bool expected)
        {
            var directory = Path.Combine(Path.GetTempPath(), "audio-cover-lookup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var logger = LogManager.GetCurrentClassLogger();
                var service = new MediaCoverService(null, null, null, null, RealDiskProvider.Create(logger), Proxy<IAppFolderInfo>((_, _) => directory), null, null, null, null, logger, imageHostHealth: new NzbDrone.Core.Http.HttpServiceHealth(LogManager.GetCurrentClassLogger()));
                var edition = new Edition { Id = 3, BookId = 2, Monitored = scenario != "unmonitored", ForeignEditionId = "hc:edition:123", Images = new List<NzbDrone.Core.MediaCover.MediaCover>() };
                edition.Book = new Book { Id = 2, Editions = new List<Edition> { edition } };
                var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://example.test/edition.jpg");
                if (!scenario.StartsWith("no-cover")) edition.Images.Add(cover);
                if (scenario == "null-images") edition.Images = null;
                if (scenario == "placeholder") cover.Url = "https://example.test/nophoto.jpg";
                var original = service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                if (scenario != "no-original" && scenario != "rendition-only") File.WriteAllBytes(original, scenario == "empty-original" ? Array.Empty<byte>() : new byte[] { 1, 2, 3 });
                if (scenario == "rendition-only") File.WriteAllBytes(service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".jpg", 500), new byte[] { 1, 2, 3 });
                if (scenario != "no-receipt") File.WriteAllText(Path.Combine(Path.GetDirectoryName(original), "cover-metadata.json"), new
                {
                    SelectedEdition = new
                    {
                        EditionProviderId = scenario is "other-edition" or "no-cover-other-receipt" ? "hc:edition:456" : "hc:edition:123",
                        CoverUrl = scenario == "other-url" ? "https://example.test/other.jpg" : cover.Url,
                        DownloadedAt = DateTime.UtcNow
                    }
                }.ToJson());
                var result = service.ReadLocalBookCover(edition);
                Assert.That(result.Ready, Is.EqualTo(expected));
                Assert.That(result.Data, Is.EqualTo(expected && scenario != "no-cover" ? new byte[] { 1, 2, 3 } : null));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
