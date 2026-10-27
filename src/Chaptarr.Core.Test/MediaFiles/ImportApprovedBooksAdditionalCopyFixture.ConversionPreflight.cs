using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(1, false)]
        [TestCase(3, false)]
        [TestCase(1, true)]
        [TestCase(3, true)]
        public void converted_destination_and_workspace_name_use_generated_part_shape(int sourceCount, bool occupied)
        {
            var root = Path.Combine(Path.GetTempPath(), "conversion-real-destination-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(441);
                var library = Path.Combine(root, "library");
                Directory.CreateDirectory(library);
                var destination = Path.Combine(library, "recording.m4b");
                if (occupied) File.WriteAllText(destination, "existing recording");
                var paths = Enumerable.Range(1, sourceCount).Select(index => Path.Combine(root, "source-" + index + ".mp3")).ToArray();
                foreach (var path in paths) File.WriteAllText(path, "constructed source");
                var converter = new FailingM4bConversionService();
                var mover = new StubMoveBookFiles
                {
                    DestinationForFile = local => Path.Combine(library, local.PartCount > 1 ? "recording-02.m4b" : "recording.m4b")
                };
                var service = new ImportApprovedBooks(new StubMediaFileService(), new StubMetadataTagService(), new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), Proxy<IBookService>(), CreateEditionService(new List<Edition> { edition }),
                    Proxy<IRecycleBinProvider>(), Proxy<IExtraService>(), mover, Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), new NoOpEventAggregator(), Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(), converter, LogManager.GetCurrentClassLogger(),
                    conversionJobService: CreateConversionJobs(converter));
                var decisions = paths.Select((path, index) => new ImportDecision<LocalBook>(new LocalBook
                {
                    Path = path, Author = author, Book = book, Edition = edition,
                    Quality = new QualityModel(Quality.MP3), Part = index + 2, PartCount = sourceCount + 2, IsManualImport = false
                })).ToList();
                var results = ImportWithConversionWorker(service, decisions, false, new DownloadClientItem { DownloadId = Guid.NewGuid().ToString("N") }, ImportMode.Move, CancellationToken.None);
                Assert.That(results.All(result => result.Result == ImportResultType.Skipped), Is.True);
                Assert.That(converter.ConvertCalls, Is.EqualTo(occupied ? 0 : 1));
                if (occupied)
                {
                    Assert.That(results.SelectMany(result => result.Errors), Has.Some.Contains(destination));
                    Assert.That(File.ReadAllText(destination), Is.EqualTo("existing recording"));
                }
                else Assert.That(Path.GetFileName(converter.LastOutputFile), Is.EqualTo("recording.m4b"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
