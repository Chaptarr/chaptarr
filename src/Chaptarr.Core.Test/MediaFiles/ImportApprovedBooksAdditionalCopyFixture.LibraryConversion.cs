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
        // A library conversion replaces exactly the files it was made from. A same-book eBook and a
        // part that was not converted stay, whether or not the profile allows upgrades: a profile
        // without upgrades must not turn the conversion into a second copy of the book.
        [TestCase(true)]
        [TestCase(false)]
        public void library_conversion_replaces_only_the_files_it_converted(bool upgradeAllowed)
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"library-conversion-{Guid.NewGuid():N}");
            var bookFolder = Path.Combine(root, "David Archer", "Black Sheep");
            var workFolder = Path.Combine(bookFolder, ".chaptarr-conversions", "library-edition-431", "attempt");
            Directory.CreateDirectory(workFolder);

            try
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(31);
                profile.UpgradeAllowed = upgradeAllowed;
                var ebookEdition = new Edition { Id = 9431, BookId = book.Id, Book = book, Title = book.Title, IsEbook = true, ReadingFormatId = 3 };
                book.Editions = new List<Edition> { edition, ebookEdition };

                var part1 = Path.Combine(bookFolder, "Black Sheep - 01.mp3");
                var part2 = Path.Combine(bookFolder, "Black Sheep - 02.mp3");
                var leftOut = Path.Combine(bookFolder, "Black Sheep - bonus.mp3");
                var ebook = Path.Combine(bookFolder, "Black Sheep.epub");
                foreach (var path in new[] { part1, part2, leftOut, ebook })
                {
                    File.WriteAllText(path, Path.GetFileName(path));
                }

                var output = Path.Combine(workFolder, "Black Sheep.m4b");
                File.WriteAllText(output, "converted m4b");

                // The receipt the worker writes after a successful encode; the import reads the
                // job key and sources from it because no download stands behind this file.
                File.WriteAllText(Path.Combine(workFolder, "conversion-artifact.json"), System.Text.Json.JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    DownloadId = LibraryConversionJob.Key(edition.Id),
                    EncoderSucceeded = true,
                    OutputSize = new FileInfo(output).Length,
                    OutputPath = output,
                    TargetQualityId = Quality.M4B.Id,
                    Sources = new[] { part1, part2 }.Select(path => new ConversionArtifactSource { Path = path, Size = new FileInfo(path).Length }).ToList()
                }));

                var mediaFileService = new StubMediaFileService();
                mediaFileService.FilesByBook.AddRange(new[]
                {
                    AudiobookFile(3101, part1, edition, 1, 2),
                    AudiobookFile(3102, part2, edition, 2, 2),
                    AudiobookFile(3103, leftOut, edition, 0, 0),
                    new BookFile
                    {
                        Id = 3104,
                        Path = ebook,
                        EditionId = ebookEdition.Id,
                        Edition = ebookEdition,
                        Quality = new QualityModel { Quality = Quality.EPUB, Revision = new Revision() }
                    }
                });

                var destination = Path.Combine(bookFolder, "Black Sheep.m4b");
                var recycleBin = new StubRecycleBinProvider();
                var service = new ImportApprovedBooks(
                    mediaFileService,
                    new StubMetadataTagService(),
                    new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(),
                    CreateBookService(new List<Book> { book }).Service,
                    CreateEditionService(new List<Edition> { edition, ebookEdition }),
                    recycleBin,
                    Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = destination, TransferFilesOnDisk = true },
                    Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(),
                    Proxy<ISeriesBookLinkService>(),
                    Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(),
                    Proxy<IM4bConversionService>(),
                    LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var request = new ConversionJobRequest
                {
                    Origin = ConversionJobOrigin.Library,
                    DownloadId = LibraryConversionJob.Key(edition.Id),
                    BookId = book.Id,
                    EditionId = edition.Id,
                    WorkRoot = Path.GetDirectoryName(workFolder),
                    WorkFolder = workFolder,
                    OutputPath = output,
                    Sources = new[] { part1, part2 }.Select(path => new ConversionArtifactSource { Path = path, Size = new FileInfo(path).Length }).ToList(),
                    TargetQualityId = Quality.M4B.Id,
                    TargetQualityName = Quality.M4B.Name,
                    TagOptions = new ConversionTagOptions()
                };

                var sources = new[] { (part1, 1), (part2, 2) }.Select(source => new ImportDecision<LocalBook>(new LocalBook
                {
                    Path = source.Item1,
                    Author = author,
                    Book = book,
                    Edition = edition,
                    Part = source.Item2,
                    PartCount = 2,
                    ExistingFile = true,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() },
                    Size = new FileInfo(source.Item1).Length,
                    Modified = File.GetLastWriteTimeUtc(source.Item1)
                })).ToList();

                var results = service.ImportLibraryConversion(request, sources);

                Assert.That(results.Select(result => result.Result), Is.EqualTo(new[] { ImportResultType.Imported }),
                    string.Join("; ", results.SelectMany(result => result.Errors)));
                Assert.That(mediaFileService.DeletedFiles.Select(deleted => deleted.File.Id), Is.EquivalentTo(new[] { 3101, 3102 }));
                Assert.That(mediaFileService.FilesByBook.Select(file => file.Id), Does.Contain(3103).And.Contain(3104));
                var converted = mediaFileService.FilesByBook.Single(file => file.Quality.Quality == Quality.M4B);
                Assert.That(converted.Path, Is.EqualTo(destination));
                Assert.That(converted.EditionId, Is.EqualTo(edition.Id), "a profile without upgrades must not create a separate copy");
                Assert.That(converted.MatchProvenance?.ImportDownloadId, Is.EqualTo(LibraryConversionJob.Key(edition.Id)),
                    "the job key on the file is the proof a restart uses to settle the job instead of encoding again");
                Assert.That(File.Exists(destination), Is.True);
                Assert.That(File.Exists(leftOut), Is.True);
                Assert.That(File.Exists(ebook), Is.True);
                Assert.That(recycleBin.DeletedFiles, Has.Count.EqualTo(2));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // A library conversion changes the format of files an edition already has. Unlike a download of
        // that edition, it must not select it: the book's monitored, pinned edition stays as it was.
        [Test]
        public void library_conversion_keeps_other_editions_monitoring_and_pins()
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"library-conversion-pins-{Guid.NewGuid():N}");
            var bookFolder = Path.Combine(root, "Jane Austen", "Persuasion");
            var workFolder = Path.Combine(bookFolder, ".chaptarr-conversions", "library-edition-433", "attempt");
            Directory.CreateDirectory(workFolder);

            try
            {
                var (_, author, book, edition) = CreateAudiobookConversionGraph(33);
                edition.Monitored = false;
                edition.ManualAdd = false;
                var pinned = new Edition { Id = 9433, BookId = book.Id, Book = book, Title = book.Title, Monitored = true, ManualAdd = true };
                book.Editions = new List<Edition> { edition, pinned };

                var part1 = Path.Combine(bookFolder, "Persuasion - 01.mp3");
                var part2 = Path.Combine(bookFolder, "Persuasion - 02.mp3");
                File.WriteAllText(part1, "part one");
                File.WriteAllText(part2, "part two");
                var output = Path.Combine(workFolder, "Persuasion.m4b");
                File.WriteAllText(output, "converted m4b");
                var sources = new[] { part1, part2 }.Select(path => new ConversionArtifactSource { Path = path, Size = new FileInfo(path).Length }).ToList();
                File.WriteAllText(Path.Combine(workFolder, "conversion-artifact.json"), System.Text.Json.JsonSerializer.Serialize(new ConversionArtifactManifest
                {
                    DownloadId = LibraryConversionJob.Key(edition.Id),
                    EncoderSucceeded = true,
                    OutputSize = new FileInfo(output).Length,
                    OutputPath = output,
                    TargetQualityId = Quality.M4B.Id,
                    Sources = sources
                }));

                var mediaFileService = new StubMediaFileService();
                mediaFileService.FilesByBook.AddRange(new[] { AudiobookFile(3301, part1, edition, 1, 2), AudiobookFile(3302, part2, edition, 2, 2) });
                var editionService = CreateEditionService(book.Editions);
                var service = new ImportApprovedBooks(
                    mediaFileService, new StubMetadataTagService(), new StubMediaInfoExtractor(), Proxy<IAuthorService>(),
                    CreateBookService(new List<Book> { book }).Service, editionService, new StubRecycleBinProvider(), Proxy<IExtraService>(),
                    new StubMoveBookFiles { DestinationPath = Path.Combine(bookFolder, "Persuasion.m4b"), TransferFilesOnDisk = true },
                    Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(),
                    Proxy<IM4bConversionService>(), LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

                var request = new ConversionJobRequest
                {
                    Origin = ConversionJobOrigin.Library,
                    DownloadId = LibraryConversionJob.Key(edition.Id),
                    BookId = book.Id,
                    EditionId = edition.Id,
                    WorkRoot = Path.GetDirectoryName(workFolder),
                    WorkFolder = workFolder,
                    OutputPath = output,
                    Sources = sources,
                    TargetQualityId = Quality.M4B.Id,
                    TargetQualityName = Quality.M4B.Name,
                    TagOptions = new ConversionTagOptions()
                };
                var decisions = new[] { (part1, 1), (part2, 2) }.Select(source => new ImportDecision<LocalBook>(new LocalBook
                {
                    Path = source.Item1, Author = author, Book = book, Edition = edition, Part = source.Item2, PartCount = 2, ExistingFile = true,
                    Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() },
                    Size = new FileInfo(source.Item1).Length, Modified = File.GetLastWriteTimeUtc(source.Item1)
                })).ToList();

                var results = service.ImportLibraryConversion(request, decisions);

                Assert.That(results.Select(result => result.Result), Is.EqualTo(new[] { ImportResultType.Imported }),
                    string.Join("; ", results.SelectMany(result => result.Errors)));
                Assert.That(((EditionServiceProxy)(object)editionService).MonitoredEditionIds, Is.Empty, "the converted edition is not selected");
                Assert.That((pinned.Monitored, pinned.ManualAdd), Is.EqualTo((true, true)), "the pinned edition keeps its monitoring and pin");
                Assert.That((edition.Monitored, edition.ManualAdd), Is.EqualTo((false, false)), "the converted edition stays unselected");
                Assert.That(mediaFileService.FilesByBook.Single(file => file.Quality.Quality == Quality.M4B).EditionId, Is.EqualTo(edition.Id));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void library_conversion_import_refuses_when_the_source_files_differ_from_the_job()
        {
            var (_, author, book, edition) = CreateAudiobookConversionGraph(32);
            var mediaFileService = new StubMediaFileService();
            var service = new ImportApprovedBooks(
                mediaFileService,
                new StubMetadataTagService(),
                new StubMediaInfoExtractor(),
                Proxy<IAuthorService>(),
                CreateBookService(new List<Book> { book }).Service,
                CreateEditionService(new List<Edition> { edition }),
                new StubRecycleBinProvider(),
                Proxy<IExtraService>(),
                new StubMoveBookFiles(),
                Proxy<IHistoryService>(),
                Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                new NoOpEventAggregator(),
                Proxy<IManageCommandQueue>(),
                Proxy<ISeriesBookLinkService>(),
                Proxy<ISeriesService>(),
                Proxy<IQualityProfileService>(),
                Proxy<IM4bConversionService>(),
                LogManager.GetLogger("ImportApprovedBooksAdditionalCopyFixture"));

            var request = new ConversionJobRequest
            {
                OutputPath = "/audiobooks/David Archer/Black Sheep/.chaptarr-conversions/library-edition-432/a/Black Sheep.m4b",
                Sources = new List<ConversionArtifactSource> { new() { Path = "/audiobooks/David Archer/Black Sheep/01.mp3" }, new() { Path = "/audiobooks/David Archer/Black Sheep/02.mp3" } },
                TargetQualityId = Quality.M4B.Id
            };
            var onlyOnePart = new List<ImportDecision<LocalBook>>
            {
                new(new LocalBook { Path = "/audiobooks/David Archer/Black Sheep/01.mp3", Author = author, Book = book, Edition = edition })
            };

            var results = service.ImportLibraryConversion(request, onlyOnePart);

            Assert.That(results.Single().Result, Is.Not.EqualTo(ImportResultType.Imported));
            Assert.That(results.Single().Errors.Single(), Does.Contain("changed since this conversion was queued"));
            Assert.That(mediaFileService.DeletedFiles, Is.Empty);
        }

        private static BookFile AudiobookFile(int id, string path, Edition edition, int part, int partCount) => new()
        {
            Id = id,
            Path = path,
            EditionId = edition.Id,
            Edition = edition,
            Part = part,
            PartCount = partCount,
            Size = new FileInfo(path).Length,
            Quality = new QualityModel { Quality = Quality.MP3, Revision = new Revision() }
        };
    }
}
