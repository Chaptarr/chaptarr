using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Http;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    public class CalibrePagingFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(true, false, true, false)]
        [TestCase(true, false, true, true)]
        [TestCase(true, false, false, false, true)]
        public void catalog_lookup_and_health_check_populate_identity_cache_across_pages(bool health, bool failedFirstPage, bool precedingFailure = false, bool wrongRoot = false, bool precedingError = false)
        {
            var rootPath = @"C:\library".AsOsAgnostic();
            var root = new RootFolder { Path = rootPath, IsCalibreLibrary = true, CalibreSettings = new CalibreSettings { Host = "localhost", Library = "books" } };
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}: ${message} ${exception:format=tostring}" };
            var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, messages); logs.Configuration = config;
            var logger = logs.GetLogger("CalibrePagingTest");
            var pageSizes = new List<int>();
            var categoryReads = 0;
            var mapped = 0;
            var cache = new CacheManager();
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                if (request.Url.ToString().Contains("/down")) throw new InvalidOperationException("First library is unavailable");
                var query = HttpUtility.ParseQueryString(new Uri(request.Url.ToString()).Query);
                if (request.Url.ToString().Contains("ajax/category"))
                {
                    categoryReads++;
                    Assert.That(query["num"], Is.EqualTo(int.MaxValue.ToString()));
                    Assert.That(query["offset"], Is.EqualTo("0"));
                    Assert.That(request.LogHttpError, Is.False);
                    Assert.That(request.LogResponseContent, Is.False);
                    Assert.That(request.RedactUrlInLogs, Is.False);
                    var offset = int.Parse(query["offset"]);
                    return new HttpResponse<CalibreCategory>(new HttpResponse(request, new HttpHeader(), new CalibreCategory
                    {
                        TotalNum = request.Url.ToString().Contains("/error") ? 1 : 751,
                        BookIds = Enumerable.Range(1, request.Url.ToString().Contains("/error") ? 1 : 751).Skip(offset).Take(int.Parse(query["num"])).ToList()
                    }.ToJson()));
                }
                Assert.That(request.Url.ToString(), Does.Contain("ajax/books"));
                Assert.That(query["category_urls"], Is.EqualTo("false").IgnoreCase);
                Assert.That(request.RedactUrlInLogs, Is.True);
                Assert.That(request.LogHttpError, Is.False);
                Assert.That(request.LogResponseContent, Is.False);
                var ids = query["ids"].Split(',').Select(int.Parse).ToList(); pageSizes.Add(ids.Count);
                if (failedFirstPage)
                {
                    // Exercise the actual exception formatter with the full 750-ID request.
                    var error = new HttpException(new HttpResponse(request, new HttpHeader(), "gateway", HttpStatusCode.BadGateway));
                    logger.Error(error, "Request failed: {0}", request.GetLogUrl());
                    throw error;
                }
                var books = ids.ToDictionary(id => id, id => new CalibreBook
                {
                    Id = id,
                    Formats = new() { ["epub"] = new CalibreBookFormat { Path = "/remote/" + id + ".epub" } }
                });
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(), books.ToJson()));
            });
            var proxy = new CalibreProxy(http, null, Proxy<IRemotePathMappingService>((_, args) =>
            {
                var raw = ((OsPath)args[1]).FullPath;
                Assert.That(raw, Does.StartWith("/remote/"), "A local path must not be mapped twice");
                mapped++;
                return new OsPath(Path.Combine(rootPath, "author", "book", Path.GetFileName(raw)));
            }), null, null, null, cache, null, new CalibreLibraryGate(new HttpServiceHealth(logger)), logger);
            var folders = new List<RootFolder>();
            if (precedingFailure) folders.Add(new RootFolder { IsCalibreLibrary = true, CalibreSettings = new CalibreSettings { Host = "localhost", Library = "down" } });
            if (precedingError) folders.Add(new RootFolder { Path = Path.Combine(rootPath, "wrong-root"), IsCalibreLibrary = true, CalibreSettings = new CalibreSettings { Host = "localhost", Library = "error" } });
            if (wrongRoot) root.Path = Path.Combine(rootPath, "wrong-root");
            folders.Add(root);
            var roots = Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All) ? folders : root);
            var disk = Proxy<IDiskProvider>((_, _) => true);
            if (health)
            {
                var result = new CalibreRootFolderCheck(disk, roots, proxy, null, logger, null).Check();
                Assert.That(result.Type, Is.EqualTo((wrongRoot || precedingError) ? HealthCheckResult.Error : failedFirstPage || precedingFailure ? HealthCheckResult.Warning : HealthCheckResult.Ok));
            }
            else
            {
                if (failedFirstPage)
                {
                    var error = Assert.Throws<CalibreException>(() => proxy.GetAllBookFilePaths(root.CalibreSettings));
                    Assert.That(error.Message.Length, Is.LessThan(200));
                    Assert.That(error.ToString(), Does.Not.Contain("ids=").And.Not.Contain("ajax/books"));
                    Assert.That(error.ToString().Length, Is.LessThan(5000), "Includes stack traces, not 750 IDs");
                }
                else
                {
                    Assert.That(proxy.GetAllBookFilePaths(root.CalibreSettings), Has.Count.EqualTo(751));
                }
            }
            var lastBook = cache.GetCache<CalibreBook>(typeof(CalibreProxy)).Find(Path.Combine(rootPath, "author", "book", "751.epub"));
            Assert.That(lastBook?.Id, Is.EqualTo(failedFirstPage ? (int?)null : 751),
                "The health check must populate identity beyond the first page without a preceding catalog read");
            Assert.That(categoryReads, Is.EqualTo(precedingError ? 2 : 1));
            Assert.That(pageSizes, Is.EqualTo(failedFirstPage ? new[] { 750 } : precedingError ? new[] { 1, 750, 1 } : new[] { 750, 1 }));
            Assert.That(mapped, Is.EqualTo(failedFirstPage ? 0 : precedingError ? 752 : 751));
        }

        [TestCase("ID snapshot")]
        [TestCase("book page 2")]
        public void health_warning_and_catalog_summary_keep_the_failed_stage(string stage)
        {
            using var logs = new LogFactory();
            var messages = new MemoryTarget { Layout = "${level}: ${message}" };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, messages);
            logs.Configuration = config;
            var logger = logs.GetLogger("catalog-stage");
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                if (stage == "ID snapshot" || request.Url.ToString().Contains("ids=751"))
                    throw new HttpException(new HttpResponse(request, new HttpHeader(), "gateway", HttpStatusCode.BadGateway));
                if (request.Url.ToString().Contains("ajax/category"))
                    return new HttpResponse<CalibreCategory>(new HttpResponse(request, new HttpHeader(), new CalibreCategory { TotalNum = 751, BookIds = Enumerable.Range(1, 751).ToList() }.ToJson()));
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(), new Dictionary<int, CalibreBook> { [1] = new() { Id = 1 } }.ToJson()));
            });
            var proxy = new CalibreProxy(http, null, null, null, null, null, new CacheManager(), null, null, logger);
            var roots = Proxy<IRootFolderService>((_, _) => new List<RootFolder> { new() { Name = "Books", IsCalibreLibrary = true, CalibreSettings = new() { Host = "localhost", Library = "books" } } });
            var result = new CalibreRootFolderCheck(null, roots, proxy, null, logger, null).Check();
            Assert.That(result.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(messages.Logs.Single(line => line.StartsWith("Warn:")),
                Is.EqualTo("Warn: Unable to check Calibre root folder Books: Unable to read Calibre catalog while reading " +
                    (stage == "ID snapshot" ? "the ID snapshot" : stage) + ": HTTP 502 BadGateway"));
            Assert.That(messages.Logs, Has.Some.Contains("failed while reading " + (stage == "ID snapshot" ? "the ID snapshot" : stage)));
        }

        [Test]
        public void catalog_failure_keeps_the_access_denied_diagnosis()
        {
            var proxy = new CalibreProxy(Proxy<IHttpClient>((_, args) => throw new HttpException(
                new HttpResponse((HttpRequest)args[0], new HttpHeader(), "", HttpStatusCode.Unauthorized))),
                null, null, null, null, null, new CacheManager(), null, null, LogManager.GetCurrentClassLogger());
            var error = Assert.Throws<CalibreException>(() => proxy.GetAllBookFilePaths(new CalibreSettings { Host = "localhost", Library = "books" }));
            Assert.That(error.Message, Is.EqualTo("Unable to read Calibre catalog while reading the ID snapshot: Calibre refused access: HTTP 401 Unauthorized"));
        }

        [TestCase(2000, 0)]
        [TestCase(2000, 100)]
        [TestCase(1, 2)]
        [TestCase(3, 2)]
        public void truncated_or_lying_id_snapshot_fails_loudly(int total, int count)
        {
            var calls = 0;
            var logger = LogManager.GetCurrentClassLogger();
            var http = Proxy<IHttpClient>((_, args) =>
            {
                calls++;
                return new HttpResponse<CalibreCategory>(new HttpResponse((HttpRequest)args[0], new HttpHeader(), new CalibreCategory
                {
                    TotalNum = total,
                    BookIds = Enumerable.Range(1, count).ToList()
                }.ToJson()));
            });
            var proxy = new CalibreProxy(http, null, null, null, null, null, new CacheManager(), null, null, logger);
            var error = Assert.Throws<CalibreInvalidResponseException>(() => proxy.GetAllBookIds(new CalibreSettings { Host = "localhost", Library = "books" }));
            Assert.That(error.Message, Does.Contain($"{count} distinct book IDs").And.Contain($"reported {total}"));
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_format_does_not_hide_available_format_or_stop_catalog(bool alternate)
        {
            var cache = new CacheManager();
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                if (new Uri(request.Url.ToString()).AbsolutePath.Contains("/ajax/category/"))
                    return new HttpResponse<CalibreCategory>(new HttpResponse(request, new HttpHeader(), "{\"total_num\":2,\"book_ids\":[1,2]}"));
                var formats = new Dictionary<string, CalibreBookFormat> { ["epub"] = new() };
                if (alternate) formats["azw3"] = new() { Path = @"C:\library\one.azw3".AsOsAgnostic(), LastModified = DateTime.UtcNow };
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(), new Dictionary<int, CalibreBook>
                {
                    [1] = new() { Id = 1, Formats = formats },
                    [2] = new() { Id = 2, Formats = new() { ["epub"] = new() { Path = @"C:\library\two.epub".AsOsAgnostic() } } }
                }.ToJson()));
            });
            var proxy = new CalibreProxy(http, null, Proxy<IRemotePathMappingService>((_, args) =>
            {
                Assert.That(((OsPath)args[1]).FullPath, Is.Not.Empty);
                return args[1];
            }), null, null, null, cache, null, null, LogManager.GetCurrentClassLogger());
            var paths = proxy.GetAllBookFilePaths(new CalibreSettings { Host = "localhost", Library = "books" });
            Assert.That(paths, Has.Count.EqualTo(alternate ? 2 : 1));
            if (alternate) Assert.That(cache.GetCache<CalibreBook>(typeof(CalibreProxy)).Find(@"C:\library\one.azw3".AsOsAgnostic()).Id, Is.EqualTo(1));
            Assert.That(cache.GetCache<CalibreBook>(typeof(CalibreProxy)).Find(@"C:\library\two.epub".AsOsAgnostic()).Id, Is.EqualTo(2));
        }

        [TestCase(100000, 0)]
        [TestCase(1501, 1)]
        [TestCase(1501, -1)]
        public void id_snapshot_survives_catalog_changes_during_detail_reads(int count, int change)
        {
            var categoryReads = 0;
            var detailReads = 0;
            var settings = new CalibreSettings { Host = "localhost", Library = "books" };
            var http = Proxy<IHttpClient>((_, args) =>
            {
                var request = (HttpRequest)args[0];
                if (new Uri(request.Url.ToString()).AbsolutePath.Contains("/ajax/category/"))
                {
                    categoryReads++;
                    return new HttpResponse<CalibreCategory>(new HttpResponse(request, new HttpHeader(), new CalibreCategory
                    { TotalNum = count, BookIds = Enumerable.Range(1, count).ToList() }.ToJson()));
                }
                detailReads++;
                var ids = HttpUtility.ParseQueryString(new Uri(request.Url.ToString()).Query)["ids"].Split(',').Select(int.Parse).ToList();
                Assert.That(ids.Count, Is.LessThanOrEqualTo(750));
                // The live catalog gains a newer ID, or loses the final ID, after the snapshot.
                var live = Enumerable.Range(1, count + change).ToHashSet();
                var books = ids.ToDictionary(id => id, id => live.Contains(id) ? new CalibreBook
                {
                    Id = id,
                    Formats = new() { ["epub"] = new() { Path = Path.Combine(@"C:\library".AsOsAgnostic(), id + ".epub") } }
                } : null);
                return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(), books.ToJson()));
            });
            var proxy = new CalibreProxy(http, null, Proxy<IRemotePathMappingService>((_, args) => args[1]), null,
                null, null, new CacheManager(), null, null, LogManager.GetCurrentClassLogger());
            Assert.That(proxy.GetAllBookFilePaths(settings), Has.Count.EqualTo(count + Math.Min(change, 0)));
            Assert.That(categoryReads, Is.EqualTo(1));
            Assert.That(detailReads, Is.EqualTo((count + 749) / 750));
        }
    }
}
