using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.MatchBench;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    [NonParallelizable]
    public class MatchBenchReleaseReplayCliFixture
    {
        private const int AuthorLocalId = 631;
        private const int TargetBookLocalId = 42421;
        private const int SiblingBookLocalId = 42407;
        private const int MamIndexerLocalId = 17;
        private const string FakeMamId = "matchbench-local-test-token";

        private string _workspace = string.Empty;
        private string _appData = string.Empty;
        private string _temporaryDirectory = string.Empty;
        private string _databasePath = string.Empty;
        private string _releasePath = string.Empty;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _workspace = Path.Combine(TestContext.CurrentContext.WorkDirectory, "matchbench-releasereplay-" + Guid.NewGuid().ToString("N"));
            _appData = Path.Combine(_workspace, "appdata");
            _temporaryDirectory = Path.Combine(_workspace, "temp");
            _databasePath = Path.Combine(_appData, "chaptarr.db");
            _releasePath = Path.Combine(_workspace, "releases.json");

            Directory.CreateDirectory(_appData);
            Directory.CreateDirectory(_temporaryDirectory);
            File.WriteAllText(Path.Combine(_appData, "config.xml"), "<Config />");
            CreateProductionSchemaAndCatalog(_databasePath);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }

        [Test]
        public async Task should_replay_real_catalog_and_report_the_production_verdicts()
        {
            WriteReleaseReports(includeSiblingPublishDate: true);

            var result = await RunReleaseReplay();
            Assert.That(result.ExitCode, Is.Zero, result.Output + Environment.NewLine + result.Error);
            var section = GetTargetBookSection(result.Output);

            Assert.Multiple(() =>
            {
                Assert.That(section, Does.Contain("audit=complete"));
                Assert.That(section, Does.Contain("APPROVED - James Hunt - Powerless (epub)"));
                Assert.That(section, Does.Contain("REJECTED - James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)"));
                Assert.That(section, Does.Contain("Release appears to match a different book by this author"));
            });
        }

        [Test]
        public async Task missing_publish_date_should_withhold_replay_verdicts_and_fail_the_cli()
        {
            WriteReleaseReports(includeSiblingPublishDate: false);

            var result = await RunReleaseReplay();
            Assert.That(result.ExitCode, Is.EqualTo(1), result.Output + Environment.NewLine + result.Error);
            var section = GetTargetBookSection(result.Output);

            Assert.Multiple(() =>
            {
                Assert.That(result.Output, Does.Contain("missing publishDate"));
                Assert.That(section, Does.Contain("decisions=2 verdicts=withheld audit=INCOMPLETE"));
                Assert.That(section, Does.Contain("INCOMPLETE - James Hunt - Powerless (epub)"));
                Assert.That(section, Does.Contain("INCOMPLETE - James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)"));
                Assert.That(section, Does.Not.Contain("approved="));
                Assert.That(section, Does.Not.Contain("APPROVED"));
                Assert.That(section, Does.Not.Contain("REJECTED"));
                Assert.That(section, Does.Not.Contain("PROVISIONAL"));
            });
        }

        [Test]
        public async Task mambench_should_use_local_native_mam_transport_and_production_decisions()
        {
            await using var responder = await LoopbackMamResponder.StartAsync(CreateMamResponse());
            SeedMamIndexer(responder.BaseUrl);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await RunMatchBench(
                "/mambench",
                cancellation.Token,
                $"/proxy={responder.BaseUrl}",
                $"/indexerid={MamIndexerLocalId}",
                $"/bookids={TargetBookLocalId}");

            Assert.That(result.ExitCode, Is.Zero, result.Output + Environment.NewLine + result.Error);
            var section = GetMamBenchBookSection(result.Output);
            var requests = responder.Requests;
            Assert.That(requests, Has.Count.EqualTo(1));

            var request = requests[0];
            var mamBaseUri = new Uri(responder.BaseUrl);

            Assert.Multiple(() =>
            {
                Assert.That(section, Does.Contain("tiers: configured="));
                Assert.That(section, Does.Contain("audit=complete"));
                Assert.That(section, Does.Contain("APPROVED - James Hunt - Powerless (epub)"));
                Assert.That(section, Does.Contain("REJECTED - James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)"));
                Assert.That(section, Does.Contain("Release appears to match a different book by this author"));
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.RawTarget, Is.EqualTo($"{responder.BaseUrl}/tor/js/loadSearchJSONbasic.php"));
                Assert.That(request.Host, Does.StartWith("127.0.0.1:"));
                Assert.That(mamBaseUri.IsLoopback, Is.True);
                Assert.That(request.Host, Does.Contain(mamBaseUri.Port.ToString()));
                Assert.That(request.Headers.TryGetValue("Cookie", out var cookie), Is.True);
                Assert.That(cookie, Does.Contain($"mam_id={FakeMamId}"));
            });

            using var payload = JsonDocument.Parse(request.Body);
            Assert.Multiple(() =>
            {
                Assert.That(payload.RootElement.GetProperty("tor").GetProperty("text").GetString(), Does.Contain("Powerless"));
                Assert.That(payload.RootElement.GetProperty("tor").GetProperty("text").GetString(), Does.Contain("James Hunt"));
                Assert.That(payload.RootElement.GetProperty("tor").GetProperty("main_cat")[0].GetString(), Is.EqualTo("14"));
            });
        }

        private void CreateProductionSchemaAndCatalog(string databasePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString;

            new MigrationController(LogManager.GetLogger(nameof(MatchBenchReleaseReplayCliFixture)), null)
                .Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            var authorName = "James Hunt";
            var authorCleanName = NzbDrone.Core.Parser.Parser.CleanAuthorName(authorName);
            var added = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var ebookQualityProfileId = connection.QuerySingle<int>(
                "SELECT \"Id\" FROM \"QualityProfiles\" WHERE \"Name\" = 'E-Book';");
            var ebookMetadataProfileId = connection.QuerySingle<int>(
                "SELECT \"Id\" FROM \"MetadataProfiles\" WHERE \"Name\" = 'Ebook Default';");

            connection.Execute(@"
                INSERT INTO ""Authors"" (
                    ""Id"", ""Name"", ""CleanName"", ""Path"", ""Monitored"", ""Added"",
                    ""QualityProfileId"", ""MetadataProfileId"", ""EbookQualityProfileId"", ""EbookMetadataProfileId"")
                VALUES (@id, @name, @cleanName, @path, 1, @added, @ebookQualityProfileId, @ebookMetadataProfileId, @ebookQualityProfileId, @ebookMetadataProfileId);",
                new
                {
                    id = AuthorLocalId,
                    name = authorName,
                    cleanName = authorCleanName,
                    path = Path.Combine(_appData, "library", authorName),
                    added,
                    ebookQualityProfileId,
                    ebookMetadataProfileId
                });

            InsertBook(connection, TargetBookLocalId, "Powerless", "A Tale Of Survival in a Powerless World", "1-4", "hc:2222947", null, added);
            InsertBook(connection, SiblingBookLocalId, "The Final Homestead", "EMP Survival in a Powerless World", "1", null, "gr:68634594", added);

            connection.Execute(@"
                INSERT INTO ""Editions"" (
                    ""Id"", ""BookId"", ""Title"", ""Monitored"", ""MediaType"", ""IsEbook"", ""Format"", ""EditionFormat"", ""Language"", ""ReleaseDate"")
                VALUES
                    (@targetEditionId, @targetBookId, 'Powerless', 1, 1, 1, 'epub', 'Ebook', 'eng', @releaseDate),
                    (@siblingEditionId, @siblingBookId, 'The Final Homestead', 1, 1, 1, 'epub', 'Ebook', 'eng', @releaseDate);",
                new
                {
                    targetEditionId = TargetBookLocalId,
                    targetBookId = TargetBookLocalId,
                    siblingEditionId = SiblingBookLocalId,
                    siblingBookId = SiblingBookLocalId,
                    releaseDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                });
        }

        private static void InsertBook(SqliteConnection connection, int id, string title, string seriesName, string seriesPosition, string hardcoverBookId, string goodreadsWorkId, DateTime added)
        {
            connection.Execute(@"
                INSERT INTO ""Books"" (
                    ""Id"", ""AuthorId"", ""Title"", ""CleanTitle"", ""SeriesName"", ""SeriesPosition"",
                    ""AudiobookMonitored"", ""EbookMonitored"", ""AnyEditionOk"", ""Added"", ""MediaType"",
                    ""HardcoverBookId"", ""GoodreadsWorkId"")
                VALUES (
                    @id, @authorId, @title, @cleanTitle, @seriesName, @seriesPosition,
                    0, 1, 0, @added, 1, @hardcoverBookId, @goodreadsWorkId);",
                new
                {
                    id,
                    authorId = AuthorLocalId,
                    title,
                    cleanTitle = NzbDrone.Core.Parser.Parser.CleanAuthorName(NzbDrone.Core.Parser.Parser.CleanBookTitle(title)),
                    seriesName,
                    seriesPosition,
                    hardcoverBookId,
                    goodreadsWorkId,
                    added
                });
        }

        private void WriteReleaseReports(bool includeSiblingPublishDate)
        {
            var reports = new List<Dictionary<string, object>>
            {
                CreateReport("powerless", "James Hunt - Powerless (epub)", includePublishDate: true),
                CreateReport(
                    "final-homestead",
                    "James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)",
                    includePublishDate: includeSiblingPublishDate)
            };

            File.WriteAllText(_releasePath, System.Text.Json.JsonSerializer.Serialize(reports));
        }

        private void SeedMamIndexer(string baseUrl)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ConnectionString;
            var settings = new MyAnonaMouseSettings
            {
                BaseUrl = baseUrl,
                MamId = FakeMamId,
                MamSsl = true,
                UnsatisfiedCount = 0,
                UnsatisfiedLimit = 100,
                UnsatisfiedSnapshotUtc = DateTime.UtcNow,
                UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow
            };

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            connection.Execute(@"
                INSERT INTO ""Indexers"" (
                    ""Id"", ""Name"", ""Implementation"", ""Settings"", ""ConfigContract"",
                    ""Enable"", ""SupportsRss"", ""SupportsSearch"", ""Priority"", ""EnableInteractiveSearch"")
                VALUES (
                    @id, 'Local MyAnonaMouse', 'MyAnonaMouse', @settings, 'MyAnonaMouseSettings',
                    1, 1, 1, 1, 1);",
                new { id = MamIndexerLocalId, settings = JsonConvert.SerializeObject(settings) });
        }

        private static string CreateMamResponse()
        {
            var authorInfo = JsonConvert.SerializeObject(new Dictionary<string, string> { ["1"] = "James Hunt" });
            return JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["found"] = 2,
                ["data"] = new[]
                {
                    CreateMamReport(901, "James Hunt - Powerless (epub)", authorInfo),
                    CreateMamReport(902, "James Hunt - The Final Homestead - EMP Survival in a Powerless World (epub)", authorInfo)
                }
            });
        }

        private static Dictionary<string, object> CreateMamReport(int id, string title, string authorInfo)
        {
            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["title"] = title,
                ["dl"] = $"fake-download-{id}",
                ["size"] = "1 MiB",
                ["seeders"] = 8,
                ["leechers"] = 0,
                ["added"] = "2026-09-20 12:00:00",
                ["author_info"] = authorInfo,
                ["category"] = 39,
                ["main_cat"] = 14,
                ["mediatype"] = 2,
                ["catname"] = "E-Books - Sci-Fi",
                ["filetype"] = "epub"
            };
        }

        private static Dictionary<string, object> CreateReport(string guid, string title, bool includePublishDate)
        {
            var report = new Dictionary<string, object>
            {
                ["guid"] = guid,
                ["title"] = title,
                ["author"] = "James Hunt",
                ["indexer"] = "OfflineReplay",
                ["indexerId"] = 1,
                ["downloadProtocol"] = "Torrent",
                ["size"] = 1_000_000,
                ["downloadUrl"] = "https://offline.invalid/release",
                ["magnetUrl"] = "magnet:?xt=urn:btih:offline",
                ["seeders"] = 1,
                ["peers"] = 0,
                ["fileType"] = "epub",
                ["categories"] = new[] { 7020 }
            };

            if (includePublishDate)
            {
                report["publishDate"] = "2026-09-20T00:00:00Z";
            }

            return report;
        }

        private Task<CliResult> RunReleaseReplay()
        {
            return RunMatchBench(
                "/releasereplay",
                CancellationToken.None,
                $"/bookids={TargetBookLocalId}",
                $"/releases={_releasePath}");
        }

        private async Task<CliResult> RunMatchBench(string command, CancellationToken cancellationToken, params string[] arguments)
        {
            var testAssemblyPath = typeof(MatchBenchReleaseReplayCliFixture).Assembly.Location;
            var matchBenchAssemblyPath = typeof(Program).Assembly.Location;
            var runtimeConfigPath = Path.ChangeExtension(testAssemblyPath, ".runtimeconfig.json");
            var depsPath = Path.ChangeExtension(testAssemblyPath, ".deps.json");
            Assert.That(File.Exists(runtimeConfigPath), Is.True, "The test runtimeconfig is required to launch the built MatchBench CLI.");
            Assert.That(File.Exists(depsPath), Is.True, "The test dependency manifest is required to launch the built MatchBench CLI.");

            var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(dotnetHostPath) ? "dotnet" : dotnetHostPath,
                WorkingDirectory = _appData,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfigPath);
            startInfo.ArgumentList.Add("--depsfile");
            startInfo.ArgumentList.Add(depsPath);
            startInfo.ArgumentList.Add(matchBenchAssemblyPath);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add($"/data={_appData}");
            startInfo.ArgumentList.Add($"/db={_databasePath}");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["TMP"] = _temporaryDirectory;
            startInfo.Environment["TEMP"] = _temporaryDirectory;
            startInfo.Environment["TMPDIR"] = _temporaryDirectory;

            foreach (var key in startInfo.Environment.Keys
                         .Where(key => key.Equals("PostgresHost", StringComparison.OrdinalIgnoreCase) ||
                                       key.EndsWith("__Postgres__Host", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                startInfo.Environment.Remove(key);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the built MatchBench CLI.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
                var timedOutOutput = await outputTask.ConfigureAwait(false);
                var timedOutError = await errorTask.ConfigureAwait(false);
                throw new TimeoutException("The MatchBench CLI did not exit within 45 seconds." + Environment.NewLine + timedOutOutput + Environment.NewLine + timedOutError);
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            return new CliResult(process.ExitCode, output, error);
        }

        private static string GetMamBenchBookSection(string output)
        {
            const string sectionStart = "[mambench] James Hunt — Powerless";
            const string sectionEnd = "[mambench] completed books=";
            var start = output.IndexOf(sectionStart, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "The CLI did not emit the MAM target book results." + Environment.NewLine + output);
            var end = output.IndexOf(sectionEnd, start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), "The CLI did not emit the MAM completion summary." + Environment.NewLine + output);
            return output[start..end];
        }

        private static string GetTargetBookSection(string output)
        {
            const string sectionStart = "[releasereplay] James Hunt — Powerless";
            const string sectionEnd = "[releasereplay] completed books=";
            var start = output.IndexOf(sectionStart, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "The CLI did not emit the target book summary." + Environment.NewLine + output);
            var end = output.IndexOf(sectionEnd, start, StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start), "The CLI did not emit the replay completion summary." + Environment.NewLine + output);
            return output[start..end];
        }

        private sealed record CliResult(int ExitCode, string Output, string Error);

        private sealed class LoopbackMamResponder : IAsyncDisposable
        {
            private readonly WebApplication _app;
            private readonly string _responseBody;
            private readonly List<MamRequest> _requests = new List<MamRequest>();
            private readonly object _requestsLock = new object();

            private LoopbackMamResponder(WebApplication app, string responseBody)
            {
                _app = app;
                _responseBody = responseBody;
            }

            public string BaseUrl { get; private set; }

            public IReadOnlyList<MamRequest> Requests
            {
                get
                {
                    lock (_requestsLock)
                    {
                        return _requests.ToArray();
                    }
                }
            }

            public static async Task<LoopbackMamResponder> StartAsync(string responseBody)
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                var app = builder.Build();
                var responder = new LoopbackMamResponder(app, responseBody);
                app.Run(responder.HandleRequestAsync);
                await app.StartAsync().ConfigureAwait(false);

                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses;
                var address = new Uri(addresses.Single());
                responder.BaseUrl = $"http://127.0.0.1:{address.Port}";
                return responder;
            }

            public async ValueTask DisposeAsync()
            {
                await _app.DisposeAsync().ConfigureAwait(false);
            }

            private async Task HandleRequestAsync(HttpContext context)
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
                var rawTarget = context.Features.Get<IHttpRequestFeature>().RawTarget;
                var headers = context.Request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                lock (_requestsLock)
                {
                    _requests.Add(new MamRequest(context.Request.Method, rawTarget, context.Request.Host.Value, headers, body));
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(_responseBody, context.RequestAborted).ConfigureAwait(false);
            }
        }

        private sealed record MamRequest(string Method, string RawTarget, string Host, IDictionary<string, string> Headers, string Body);
    }
}
