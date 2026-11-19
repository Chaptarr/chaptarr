using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
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
        [TestCase(null, ImportResultType.Imported)]
        [TestCase("Full Cast", ImportResultType.WaitingForConfirmation)]
        [TestCase("Dramatized", ImportResultType.WaitingForConfirmation)]
        public void edition_production_is_not_stored_as_file_evidence_or_used_by_the_next_upgrade(string nextProduction, ImportResultType expected)
        {
            RunRecordingImports(null, nextProduction, expected);
        }

        [Test]
        public void importing_cast_only_tags_stores_full_cast_without_claiming_dramatization()
        {
            RunRecordingImports("Full Cast", null, null);
        }

        private static void RunRecordingImports(string firstProduction, string nextProduction, ImportResultType? expected)
        {
            var root = Directory.CreateTempSubdirectory("chaptarr-recording-chain-").FullName;
            try
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(387);
                profile.UpgradeAllowed = true;
                profile.ConvertToQualityId = null;
                edition.AudioProductionType = "Dramatized / Full-Cast";
                edition.IsGraphicAudio = true;
                edition.Monitored = true;
                book.Editions = new List<Edition> { edition };
                var media = new StubMediaFileService();
                var mover = new StubMoveBookFiles { DestinationForFile = local => Path.Combine(root, "library-" + Path.GetFileName(local.Path)), TransferFilesOnDisk = true };
                var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), CreateBookService(new List<Book> { book }).Service, CreateEditionService(book.Editions),
                    new StubRecycleBinProvider(), Proxy<IExtraService>(), mover, Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(), Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), LogManager.GetCurrentClassLogger(), configService: CreateConfigService());
                ImportResult Import(string filename, Quality quality, string production)
                {
                    var path = Path.Combine(root, filename);
                    File.WriteAllText(path, filename);
                    var local = new LocalBook { Path = path, Author = author, Book = book, Edition = edition,
                        Quality = new QualityModel(quality), Size = new FileInfo(path).Length,
                        DurationSeconds = 500, Modified = File.GetLastWriteTimeUtc(path),
                        RawTags = new RawFileTags { AllTags = production == null ? new() : new() { ["PRODUCTION"] = new() { production } } } };
                    return service.Import(new() { new(local) }, true,
                        new DownloadClientItem { DownloadId = filename }, ImportMode.Copy).Single();
                }

                var first = Import("first.mp3", Quality.MP3, firstProduction);
                Assert.That(first.Result, Is.EqualTo(ImportResultType.Imported), string.Join("; ", first.Errors));
                var saved = media.FilesByBook.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(saved.IsGraphicAudio, Is.EqualTo(firstProduction != null), "The legacy flag records observed production, not catalog-only production or a publisher identity");
                    Assert.That(saved.AudioProductionType, Is.EqualTo(firstProduction));
                });
                if (expected.HasValue)
                {
                    var next = Import("upgrade.m4b", Quality.M4B, nextProduction);
                    Assert.That(next.Result, Is.EqualTo(expected.Value), string.Join("; ", next.Errors));
                    if (expected == ImportResultType.WaitingForConfirmation)
                    {
                        Assert.That(media.FilesByBook.Single(), Is.SameAs(saved));
                        Assert.That(media.DeletedFiles, Is.Empty);
                        Assert.That(File.ReadAllText(saved.Path), Is.EqualTo("first.mp3"));
                        Assert.That(next.Errors, Has.Some.StartsWith("The recording type differs."));
                    }
                    else
                    {
                        Assert.That(media.DeletedFiles.Select(file => file.File.Id), Is.EqualTo(new[] { saved.Id }));
                        Assert.That(File.ReadAllText(media.FilesByBook.Single().Path), Is.EqualTo("upgrade.m4b"));
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
