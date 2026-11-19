using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Download;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [TestCase(BookMediaType.Audiobook, false, false)]
        [TestCase(BookMediaType.Audiobook, true, false)]
        [TestCase(BookMediaType.Audiobook, false, true)]
        [TestCase(BookMediaType.Ebook, false, false)]
        [TestCase(BookMediaType.Ebook, true, false)]
        [TestCase(BookMediaType.Ebook, false, true)]
        public void real_quality_gate_preserves_refused_files_and_existing_audio_completeness(
            BookMediaType mediaType, bool forced, bool mixed)
        {
            var root = Path.Combine(Path.GetTempPath(), "quality-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph();
                var allowed = mediaType == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB;
                var refused = mediaType == BookMediaType.Audiobook ? Quality.MP3 : Quality.MOBI;
                profile.ConvertToQualityId = null;
                profile.Name = "Wanted format";
                profile.ProfileType = mediaType == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook;
                profile.Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = refused, Allowed = false },
                    new() { Quality = allowed, Allowed = true }
                };
                author.Id = 7;
                author.EbookQualityProfile = profile;
                author.EbookQualityProfileId = profile.Id;
                book.Id = 901;
                book.AuthorId = author.Id;
                book.MediaType = mediaType;
                edition.BookId = book.Id;
                edition.ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3;
                edition.Monitored = true;
                book.Editions = new List<Edition> { edition };
                var media = new StubMediaFileService();
                var mover = new StubMoveBookFiles { DestinationPath = Path.Combine(root, "imported.book"), TransferFilesOnDisk = true };
                var service = new ImportApprovedBooks(media, new StubMetadataTagService(), new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), CreateBookService(new List<Book> { book }).Service, CreateEditionService(book.Editions),
                    new StubRecycleBinProvider(), Proxy<IExtraService>(), mover, Proxy<IHistoryService>(),
                    Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(), new NoOpEventAggregator(),
                    Proxy<IManageCommandQueue>(), Proxy<ISeriesBookLinkService>(), Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), LogManager.GetCurrentClassLogger(),
                    configService: CreateConfigService());
                var decisions = (mixed ? new[] { refused, allowed } : new[] { refused }).Select(quality =>
                {
                    var path = Path.Combine(root, "download." + quality.Name.ToLowerInvariant());
                    File.WriteAllText(path, quality.Name);
                    return new ImportDecision<LocalBook>(new LocalBook
                    {
                        Path = path, Author = author, Book = book, Edition = edition,
                        Quality = new QualityModel(quality), DurationSeconds = 500,
                        Size = new FileInfo(path).Length, Modified = File.GetLastWriteTimeUtc(path)
                    });
                }).ToList();
                List<ImportResult> results = null;
                var harness = new CompletedDownloadServiceQualityProfileFixture.Harness(client =>
                    results = service.Import(decisions, true, client, ImportMode.Copy));
                var tracked = harness.TrackedDownload;
                tracked.RemoteBook.Author = author;
                tracked.RemoteBook.Books = new List<Book> { book };
                tracked.RemoteBook.ParsedBookInfo.Quality = new QualityModel(allowed);
                tracked.DownloadItem.DownloadForced = forced;

                harness.Subject.Import(tracked);

                Assert.That(results, Has.Count.EqualTo(mixed ? 2 : 1));
                Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadFailedEvent>());
                Assert.That(decisions.All(decision => File.Exists(decision.Item.Path)), Is.True);
                if (forced || mixed)
                {
                    var complete = !mixed || mediaType == BookMediaType.Ebook;
                    Assert.That(tracked.State, Is.EqualTo(complete ? TrackedDownloadState.Imported : TrackedDownloadState.ImportBlocked));
                    Assert.That(results.Count(result => result.Result == ImportResultType.Imported), Is.EqualTo(1));
                    Assert.That(mover.CopyCalls, Is.EqualTo(1));
                    Assert.That(File.ReadAllText(mover.DestinationPath), Is.EqualTo((forced ? refused : allowed).Name));
                    Assert.That(harness.EventAggregator.Events.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(complete ? 1 : 0));
                    Assert.That(harness.EventAggregator.Events.OfType<BookImportIncompleteEvent>().Count(), Is.EqualTo(complete ? 0 : 1));
                    if (!complete)
                    {
                        Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                            Has.Some.Contains("not allowed by profile"));
                        Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                            Has.None.Contains("but it only contains"));
                    }
                }
                else
                {
                    Assert.That(results.Single().ImportDecision.Rejections.Single().IsQualityFilter, Is.True);
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                    Assert.That(mover.CopyCalls, Is.Zero);
                    Assert.That(harness.EventAggregator.Events, Has.One.InstanceOf<BookImportIncompleteEvent>());
                    Assert.That(harness.EventAggregator.Events, Has.None.InstanceOf<DownloadCompletedEvent>());
                    var message = (string)typeof(QueueService).GetMethod("GetQueueErrorMessage", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, new object[] { tracked });
                    Assert.That(message, Does.Contain("Grabbed").And.Contain(refused.Name).And.Contain("Manual Import"));
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
