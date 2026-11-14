using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
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
        private sealed class UnavailableImportedBook : LazyLoaded<Book>
        {
            public override void LazyLoad() => throw new InvalidOperationException("Imported book database read unavailable");
        }

        [TestCase(WriteAudioTagsType.Sync, false, true)]
        [TestCase(WriteAudioTagsType.Sync, false)]
        [TestCase(WriteAudioTagsType.Sync, true)]
        [TestCase(WriteAudioTagsType.NewFiles, false)]
        [TestCase(WriteAudioTagsType.No, false)]
        public void committed_import_preloads_series_once_on_the_books_actually_passed_to_tagging(WriteAudioTagsType mode, bool readFails, bool lazyFails = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "series-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var (profile, author, book, edition) = CreateAudiobookConversionGraph(386);
                profile.ConvertToQualityId = null;
                book.SeriesLinks = new List<SeriesBookLink>();
                book.Editions = new List<Edition> { edition };
                var media = new StubMediaFileService();
                var mover = new StubMoveBookFiles { DestinationForFile = file => Path.Combine(root, "library", Path.GetFileName(file.Path)), TransferFilesOnDisk = true };
                var detached = InterfaceProxy.Proxy<IMoveBookFiles>((method, args) =>
                {
                    var result = method.Invoke(mover, args);
                    if (result is BookFile file)
                    {
                        file.Edition = new Edition { Id = edition.Id, BookId = book.Id, Title = edition.Title,
                            Book = new Book { Id = book.Id, MediaType = BookMediaType.Audiobook, LazySeriesLinks = new LazyLoaded<List<SeriesBookLink>>() } };
                        if (lazyFails) file.Edition.LazyBook = new UnavailableImportedBook();
                    }
                    return result;
                });
                var reads = 0;
                var ids = new List<int>();
                var series = InterfaceProxy.Proxy<ISeriesBookLinkService>((_, args) =>
                {
                    reads++;
                    ids.AddRange((List<int>)args[0]);
                    if (readFails) throw new InvalidOperationException("Database unavailable");
                    return new List<SeriesBookLink> { new() { BookId = book.Id, Position = "2", Series = new Series { Title = "Series", GoodreadsSeriesId = "gr:1" } } };
                });
                var tagged = new List<Book>();
                var tags = InterfaceProxy.Proxy<IMetadataTagService>((_, args) =>
                {
                    if (!lazyFails) tagged.Add(((BookFile)args[0]).Edition.Book);
                    return null;
                });
                var config = InterfaceProxy.Proxy<IConfigService>((method, args) => method.Name == "get_WriteAudioTags" ? mode : method.Invoke(CreateConfigService(), args));
                var service = new ImportApprovedBooks(media, tags, new StubMediaInfoExtractor(),
                    Proxy<IAuthorService>(), CreateBookService(new List<Book> { book }).Service, CreateEditionService(book.Editions),
                    new StubRecycleBinProvider(), Proxy<IExtraService>(), detached, Proxy<IHistoryService>(), Proxy<NzbDrone.Core.Download.History.IDownloadHistoryService>(),
                    new NoOpEventAggregator(), Proxy<IManageCommandQueue>(), series, Proxy<ISeriesService>(),
                    Proxy<IQualityProfileService>(), Proxy<IM4bConversionService>(), LogManager.GetCurrentClassLogger(), configService: config);
                var decisions = Enumerable.Range(1, 50).Select(part =>
                {
                    var source = Path.Combine(root, $"{part}.mp3");
                    File.WriteAllText(source, "recording");
                    return new ImportDecision<LocalBook>(new LocalBook { Path = source, Author = author, Book = book, Edition = edition,
                        Part = part, PartCount = 50, Quality = new QualityModel(Quality.MP3), Size = new FileInfo(source).Length, Modified = File.GetLastWriteTimeUtc(source) });
                }).ToList();
                var results = service.Import(decisions, true, new DownloadClientItem { DownloadId = "series-import", DownloadForced = true }, ImportMode.Copy);
                Assert.That(results.Select(result => result.Result), Is.All.EqualTo(ImportResultType.Imported));
                if (lazyFails)
                {
                    Assert.That(reads, Is.Zero);
                    return;
                }
                Assert.That(tagged, Has.Count.EqualTo(50));
                Assert.That(tagged.Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(50), "Separate lazy Book instances exercise the actual post-commit preload");
                Assert.That(reads, Is.EqualTo(mode == WriteAudioTagsType.No ? 0 : 1));
                if (mode != WriteAudioTagsType.No)
                {
                    Assert.That(ids, Is.EqualTo(new[] { book.Id }));
                    Assert.That(tagged.All(item => item.LazySeriesLinks.IsLoaded), Is.EqualTo(!readFails),
                        "A failed preload must remain unloaded so the file writer can retry; it must not fabricate an empty catalog");
                    if (!readFails) Assert.That(tagged.Select(item => item.SeriesLinks.Count), Is.All.EqualTo(1));
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
