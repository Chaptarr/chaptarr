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
        [Test]
        public void hand_picked_grab_must_preserve_both_recordings_with_upgrades_off()
        {
            var scratch = Path.Combine(Path.GetTempPath(), "issue-286-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(286);
                profile.UpgradeAllowed = false;
                profile.ConvertToQualityId = null;
                author.Name = "Pierce Brown";
                book.Title = "Dark Age";
                edition.Title = "Dark Age";
                var graphicEdition = new Edition { Id = edition.Id + 1, BookId = book.Id, Book = book, Title = "Dark Age", ReadingFormatId = 2 };
                book.Editions = new List<Edition> { edition, graphicEdition };
                var folder = Path.Combine(scratch, "Dark Age - Full Cast");
                Directory.CreateDirectory(folder);
                var regular = Path.Combine(folder, "Dark Age - Part 1.m4b");
                var graphic = Path.Combine(folder, "Dark Age.m4b");
                var source = Path.Combine(scratch, "incoming.m4b");
                File.WriteAllText(regular, "regular recording");
                File.WriteAllText(graphic, "GraphicAudio recording");
                File.WriteAllText(source, "new recording");
                var media = new StubMediaFileService();
                media.Add(new BookFile { Id = 1, EditionId = edition.Id, Edition = edition, Path = regular, Quality = new QualityModel(Quality.M4B) });
                media.Add(new BookFile { Id = 2, EditionId = graphicEdition.Id, Edition = graphicEdition, Path = graphic, Quality = new QualityModel(Quality.M4B), IsGraphicAudio = false });
                var recycle = new StubRecycleBinProvider();
                var mover = new StubMoveBookFiles { DestinationPath = graphic, TransferFilesOnDisk = true };
                var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), Proxy<IBookService>(), CreateEditionService(book.Editions),
                    recycle, Proxy<IExtraService>(), mover, Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), LogManager.GetCurrentClassLogger());
                var local = new LocalBook { Path = source, Author = author, Book = book, Edition = edition,
                    Quality = new QualityModel(Quality.M4B),
                    Size = new FileInfo(source).Length, Modified = File.GetLastWriteTimeUtc(source) };

                var result = service.Import(new List<ImportDecision<LocalBook>> { new(local) }, true,
                    new DownloadClientItem { DownloadId = "interactive-grab", DownloadForced = true }, ImportMode.Copy).Single();

                Assert.That(result.Result, Is.EqualTo(ImportResultType.Skipped));
                Assert.That(result.Errors.Single(), Does.Contain("destination is already occupied"));
                Assert.That(File.ReadAllText(regular), Is.EqualTo("regular recording"));
                Assert.That(File.ReadAllText(graphic), Is.EqualTo("GraphicAudio recording"));
                Assert.That(File.ReadAllText(source), Is.EqualTo("new recording"));
                Assert.That(media.FilesByBook.Select(file => file.Id), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(media.DeletedFiles, Is.Empty);
                Assert.That(media.ReplaceCalls, Is.Zero);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.Zero);
                Assert.That(recycle.DeletedFiles, Is.Empty);
            }
            finally
            {
                Directory.Delete(scratch, true);
            }
        }
    }
}
