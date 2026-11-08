using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(false, false, false, false, false, false, ImportResultType.Imported)]
        [TestCase(false, true, false, false, false, false, ImportResultType.WaitingForConfirmation)]
        [TestCase(true, true, false, false, false, false, ImportResultType.Imported)]
        [TestCase(true, true, false, true, false, false, ImportResultType.Imported)]
        [TestCase(false, false, false, true, false, false, ImportResultType.Skipped)]
        [TestCase(true, false, false, false, true, false, ImportResultType.Skipped)]
        [TestCase(false, false, true, false, false, false, ImportResultType.WaitingForConfirmation)]
        [TestCase(false, false, true, true, false, false, ImportResultType.WaitingForConfirmation)]
        [TestCase(false, false, true, false, true, false, ImportResultType.WaitingForConfirmation)]
        [TestCase(true, false, true, false, false, false, ImportResultType.Imported)]
        [TestCase(true, false, false, false, false, true, ImportResultType.Skipped)]
        [TestCase(false, false, false, true, false, false, ImportResultType.Imported, 1000)]
        [TestCase(false, true, false, true, false, false, ImportResultType.WaitingForConfirmation, 1000)]
        [TestCase(true, false, false, true, false, false, ImportResultType.Skipped, -100)]
        [TestCase(false, false, true, false, false, false, ImportResultType.WaitingForConfirmation, 0, true, 0)]
        [TestCase(false, false, true, false, false, false, ImportResultType.Imported, 0, true, 3)]
        [TestCase(false, false, true, false, false, false, ImportResultType.WaitingForConfirmation, 0, true, 1)]
        [TestCase(false, false, false, false, false, false, ImportResultType.Imported, 0, true, 0)]
        public void replacement_policy_is_decided_before_file_transfer(bool handPicked, bool wait, bool differentRecording,
            bool equal, bool worse, bool missingProfile, ImportResultType expected, int incomingScore = 0, bool productionEdition = false, int matchingCredits = 0)
        {
            var root = Path.Combine(Path.GetTempPath(), "upgrade-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(386);
                profile.UpgradeAllowed = true;
                profile.ConfirmAutomaticUpgrades = wait;
                profile.ConvertToQualityId = null;
                var oldFormat = new CustomFormat { Id = 1, Name = "Existing format" };
                var newFormat = new CustomFormat { Id = 2, Name = "Incoming format" };
                profile.FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = oldFormat, Score = incomingScore == 0 ? 0 : 100 },
                    new() { Format = newFormat, Score = incomingScore }
                };
                var formats = InterfaceProxy.Proxy<ICustomFormatCalculationService>((_, args) =>
                    new List<CustomFormat> { args[0] is LocalBook ? newFormat : oldFormat });
                edition.Monitored = true;
                edition.IsGraphicAudio = productionEdition;
                edition.NarratorNames = new List<string> { "Tim Gerard Reynolds", "John Curless", "Moira Quirk" };
                var oldTags = matchingCredits > 0 ? new Dictionary<string, List<string>>
                {
                    ["NARRATOR"] = edition.NarratorNames,
                    ["PRODUCTION"] = new() { "GraphicAudio" }
                } : null;
                book.Editions = new List<Edition> { edition };
                if (missingProfile) author.AudiobookQualityProfile = null;
                var source = Path.Combine(root, "incoming.m4b");
                // #286: no production tag or stored flag; original filename is the available evidence.
                var old = Path.Combine(root, differentRecording ? "Dark Age - GraphicAudio.mp3" : "old.mp3");
                File.WriteAllText(source, "new recording");
                File.WriteAllText(old, "old recording");
                var media = new StubMediaFileService();
                media.Add(new BookFile { Id = 10, Path = old, EditionId = edition.Id, Edition = edition,
                    Quality = new QualityModel(equal || worse ? Quality.M4B : Quality.MP3), AllTags = oldTags });
                var mover = new StubMoveBookFiles { DestinationPath = Path.Combine(root, "library.m4b"), TransferFilesOnDisk = true };
                var recycle = new StubRecycleBinProvider();
                var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), CreateBookService(new List<Book> { book }).Service, CreateEditionService(book.Editions),
                    recycle, Proxy<IExtraService>(), mover, Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(), Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), LogManager.GetCurrentClassLogger(), configService: CreateConfigService(), customFormatCalculationService: formats);
                var local = new LocalBook { Path = source, Author = author, Book = book, Edition = edition,
                    Quality = new QualityModel(worse ? Quality.MP3 : Quality.M4B), DurationSeconds = 500,
                    Size = new FileInfo(source).Length, Modified = File.GetLastWriteTimeUtc(source),
                    RawTags = matchingCredits > 0 ? new RawFileTags { AllTags = new Dictionary<string, List<string>>
                    {
                        ["NARRATOR"] = edition.NarratorNames.Take(matchingCredits).ToList()
                    } } : null };
                var result = service.Import(new() { new(local) }, true,
                    new DownloadClientItem { DownloadId = "policy", DownloadForced = handPicked }, ImportMode.Copy).Single();
                Assert.That(result.Result, Is.EqualTo(expected), string.Join("; ", result.Errors));
                if (expected == ImportResultType.Skipped && !missingProfile)
                {
                    Assert.That(result.Errors, Has.Some.Contains($"Existing: {media.FilesByBook.Single().Quality} (score {(incomingScore == 0 ? 0 : 100)})"));
                    Assert.That(result.Errors, Has.Some.Contains($"New: {local.Quality} (score {incomingScore})"));
                }

                if (differentRecording && !handPicked && expected == ImportResultType.WaitingForConfirmation)
                {
                    Assert.That(result.Errors, Has.Some.StartsWith("The recording type differs."));
                }

                if (expected == ImportResultType.Imported)
                {
                    Assert.That(media.DeletedFiles.Select(file => file.File.Id), Is.EqualTo(new[] { 10 }));
                    Assert.That(media.FilesByBook.Single().IsGraphicAudio, Is.False,
                        "Edition metadata and narrator agreement must not become observed production evidence on the imported file");
                    Assert.That(media.FilesByBook.Single().AudioProductionType, Is.Null);
                    Assert.That(File.ReadAllText(mover.DestinationPath), Is.EqualTo("new recording"));
                }
                else
                {
                    Assert.That(mover.CopyCalls, Is.Zero);
                    Assert.That(media.DeletedFiles, Is.Empty);
                    Assert.That(File.ReadAllText(old), Is.EqualTo("old recording"));
                    Assert.That(recycle.DeletedFiles, Is.Empty);
                }
            }
            finally { Directory.Delete(root, true); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void automatic_ebook_upgrade_obeys_the_same_wait_choice(bool wait)
        {
            var (profile, author, book, edition) = CreateAudiobookConversionGraph(387);
            profile.UpgradeAllowed = true;
            profile.ConfirmAutomaticUpgrades = wait;
            profile.ProfileType = ProfileType.Ebook;
            profile.Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.MOBI, Allowed = true }, new() { Quality = Quality.EPUB, Allowed = true } };
            author.EbookQualityProfile = profile;
            author.EbookQualityProfileId = profile.Id;
            book.MediaType = BookMediaType.Ebook;
            var oldPath = Path.GetTempFileName();
            try
            {
                var incoming = new LocalBook { Path = "/download/new.epub", Book = book, Edition = edition, Author = author, Quality = new QualityModel(Quality.EPUB) };
                var old = new BookFile { Id = 1, Path = oldPath, EditionId = edition.Id, Edition = edition, Quality = new QualityModel(Quality.MOBI) };
                var probes = 0;
                var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IDiskProvider.FileExists)));
                    probes++;
                    return File.Exists((string)args[0]);
                });
                using var logs = new LogFactory();
                var sink = new NLog.Targets.MemoryTarget { Layout = "${message}" };
                var logging = new NLog.Config.LoggingConfiguration();
                logging.AddRuleForAllLevels(sink);
                logs.Configuration = logging;
                var service = new ImportApprovedBooks(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                    logs.GetLogger("replacement"), configService: CreateConfigService(), diskProvider: disk);
                var method = typeof(ImportApprovedBooks).GetMethod("CheckReplacement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var decision = ((ImportResultType Result, string Reason))method.Invoke(service, new object[] { new[] { incoming, incoming }, new[] { old }, profile, false,
                    new Dictionary<LocalBook, List<CustomFormat>> { [incoming] = new List<CustomFormat>() }, null });
                Assert.That(decision.Result, Is.EqualTo(wait ? ImportResultType.WaitingForConfirmation : ImportResultType.Imported));
                Assert.That(probes, Is.EqualTo(1), "Probe the old file once per group, not once per incoming part");
                Assert.That(sink.Logs.Count(line => line.Contains("[REPLACEMENT-CHECK] 2 incoming, 1 existing files, 2 comparisons in")), Is.EqualTo(1));
            }
            finally { File.Delete(oldPath); }
        }
    }
}
