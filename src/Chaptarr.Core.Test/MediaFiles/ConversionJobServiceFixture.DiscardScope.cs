using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ConversionJobServiceFixture
    {
        [Test]
        public void bonus_discard_scope_preserves_other_attempts_for_the_same_download([Values(false, true)] bool postgres,
            [Values("immediate", "restarted", "whole-download")] string cleanup)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "conversion-discard-scope-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var rows = new ConversionJobRepository(database, new StubEventAggregator());
                var request = CreateRequest(directory, Guid.NewGuid().ToString("N"));
                var converter = new RetainedResultConverter { Success = true };
                var worker = CreateService(rows, converter);
                try
                {
                    RunWorker(worker, request);
                    var grabbedFolder = Path.Combine(request.WorkRoot, "grabbed-format-attempt");
                    Directory.CreateDirectory(grabbedFolder);
                    var grabbedOutput = Path.Combine(grabbedFolder, "grabbed.m4b");
                    File.WriteAllText(grabbedOutput, "keep the grabbed format");
                    File.WriteAllText(Path.Combine(grabbedFolder, "conversion-artifact.json"), JsonSerializer.Serialize(new ConversionArtifactManifest
                    {
                        DownloadId = request.DownloadId, OutputPath = grabbedOutput,
                        EncoderSucceeded = true, OutputSize = new FileInfo(grabbedOutput).Length,
                        Sources = new List<ConversionArtifactSource> { new() { Path = Path.Combine(directory, "grabbed-source.mp3"), Size = 1 } }
                    }));
                    var historyReads = 0;
                    var history = InterfaceProxy.Proxy<IHistoryService>((method, _) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IHistoryService.FindByDownloadId)));
                        historyReads++;
                        return new List<EntityHistory> { new() { EventType = EntityHistoryEventType.BookFileConverted,
                            Data = new Dictionary<string, string> { ["ConvertedPath"] = grabbedOutput } } };
                    });
                    var serviceRows = (IConversionJobRepository)rows;
                    if (cleanup == "restarted")
                    {
                        serviceRows = DispatchProxy.Create<IConversionJobRepository, FailingRepositoryProxy>();
                        var failure = (FailingRepositoryProxy)(object)serviceRows;
                        failure.Repository = rows;
                        failure.Method = nameof(IConversionJobRepository.SetFields);
                        failure.FailOnCall = 1;
                    }
                    var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), serviceRows, new UnusedConversionService(),
                        new ConversionTrackingService(new StubEventAggregator()), DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(),
                        CreateConfig(), LogManager.GetCurrentClassLogger(), historyService: history);
                    if (cleanup == "whole-download")
                    {
                        service.PrepareArtifactRemoval(request.DownloadId, ConversionRemovalOrigin.UserAction)();
                        Assert.That(historyReads, Is.EqualTo(1));
                    }
                    else if (cleanup == "immediate")
                    {
                        service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(), "bonus refused", discardOutput: true);
                    }
                    else
                    {
                        Assert.That(() => service.RetainCompletedOutput(request.DownloadId, request.Sources.Select(source => source.Path).ToArray(),
                            "bonus refused", discardOutput: true), Throws.Exception);
                        var pending = JsonSerializer.Deserialize<ConversionJobRequest>(rows.FindByDownloadId(request.DownloadId).RequestJson);
                        Assert.That(pending.DiscardRequested, Is.True);
                        Assert.That(pending.DiscardCurrentAttemptOnly, Is.True);
                        Assert.That(File.Exists(request.OutputPath), Is.True);
                        var restarted = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles(), rows, new UnusedConversionService(),
                            new ConversionTrackingService(new StubEventAggregator()), DispatchProxy.Create<IManageCommandQueue, RecordingCommandQueueProxy>(),
                            CreateConfig(), LogManager.GetCurrentClassLogger(), historyService: history);
                        typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                        var held = rows.FindByDownloadId(request.DownloadId);
                        Assert.That(held, Is.Not.Null, "Restart must preserve the saved cleanup backoff");
                        var retry = JsonSerializer.Deserialize<ConversionJobRequest>(held.RequestJson);
                        Assert.That(retry.CleanupError, Is.Not.Empty);
                        Assert.That(retry.CleanupNotBefore, Is.GreaterThan(DateTime.UtcNow));
                        Assert.That(File.Exists(request.OutputPath), Is.True);
                        retry.CleanupNotBefore = DateTime.UtcNow.AddSeconds(-1);
                        held.RequestJson = JsonSerializer.Serialize(retry);
                        rows.Update(held);
                        Assert.That(held.Status, Is.EqualTo(ConversionJobStatus.ReadyToImport), "The failed status write has not cancelled the job yet");
                        Assert.That(held.CleanupAttention, Is.True);
                        typeof(ConversionJobService).GetField("_attentionNotBefore", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(restarted, DateTime.MinValue);
                        typeof(ConversionJobService).GetMethod("ReviewCleanupAttention", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(restarted, null);
                    }
                    Assert.That(rows.FindByDownloadId(request.DownloadId), Is.Null);
                    Assert.That(Directory.Exists(request.WorkFolder), Is.False);
                    Assert.That(File.Exists(request.Sources.Single().Path), Is.True);
                    Assert.That(File.Exists(grabbedOutput), Is.EqualTo(cleanup != "whole-download"));
                    if (cleanup != "whole-download")
                    {
                        Assert.That(historyReads, Is.Zero, "A recording-scoped discard must not enumerate another format's history");
                        Assert.That(File.ReadAllText(grabbedOutput), Is.EqualTo("keep the grabbed format"));
                    }
                }
                finally
                {
                    ShutdownAndWait(worker);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            });
        }
    }
}
