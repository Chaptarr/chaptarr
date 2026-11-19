using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void failed_cleanup_batch_rotates_before_the_next_bounded_retry([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-cleanup-fairness-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var rows = new ConversionJobRepository(database, new StubEventAggregator());
                using var factory = new LogFactory();
                var sink = new ConcurrentLogTarget { Layout = "${level}|${message}" };
                var configuration = new NLog.Config.LoggingConfiguration(factory);
                configuration.AddRuleForAllLevels(sink);
                factory.Configuration = configuration;
                var service = CreateService(rows, new UnusedConversionService(), logger: factory.GetLogger("cleanup-fairness"));
                var failed = new List<ConversionJob>();
                try
                {
                    for (var index = 0; index <= ConversionJobRepository.CleanupBatchSize; index++)
                    {
                        var id = "cleanup-" + index;
                        var folder = Path.Combine(directory, ".chaptarr-conversions", id, "attempt");
                        Directory.CreateDirectory(folder);
                        var request = new ConversionJobRequest { DownloadId = id, DiscardRequested = true,
                            WorkRoot = Path.GetDirectoryName(folder), WorkFolder = folder, OutputPath = Path.Combine(folder, "book.m4b") };
                        if (index < ConversionJobRepository.CleanupBatchSize)
                        {
                            File.WriteAllText(Path.Combine(folder, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                            {
                                DownloadId = "another-owner", OutputPath = request.OutputPath
                            }));
                        }
                        var job = rows.Insert(new ConversionJob { DownloadId = id, Status = ConversionJobStatus.Cancelled,
                            RequestJson = JsonSerializer.Serialize(request), WorkRoot = request.WorkRoot, WorkFolder = folder, OutputPath = request.OutputPath,
                            CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1) });
                        if (index < ConversionJobRepository.CleanupBatchSize) failed.Add(job);
                    }
                    var retry = typeof(ConversionJobService).GetMethod("RetryDiscardCleanup", BindingFlags.NonPublic | BindingFlags.Instance);
                    retry.Invoke(service, null);
                    Assert.That(rows.FindByDownloadId("cleanup-100"), Is.Not.Null);
                    Assert.That(rows.PendingCleanup().First().DownloadId, Is.EqualTo("cleanup-100"));
                    retry.Invoke(service, null);
                    Assert.That(rows.FindByDownloadId("cleanup-100"), Is.Null, "Permanent failures must not starve later cleanup");
                    Assert.That(failed.All(job => rows.FindByDownloadId(job.DownloadId) != null), Is.True);
                    Assert.That(failed.All(job => File.Exists(Path.Combine(job.WorkFolder, "conversion-artifact.json"))), Is.True);
                    Assert.That(sink.Logs.Any(message => message.Contains("attempted=100, succeeded=0, failed=100, elapsedMs=")), Is.True);
                    Assert.That(sink.Logs.Any(message => message.Contains("attempted=1, succeeded=1, failed=0, elapsedMs=")), Is.True);
                }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }
    }
}
