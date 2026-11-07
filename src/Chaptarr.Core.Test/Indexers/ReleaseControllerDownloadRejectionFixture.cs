using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Indexers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    [NonParallelizable]
    public class ReleaseControllerDownloadRejectionFixture
    {
        private LoggingConfiguration _previousConfiguration;

        [SetUp]
        public void SetUp()
        {
            _previousConfiguration = LogManager.Configuration;
        }

        [TearDown]
        public void TearDown()
        {
            LogManager.Configuration = _previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }

        [Test]
        public void should_return_the_download_client_rejection_detail_without_blaming_the_indexer()
        {
            var releaseInfo = new ReleaseInfo { Title = "Test Release" };
            var innerException = new DownloadClientException("qBittorrent API request failed (HTTP 409 Conflict). Torrent is already added");
            var rejection = new DownloadClientRejectedReleaseException(
                releaseInfo,
                "qBittorrent rejected the magnet link due to a conflict",
                innerException);
            var memory = ConfigureLogging();
            var cacheManager = new CacheManager();
            var controller = CreateController(cacheManager, new RejectingDownloadService(rejection));
            var resource = new ReleaseResource
            {
                IndexerId = 1,
                Guid = "test-release"
            };

            cacheManager.GetCache<DownloadDecision>(typeof(ReleaseController), "downloadDecisions")
                .Set("1_test-release", new DownloadDecision(new RemoteBook
                {
                    Release = releaseInfo,
                    Author = new Author(),
                    Books = new List<Book> { new() { Id = 1, Title = "Test Book" } }
                }));

            var exception = Assert.ThrowsAsync<NzbDroneClientException>(async () => await controller.DownloadRelease(resource));

            Assert.Multiple(() =>
            {
                Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(
                    exception.Message,
                    Is.EqualTo("Download client rejected release: qBittorrent rejected the magnet link due to a conflict: qBittorrent API request failed (HTTP 409 Conflict). Torrent is already added"));
                Assert.That(memory.Logs, Has.Some.EqualTo("Warn|Download client rejected release"));
                Assert.That(memory.Logs, Has.None.StartsWith("Error|"));
            });
        }

        [Test]
        public async Task should_return_a_specific_conflict_when_a_torrent_is_already_in_the_client()
        {
            var releaseInfo = new ReleaseInfo { Title = "Test Release" };
            var cacheManager = new CacheManager();
            var controller = CreateController(cacheManager, new ExistingTorrentDownloadService());
            var resource = new ReleaseResource
            {
                IndexerId = 1,
                Guid = "test-release"
            };

            cacheManager.GetCache<DownloadDecision>(typeof(ReleaseController), "downloadDecisions")
                .Set("1_test-release", new DownloadDecision(new RemoteBook
                {
                    Release = releaseInfo,
                    Author = new Author(),
                    Books = new List<Book> { new() { Id = 1, Title = "Test Book" } }
                }));

            var result = await controller.DownloadRelease(resource);
            Assert.That(result.Result, Is.InstanceOf<ConflictObjectResult>());
            var conflict = (ConflictObjectResult)result.Result;

            Assert.Multiple(() =>
            {
                Assert.That(conflict.StatusCode, Is.EqualTo((int)HttpStatusCode.Conflict));
                Assert.That(conflict.Value.GetType().GetProperty("code")?.GetValue(conflict.Value), Is.EqualTo("existingTorrent"));
            });
        }

        private static ReleaseController CreateController(ICacheManager cacheManager, IDownloadService downloadService)
        {
            var controller = new ReleaseController(
                rssFetcherAndParser: null,
                releaseSearchService: null,
                downloadDecisionMaker: null,
                prioritizeDownloadDecision: null,
                downloadService: downloadService,
                authorService: null,
                bookService: null,
                parsingService: null,
                indexerFactory: null,
                cacheManager: cacheManager,
                logger: LogManager.GetLogger(nameof(ReleaseControllerDownloadRejectionFixture)));

            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = context
            };

            return controller;
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memory = new MemoryTarget("release-controller-rejection-memory")
            {
                Layout = "${level}|${message}"
            };
            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memory, nameof(ReleaseControllerDownloadRejectionFixture));
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();
            return memory;
        }

        private sealed class RejectingDownloadService : IDownloadService
        {
            private readonly DownloadClientRejectedReleaseException _rejection;

            public RejectingDownloadService(DownloadClientRejectedReleaseException rejection)
            {
                _rejection = rejection;
            }

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                return Task.FromException(_rejection);
            }

            public Task AdoptExistingTorrent(RemoteBook remoteBook, int? downloadClientId)
            {
                return Task.FromException(_rejection);
            }
        }

        private sealed class ExistingTorrentDownloadService : IDownloadService
        {
            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                return Task.FromException(new ExistingTorrentFoundException(
                    "Test client",
                    new DownloadClientItem { DownloadId = "abcdef123456", Title = "Already queued" }));
            }

            public Task AdoptExistingTorrent(RemoteBook remoteBook, int? downloadClientId)
            {
                return DownloadReport(remoteBook, downloadClientId);
            }
        }
    }
}
