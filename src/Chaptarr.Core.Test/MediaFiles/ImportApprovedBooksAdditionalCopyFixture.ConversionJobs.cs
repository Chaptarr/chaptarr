using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using System.Threading;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
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
            if (media == null)
            {
                if (database == null) media = Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoLibraryMediaFiles();
                else
                {
                    var files = new MediaFileRepository(database, new NoOpEventAggregator());
                    media = Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name switch
                    {
                        nameof(IMediaFileService.GetFilePathOwnership) => files.GetFilePathOwnership((IReadOnlyCollection<int>)args[0], (IReadOnlyCollection<string>)args[1]),
                        nameof(IMediaFileService.GetFilesByBook) => files.GetFilesByBook((int)args[0]),
                        nameof(IMediaFileService.GetFileWithPath) => files.GetFileWithPath((string)args[0]),
                        _ => throw new NotImplementedException(method.Name)
                    });
                }
            }
            var service = new ConversionJobService(Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestCleanupDisk(), Chaptarr.Core.Test.MediaFiles.ConversionJobServiceFixture.TestRootFolders(), media, database == null ? ConversionJobServiceFixture.CreateRepository(Path.Combine(directory, "jobs.db")) : new ConversionJobRepository(database, new NoOpEventAggregator()), converter,
                tracking ?? new ConversionTrackingService(new NoOpEventAggregator()), Chaptarr.Core.Test.Common.InterfaceProxy.Proxy<IManageCommandQueue>((method, args) => null),
                config ?? CreateConfigService(), LogManager.GetCurrentClassLogger());
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
                Assert.That(ConversionJobServiceFixture.DispatchWorkers(fixture.Service).Wait(TimeSpan.FromSeconds(15)), Is.True);
            }
            return importer.Import(decisions, replaceExisting, downloadClientItem, importMode, cancellationToken);
        }

        [TearDown]
        public void CleanupConversionJobFixtures()
        {
            var errors = new List<Exception>();
            try
            {
                foreach (var fixture in WorkerJobFixtures.Value ?? new())
                {
                    try
                    {
                        ConversionJobServiceFixture.ShutdownAndWait(fixture.Service);
                        SqliteConnection.ClearAllPools();
                        if (Directory.Exists(fixture.Directory)) Directory.Delete(fixture.Directory, true);
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            }
            finally { WorkerJobFixtures.Value = null; }
            if (errors.Count > 0) throw new AggregateException("Conversion fixture cleanup failed", errors);
        }
    }
}
