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

        internal sealed class CoverSnapshotFixture : IDisposable
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
            public string ReceiptPath => Path.Combine(Path.GetDirectoryName(Service.GetCoverPath(Book.Id, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png")), "cover-metadata.json");

            public CoverSnapshotFixture(NzbDrone.Core.Http.IHttpServiceHealth health = null, int bookId = 2, Book selectedBook = null, Edition storedEdition = null)
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
                OriginalEdition = storedEdition ?? Edition(3, "hc:edition:123", "original.png");
                ReplacementEdition = selectedBook?.Editions.Single(edition => edition.Monitored) ?? Edition(4, "hc:edition:456", "replacement.png");
                Book = selectedBook ?? new Book { Id = bookId, Title = "Cover snapshot", Editions = new List<Edition> { ReplacementEdition } };
                bookId = Book.Id;
                OriginalEdition.Book = new Book { Id = bookId, Editions = new List<Edition> { OriginalEdition } };
                ReplacementEdition.Book = Book;
                OriginalEdition.BookId = ReplacementEdition.BookId = bookId;
                var path = Service.GetCoverPath(Book.Id, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, Original);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "cover-metadata.json"), new
                {
                    SelectedEdition = new { LocalEditionId = OriginalEdition.Id, EditionProviderId = BookEditionIdentity.GetTrustedForeignEditionId(OriginalEdition), CoverUrl = OriginalEdition.Images[0].Url, DownloadedAt = DateTime.UtcNow, ContentHash = MediaCoverRendition.ComputeContentSha256(Original) }
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

            public IBookService Books => Proxy<IBookService>((method, _) => method.Name switch
            {
                nameof(IBookService.GetBook) => Book,
                nameof(IBookService.GetExistingBooks) => new List<Book> { Book },
                _ => throw new AssertionException(method.Name)
            });
            public void Execute(DownloadBookMediaCommand command) => new DownloadMediaService(null, Books, Deferred, Service,
                null, LogManager.GetCurrentClassLogger()).Execute(command);
            public void Replace() => Service.EnsureBookCoversForImport(Book);
            public void Dispose() { _logs.Dispose(); Directory.Delete(_directory, true); }
        }


        internal static void AssertSelectionCommandReachesDeferredWriter(DownloadBookMediaCommand command, Book selectedBook, Edition storedEdition, bool expectReplaced)
        {
            var bookId = selectedBook.Id;
            Assert.That(command.BookId > 0 ? new[] { command.BookId } : command.BookIds.ToArray(), Does.Contain(bookId));
            using var h = new CoverSnapshotFixture(selectedBook: selectedBook, storedEdition: storedEdition);
            h.Deferred.IsCoverDownloadDeferred = true;
            h.Execute(command);
            var replaced = BookEditionIdentity.GetTrustedForeignEditionId(storedEdition) != BookEditionIdentity.GetTrustedForeignEditionId(h.ReplacementEdition);
            Assert.That(replaced, Is.EqualTo(expectReplaced), "The fixture must exercise the stated edition replacement");
            Assert.That(File.Exists(h.ReceiptPath), Is.EqualTo(!expectReplaced), "Remove the proven old edition locally before the import ends");
            Assert.That(File.Exists(h.Service.GetCoverPath(bookId, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png")), Is.EqualTo(!expectReplaced));
            Assert.That(h.Downloads, Is.Zero, "No network while importing");
            var flush = new DeferredCoverDownloadService(h.Deferred, h.Books, null, h.Service, LogManager.GetCurrentClassLogger());
            flush.HandleAsync(new NzbDrone.Core.MediaFiles.Events.ImportStageProgressEvent(
                NzbDrone.Core.MediaFiles.Events.ImportStage.ImportComplete, "done") { CommandId = 77 });
            Assert.That(h.Downloads, Is.EqualTo(expectReplaced ? 1 : 0));
            Assert.That(h.Service.ReadLocalBookCover(h.ReplacementEdition).Ready, Is.True);
        }

        [Test]
        public void embedding_rejects_placeholder_bytes_under_an_ordinary_legacy_cover_url()
        {
            using var h = new CoverSnapshotFixture();
            var path = h.Service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png");
            File.WriteAllBytes(path, Convert.FromBase64String(MediaCoverRenditionFixture.MascotWebpBase64));
            var receipt = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(h.ReceiptPath));
            receipt["selectedEdition"]["contentHash"] = null;
            File.WriteAllText(h.ReceiptPath, receipt.ToString());
            var cover = h.Service.ReadLocalBookCover(h.OriginalEdition);
            Assert.That(cover.Ready, Is.False, "Keep the audio file's art instead of embedding a silhouette");
            Assert.That(cover.Data, Is.Null);
            Assert.That(h.Downloads, Is.Zero);
        }

        [TestCase("current", true)]
        [TestCase("stale", false)]
        [TestCase("placeholder", false)]
        [TestCase("disabled", false)]
        public void calibre_uses_the_same_current_cover_reader(string scenario, bool expected)
        {
            using var h = new CoverSnapshotFixture();
            if (scenario == "placeholder")
            {
                // Captured Amazon shared silhouette, SHA-256 a5efe6ec..., a valid PNG.
                var png = Convert.FromBase64String("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");
                Assert.That(NzbDrone.Core.Books.Calibre.CalibreImageValidator.IsValidImage(png), Is.True);
                Assert.That(MediaCoverRendition.IsKnownPlaceholderImageHash(MediaCoverRendition.ComputeContentSha256(png)), Is.True);
                File.WriteAllBytes(h.Service.GetCoverPath(2, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".png"), png);
            }
            if (scenario == "stale") h.OriginalEdition.Images[0].Url = "https://images.example/new.png";
            Newtonsoft.Json.Linq.JObject payload = null;
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                payload = Newtonsoft.Json.Linq.JObject.Parse(System.Text.Encoding.UTF8.GetString(request.ContentData));
                return new HttpResponse(request, new HttpHeader(), "{}"); // Stop after capturing the real outbound write.
            });
            var logger = LogManager.GetCurrentClassLogger();
            var calibre = new NzbDrone.Core.Books.Calibre.CalibreProxy(http, h.Service, null, null, null, null,
                new NzbDrone.Common.Cache.CacheManager(), null,
                new NzbDrone.Core.Books.Calibre.CalibreLibraryGate(new NzbDrone.Core.Http.HttpServiceHealth(logger)), logger);
            var file = new NzbDrone.Core.MediaFiles.BookFile { CalibreId = 1, Author = new Author { Name = "Author" }, Edition = h.OriginalEdition };
            Assert.Catch<NzbDrone.Core.Books.Calibre.CalibreException>(() => calibre.SetFields(file,
                new NzbDrone.Core.Books.Calibre.CalibreSettings { Host = "localhost" }, scenario != "disabled"));
            Assert.That(payload, Is.Not.Null);
            Assert.That((string)payload["changes"]["cover"], Is.EqualTo(expected ? Convert.ToBase64String(h.Original) : null));
        }

        [Test]
        public void request_claimed_while_an_older_download_runs_is_requeued()
        {
            using var h = new CoverSnapshotFixture();
            var newer = new Book { Id = h.Book.Id, Editions = new List<Edition> { h.OriginalEdition } };
            Assert.That(newer.Editions.Single().Id, Is.Not.EqualTo(h.Book.Editions.Single().Id));
            h.BeforeDownload = () => h.Service.EnsureBookCoversForImport(newer);
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
            fixture.ReplacementEdition.Id = fixture.OriginalEdition.Id;
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
        [TestCase("other-edition", true)]
        [TestCase("unverified", true)]
        [TestCase("no-provider-id", true)]
        [TestCase("other-url", false)]
        [TestCase("no-receipt", false)]
        [TestCase("no-original", false)]
        [TestCase("empty-original", false)]
        [TestCase("rendition-only", false)]
        [TestCase("no-cover", true)]
        [TestCase("placeholder", false)]
        [TestCase("null-images", false)]
        [TestCase("no-cover-other-receipt", true)]
        public void local_cover_requires_current_url_and_usable_original(string scenario, bool expected)
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
                        LocalEditionId = scenario is "other-edition" or "no-cover-other-receipt" ? 4 : 3,
                        ContentHash = scenario == "unverified" ? null : MediaCoverRendition.ComputeContentSha256(new byte[] { 1, 2, 3 }),
                        EditionProviderId = scenario == "no-provider-id" ? null : scenario is "other-edition" or "no-cover-other-receipt" ? "hc:edition:456" : "hc:edition:123",
                        CoverUrl = scenario == "other-url" ? "https://example.test/other.jpg" : cover.Url,
                        DownloadedAt = DateTime.UtcNow
                    }
                }.ToJson());
                var result = service.ReadLocalBookCover(edition);
                Assert.That(result.Ready, Is.EqualTo(expected));
                Assert.That(result.Data, Is.EqualTo(expected && !scenario.StartsWith("no-cover") ? new byte[] { 1, 2, 3 } : null));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
