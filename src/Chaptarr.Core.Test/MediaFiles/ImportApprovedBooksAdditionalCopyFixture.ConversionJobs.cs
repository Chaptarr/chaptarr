using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using System.Threading;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        private static readonly AsyncLocal<List<(string Directory, ConversionJobService Service)>> WorkerJobFixtures = new();

        private static ConversionJobService CreateConversionJobs(IM4bConversionService converter,
            IConversionTrackingService tracking = null, IConfigService config = null, IMediaFileService media = null, NzbDrone.Core.Datastore.IMainDatabase database = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "import-conversion-jobs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (NzbDrone.Core.Datastore.TableMapping.Mapper.TableMap.Count == 0) NzbDrone.Core.Datastore.TableMapping.Map();
            var service = new ConversionJobService(database == null ? ConversionJobServiceFixture.CreateRepository(Path.Combine(directory, "jobs.db")) : new ConversionJobRepository(database, new NoOpEventAggregator()), converter,
                tracking ?? new ConversionTrackingService(new NoOpEventAggregator()), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IManageCommandQueue>((method, args) => null),
                config ?? CreateConfigService(), LogManager.GetCurrentClassLogger(), mediaFileService: media);
            (WorkerJobFixtures.Value ??= new()).Add((directory, service));
            return service;
        }

        private static List<ImportResult> ImportWithConversionWorker(ImportApprovedBooks importer,
            List<ImportDecision<LocalBook>> decisions, bool replaceExisting,
            DownloadClientItem downloadClientItem, ImportMode importMode, CancellationToken cancellationToken, ConversionJobService worker = null)
        {
            var result = importer.Import(decisions, replaceExisting, downloadClientItem, importMode, cancellationToken);
            if (!result.Any(item => item.Result == ImportResultType.Pending)) return result;
            foreach (var fixture in WorkerJobFixtures.Value ?? new())
            {
                if (worker != null && fixture.Service != worker) continue;
                if (fixture.Service.Get(downloadClientItem.DownloadId)?.Status != ConversionJobStatus.Queued) continue;
                typeof(ConversionJobService).GetMethod("DispatchQueuedJobs", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(fixture.Service, null);
                Assert.That(SpinWait.SpinUntil(() => !fixture.Service.IsRunning(downloadClientItem.DownloadId), TimeSpan.FromSeconds(15)), Is.True);
            }
            return importer.Import(decisions, replaceExisting, downloadClientItem, importMode, cancellationToken);
        }

        [TearDown]
        public void CleanupConversionJobFixtures()
        {
            foreach (var fixture in WorkerJobFixtures.Value ?? new())
            {
                try { fixture.Service.Handle(new ApplicationShutdownRequested()); }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    if (Directory.Exists(fixture.Directory)) Directory.Delete(fixture.Directory, true);
                }
            }
            WorkerJobFixtures.Value = null;
        }
    }
}
