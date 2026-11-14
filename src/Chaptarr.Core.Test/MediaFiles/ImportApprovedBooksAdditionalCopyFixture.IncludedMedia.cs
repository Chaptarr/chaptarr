using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Download;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    public partial class ImportApprovedBooksAdditionalCopyFixture
    {
        [Test]
        public void linked_bonus_grab_does_not_change_recovered_download_targets()
        {
            var original = new EntityHistory { Id = 1, BookId = 10, EditionId = 100, EventType = EntityHistoryEventType.Grabbed,
                Date = DateTime.UtcNow.AddMinutes(-5) };
            var included = new EntityHistory { Id = 2, BookId = 20, EditionId = 200, EventType = EntityHistoryEventType.Grabbed,
                Date = DateTime.UtcNow, Data = new() { ["IncludedFromGrabId"] = "1" } };
            var remote = new RemoteBook { Books = new() { new Book { Id = 10 }, new Book { Id = 20 } } };
            remote.RestoreGrabbedTargets(new[] { included, original });
            Assert.That(remote.TargetEditionIds, Is.EquivalentTo(new Dictionary<int, int> { [10] = 100 }));
            Assert.That(RemoteBook.GetCapturedGrabHistory(new[] { included, original }), Is.EqualTo(new[] { original }));
            included.Data.Clear();
            remote.RestoreGrabbedTargets(new[] { included, original });
            Assert.That(remote.TargetEditionIds.Keys, Is.EquivalentTo(new[] { 10, 20 }));
        }

        [Test]
        public void included_media_uses_its_own_wanted_row_profile_and_original_download(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested,
            [Values("both", "paused", "unmonitored", "owned", "disallowed", "pin", "wrong-only", "requested-only", "unmatched", "copy-failure", "conversion", "no-root", "no-profile", "other-author", "other-work", "locked", "missing") ] string scenario)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var root = Path.Combine(Path.GetTempPath(), "included-media-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try { RunIncludedMedia(db, root, requested, scenario); }
                finally { Directory.Delete(root, true); }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void included_recording_with_one_refused_part_imports_no_parts(bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "multipart-quality");
        }

        [Test]
        public void included_skip_history_write_failure_cannot_block_a_completed_grab(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, "history-note-failure");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void included_audio_copy_failure_preserves_imported_parts_without_holding_the_download(bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "partial-copy-failure");
        }

        [Test]
        public void moving_requested_files_keeps_included_media_until_its_import_finishes(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested,
            [Values(false, true)] bool conversion)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, conversion ? "move-conversion" : "move");
        }

        [Test]
        public void included_media_respects_active_grabs_and_minimum_score(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested,
            [Values("in-flight", "failed-grab", "removed-grab", "same-grab", "cf-low")] string scenario)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, scenario);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void included_mp3_can_convert_when_only_the_resulting_m4b_quality_is_allowed(bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "conversion-only");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void requested_automatic_mp3_still_requires_allowed_source_quality_before_conversion(bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Audiobook, "requested-conversion-only");
        }

        [Test]
        public void included_conversion_uses_source_size_even_when_output_would_fail_the_score([Values(false, true)] bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "conversion-cf");
        }

        [Test]
        public void included_conversion_with_disallowed_target_keeps_the_allowed_original([Values(false, true)] bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "conversion-quality");
        }

        [Test]
        public void included_media_newly_owned_before_apply_is_skipped(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, "race-owned");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void included_mobi_is_not_imported_under_an_epub_only_profile(bool postgres)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Audiobook, "mobi");
        }

        [Test]
        public void conversion_policy_uses_source_bytes_before_work_starts(
            [Values(false, true)] bool postgres,
            [Values("source-cf-low", "requested-source-cf", "requested-source-cf-low", "multipart-sum", "multipart-unmatched", "conversion-no-profile", "requested-no-profile", "conversion-paused", "conversion-policy-change", "conversion-finished-paused", "conversion-finished-policy-change", "requested-multipart-sum", "requested-multipart-low", "partial-size-retry")] string scenario)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres,
                scenario.StartsWith("requested-", StringComparison.Ordinal) ? BookMediaType.Audiobook : BookMediaType.Ebook, scenario);
        }

        [Test]
        public void included_format_scores_original_release_title_and_group_against_its_own_profile(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested,
            [Values("release-group-negative", "release-title-positive")] string scenario)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, scenario);
        }

        [Test]
        public void failed_or_cancelled_bonus_conversion_does_not_hold_completed_grab(
            [Values(false, true)] bool postgres,
            [Values("conversion-failed", "conversion-cancelled", "conversion-failed-paused", "conversion-cancelled-paused")] string scenario)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, scenario);
        }

        [Test]
        public void unreadable_bonus_file_does_not_hold_completed_grab(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, "unreadable");
        }

        [Test]
        public void bonus_copy_failure_uses_normal_client_cleanup_settings(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType requested,
            [Values("cleanup-enabled", "cleanup-disabled", "cleanup-preserved")] string scenario)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, requested, scenario);
        }

        [Test]
        public void accepted_running_bonus_conversion_keeps_importing_after_policy_changes(
            [Values(false, true)] bool postgres,
            [Values("wanted", "paused", "policy-change", "unmonitored", "no-profile")] string change)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "conversion-running-" + change);
        }

        [Test]
        public void accepted_bonus_still_respects_files_or_edition_pin_added_before_apply(
            [Values(false, true)] bool postgres, [Values("owned", "pin")] string conflict)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "conversion-conflict-" + conflict);
        }

        [Test]
        public void pinning_another_edition_waits_for_the_bonus_worker_before_discarding_its_output(
            [Values(false, true)] bool postgres, [Values("queued", "running")] string phase)
        {
            included_media_uses_its_own_wanted_row_profile_and_original_download(postgres, BookMediaType.Ebook, "conversion-" + phase + "-pin");
        }

        private void RunIncludedMedia(IMainDatabase db, string root, BookMediaType requested, string scenario)
        {
            using var logFactory = new LogFactory();
            var sink = new ConcurrentLogTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration();
            logging.AddRule(LogLevel.Debug, LogLevel.Fatal, sink);
            logFactory.Configuration = logging;
            var logger = logFactory.GetLogger("IncludedMedia");
            HistoryService history = null;
            DownloadHistoryService downloads = null;
            var completed = 0;
            var failed = 0;
            var incompleteImports = 0;
            var conversionFailures = 0;
            var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) =>
            {
                if (args[0] is TrackImportedEvent imported)
                {
                    if (scenario == "cf-low") imported.Message = "Earlier import note";
                    history.Handle(imported);
                }
                if (args[0] is BookFileConvertedEvent converted) history.Handle(converted);
                if (args[0] is BookImportIncompleteEvent incomplete) { incompleteImports++; history.Handle(incomplete); downloads.Handle(incomplete); }
                if (args[0] is BookFileConversionFailedEvent) conversionFailures++;
                if (args[0] is DownloadCompletedEvent done) { completed++; downloads.Handle(done); }
                if (args[0] is DownloadFailedEvent) failed++;
                return null;
            });
            var terminalConversion = scenario.StartsWith("conversion-failed", StringComparison.Ordinal) || scenario.StartsWith("conversion-cancelled", StringComparison.Ordinal);
            var runningConversion = scenario.StartsWith("conversion-running-", StringComparison.Ordinal);
            var converting = terminalConversion || runningConversion || scenario is "conversion-finished-paused" or "conversion-finished-policy-change" or "requested-multipart-sum" or "requested-multipart-low" or "requested-no-profile" or "conversion-paused" or "conversion-policy-change" or "source-cf-low" or "requested-source-cf" or "requested-source-cf-low" or "multipart-sum" or "multipart-unmatched" or "conversion-no-profile" or "conversion" or "move-conversion" or "conversion-cf" or "conversion-quality" or "conversion-only" or "requested-conversion-only";
            var requestedConversionOnly = scenario == "requested-conversion-only";
            converting |= scenario.StartsWith("conversion-conflict-", StringComparison.Ordinal) || scenario == "conversion-queued-pin";
            var moving = scenario is "move" or "move-conversion";
            var partial = scenario is "partial-copy-failure" or "partial-size-retry";
            var multipartRefused = scenario == "multipart-quality";
            var multipart = multipartRefused || scenario is "requested-multipart-sum" or "requested-multipart-low" or "multipart-sum" or "multipart-unmatched";
            var sourceRefused = scenario is "requested-multipart-low" or "source-cf-low" or "requested-source-cf-low";
            var requestedSource = scenario is "requested-multipart-sum" or "requested-multipart-low" or "requested-no-profile" or "requested-source-cf" or "requested-source-cf-low";
            var noteFailure = scenario == "history-note-failure";
            var copyFailure = scenario == "copy-failure" || partial || scenario.StartsWith("cleanup-", StringComparison.Ordinal);
            var unavailable = scenario == "missing";
            var lockProbes = 0;
            var other = requested == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
            var audioProfile = new QualityProfile { Id = 1, Name = "Audio", ProfileType = ProfileType.Audiobook, UpgradeAllowed = false,
                Items = new() { new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true } } };
            var ebookProfile = new QualityProfile { Id = 2, Name = "Text", ProfileType = ProfileType.Ebook, UpgradeAllowed = false,
                Items = new() { new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true } } };
            if (scenario == "cf-low" || noteFailure) (other == BookMediaType.Audiobook ? audioProfile : ebookProfile).MinFormatScore = 10;
            if (converting)
            {
                audioProfile.Items.Add(new QualityProfileQualityItem { Quality = Quality.MP3,
                    Allowed = scenario is not "conversion-only" and not "requested-conversion-only" });
                audioProfile.ConvertToQualityId = Quality.M4B.Id;
            }
            var sizeFormat = new CustomFormat("Source size", new SizeSpecification { Min = (sourceRefused ? 100d : scenario is "multipart-sum" or "requested-multipart-sum" or "partial-size-retry" ? 30d : 14d) / (1024 * 1024 * 1024), Max = 1 }) { Id = 71 };
            if (scenario == "conversion-cf" || sourceRefused || requestedSource || scenario is "multipart-sum" or "partial-size-retry")
            {
                audioProfile.MinFormatScore = 10;
                audioProfile.FormatItems.Add(new ProfileFormatItem { Format = sizeFormat, Score = 10 });
            }
            var releaseFormat = scenario == "release-group-negative"
                ? new CustomFormat("Refused group", new ReleaseGroupSpecification { Value = "^FixtureGroupX$" }) { Id = 72 }
                : new CustomFormat("Retail release", new ReleaseTitleSpecification { Value = "Retail" }) { Id = 73 };
            var releaseEvidence = scenario is "release-group-negative" or "release-title-positive";
            if (releaseEvidence)
            {
                var bonusProfile = other == BookMediaType.Audiobook ? audioProfile : ebookProfile;
                bonusProfile.MinFormatScore = scenario == "release-title-positive" ? 100 : 0;
                bonusProfile.FormatItems.Add(new ProfileFormatItem { Format = releaseFormat, Score = scenario == "release-title-positive" ? 100 : -1000 });
            }
            if (scenario == "conversion-quality") audioProfile.Items[0].Allowed = false;
            if (scenario == "disallowed") (other == BookMediaType.Audiobook ? audioProfile : ebookProfile).Items[0].Allowed = false;
            var authors = new AuthorRepository(db, events, logger);
            var author = authors.Insert(new Author { Name = "Frank Herbert", CleanName = "frankherbert", SortName = "frankherbert",
                TitleSlug = "frank-herbert", Path = Path.Combine(root, "library"), Added = DateTime.UtcNow,
                AudiobookRootFolderPath = Path.Combine(root, "library"), EbookRootFolderPath = Path.Combine(root, "library"),
                AudiobookMonitored = true, EbookMonitored = true, AudiobookQualityProfileId = 1, EbookQualityProfileId = 2 });
            author.AudiobookQualityProfile = audioProfile; author.EbookQualityProfile = ebookProfile;
            if (scenario == "paused") { if (other == BookMediaType.Audiobook) author.AudiobookMonitored = false; else author.EbookMonitored = false; }
            if (scenario == "no-root") { if (other == BookMediaType.Audiobook) author.AudiobookRootFolderPath = null; else author.EbookRootFolderPath = null; }
            if (scenario is "no-profile" or "conversion-no-profile")
            {
                if (other == BookMediaType.Audiobook) { author.AudiobookQualityProfileId = null; author.AudiobookQualityProfile = null; }
                else { author.EbookQualityProfileId = null; author.EbookQualityProfile = null; }
            }
            if (scenario == "requested-no-profile")
            {
                author.AudiobookQualityProfileId = null;
                author.AudiobookQualityProfile = null;
            }
            var bookRows = new BookRepository(db, events);
            var editionRows = new EditionRepository(db, events);
            Book Seed(BookMediaType side)
            {
                var b = bookRows.Insert(new Book { AuthorId = author.Id, Author = author, Title = "Dune", CleanTitle = "dune",
                    TitleSlug = "dune-" + side, GoodreadsWorkId = scenario == "other-work" && side == other ? "gr:99999" : "gr:3634639", MediaType = side, AnyEditionOk = true, Added = DateTime.UtcNow,
                    AudiobookMonitored = side == BookMediaType.Audiobook, EbookMonitored = side == BookMediaType.Ebook });
                var e = editionRows.Insert(new Edition { BookId = b.Id, Book = b, Title = "Dune", MatchingTitle = "dune",
                    ForeignEditionId = "gr:included-" + side, ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 3,
                    Format = side == BookMediaType.Audiobook ? "audiobook" : "ebook", Language = "eng", Monitored = true,
                    Narrator = side == BookMediaType.Audiobook ? "Simon Vance" : null,
                    NarratorNames = side == BookMediaType.Audiobook ? new() { "Simon Vance" } : new(), DurationSeconds = side == BookMediaType.Audiobook ? 3600 : null });
                b.Editions = new List<Edition> { e };
                return b;
            }
            var target = Seed(requested); var included = Seed(other);
            if (scenario == "unmonitored") { included.SetMonitored(false); bookRows.Update(included); }
            if (scenario == "pin")
            {
                included.AnyEditionOk = false; bookRows.Update(included);
                var initial = included.Editions.First(); initial.Monitored = false; editionRows.Update(initial);
                editionRows.Insert(new Edition { BookId = included.Id, Title = "Dune: Selected Translation", MatchingTitle = "dune selected translation",
                    ForeignEditionId = "gr:other", ReadingFormatId = other == BookMediaType.Audiobook ? 2 : 3,
                    Language = "eng", Monitored = true, ManualAdd = true, Narrator = "Different Narrator" });
            }
            var histories = new HistoryRepository(db, events);
            var downloadRows = new DownloadHistoryRepository(db, events);
            history = new HistoryService(histories, logger, downloadRows);
            downloads = new DownloadHistoryService(downloadRows, history);
            var source = Path.Combine(root, "download"); Directory.CreateDirectory(source);
            string PathFor(BookMediaType side) => Path.Combine(source, side == BookMediaType.Audiobook ? (converting ? "Dune.mp3" : "Dune.m4b") : scenario == "mobi" ? "Dune.mobi" : "Dune.epub");
            if (scenario != "wrong-only") File.WriteAllText(PathFor(requested), "requested source");
            if (scenario != "requested-only" && scenario != "missing") File.WriteAllText(PathFor(other), "included source");
            var secondPart = Path.Combine(source, multipartRefused || converting ? "Dune Part 2.mp3" : "Dune Part 2.m4b");
            if (partial) File.WriteAllText(secondPart, "included second part");
            var thirdPart = Path.Combine(source, converting ? "Dune Part 3.mp3" : "Dune Part 3.m4b");
            if (multipart)
            {
                File.WriteAllText(secondPart, "short");
                File.WriteAllText(thirdPart, "included third part");
            }
            var fileRows = new MediaFileRepository(db, events);
            if (scenario == "owned")
            {
                var existing = Path.Combine(root, "owned" + Path.GetExtension(PathFor(other))); File.WriteAllText(existing, "keep existing");
                fileRows.Insert(new BookFile { EditionId = included.Editions.First().Id, Path = existing,
                    Quality = new QualityModel(other == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB), MediaInfo = new MediaInfoModel(),
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
            }
            var raceSeeded = false;
            List<BookFile> ReadCurrentBookFiles(int bookId)
            {
                if (scenario == "race-owned" && bookId == included.Id && !raceSeeded)
                {
                    raceSeeded = true;
                    var path = Path.Combine(root, "already-imported" + Path.GetExtension(PathFor(other)));
                    File.WriteAllText(path, "keep other import");
                    fileRows.Insert(new BookFile { EditionId = included.Editions.First().Id, Path = path,
                        Quality = new QualityModel(other == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB),
                        MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                }
                return fileRows.GetFilesByBook(bookId);
            }
            var media = InterfaceProxy.Proxy<IMediaFileService>((method, args) => method.Name switch
            {
                nameof(IMediaFileService.GetFilesByBook) => ReadCurrentBookFiles((int)args[0]),
                nameof(IMediaFileService.GetFilePathOwnership) => fileRows.GetFilePathOwnership((IReadOnlyCollection<int>)args[0], (IReadOnlyCollection<string>)args[1]),
                nameof(IMediaFileService.GetFilesByBooks) => fileRows.GetFilesByBooks((List<int>)args[0]),
                nameof(IMediaFileService.GetFilesByEdition) => fileRows.GetFilesByEdition((int)args[0]),
                nameof(IMediaFileService.GetFileWithPath) => args[0] is List<string> paths ? fileRows.GetFileWithPath(paths) : fileRows.GetFileWithPath((string)args[0]),
                nameof(IMediaFileService.AddMany) => AddFiles((List<BookFile>)args[0]),
                nameof(IMediaFileService.ReplaceMany) => SaveFiles(args),
                _ => throw new InvalidOperationException("Unexpected file action " + method.Name)
            });
            object AddFiles(List<BookFile> rows) { fileRows.InsertMany(rows); return null; }
            object SaveFiles(object[] args)
            {
                Assert.That((List<BookFile>)args[1], Is.Empty, "Neither format may replace an existing recording");
                if (args.Length == 5)
                    ((Action<Action<IDbConnection, IDbTransaction>>)args[4])((connection, transaction) =>
                        fileRows.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1], connection, transaction));
                else fileRows.ReplaceMany((List<BookFile>)args[0], (List<BookFile>)args[1]);
                return null;
            }
            var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name == nameof(IAuthorService.GetAuthors) ? new List<Author> { author } : author);
            var editionService = new EditionService(editionRows, events, logger);
            var bookService = new BookService(bookRows, editionService, events, authorService, media, null,
                new SeriesBookLinkRepository(db, events), null, logger);
            var fts = new EditionFtsRepository(db, logger); fts.RebuildIndex();
            var config = ConfigServiceTestProxy.Create(BookMatchingStrictness.Balanced, usePathAsTagsFallback: false);
            var matcher = new FileMatchingService(InterfaceProxy.Proxy<IMatchingUploadLogger>((_, _) => null), null,
                new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null, config,
                authorService, events, null, fts, bookService, editionService, editionRows, null, logger);
            var fs = new FileSystem();
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                nameof(IDiskProvider.FileExists) => File.Exists((string)args[0]),
                nameof(IDiskProvider.FolderExists) => Directory.Exists((string)args[0]),
                nameof(IDiskProvider.GetFileInfo) => fs.FileInfo.FromFileName((string)args[0]),
                nameof(IDiskProvider.GetDirectoryInfo) => fs.DirectoryInfo.FromDirectoryName((string)args[0]),
                nameof(IDiskProvider.GetFileInfos) => Directory.GetFiles((string)args[0], "*", SearchOption.AllDirectories).Select(fs.FileInfo.FromFileName).ToList(),
                nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)args[0], "*", SearchOption.AllDirectories),
                nameof(IDiskProvider.DeleteFolder) => throw new AssertionException("The source folder still contains included media"),
                nameof(IDiskProvider.GetFileSize) => new FileInfo((string)args[0]).Length,
                nameof(IDiskProvider.FileGetLastWrite) => File.GetLastWriteTimeUtc((string)args[0]),
                nameof(IDiskProvider.IsFileLocked) => ++lockProbes > 0 && scenario == "locked" && (string)args[0] == PathFor(other),
                _ => throw new InvalidOperationException("Unexpected disk action " + method.Name)
            });
            var tags = InterfaceProxy.Proxy<IMetadataTagService>((method, args) =>
            {
                if (method.Name == nameof(IMetadataTagService.WriteTags)) return null;
                Assert.That(method.Name, Is.EqualTo(nameof(IMetadataTagService.ReadAllTagsAndDuration)));
                var path = ((IFileInfo)args[0]).FullName;
                if (scenario == "unreadable" && path == PathFor(other)) throw new UnauthorizedAccessException("Included source cannot be read");
                var title = (scenario == "unmatched" && path == PathFor(other) || scenario == "multipart-unmatched" && path == secondPart) ? "An unrelated story" : "Dune";
                return (new Dictionary<string, List<string>> { ["TITLE"] = new() { title }, ["ARTIST"] = new() { scenario == "other-author" && path == PathFor(other) ? "Jane Austen" : "Frank Herbert" },
                    ["NARRATOR"] = new() { "Simon Vance" } }, (int?)(Path.GetExtension(path) != ".m4b" ? 3600 : multipartRefused ? 1200 : partial ? 1800 : 3600));
            });
            var mover = new StubMoveBookFiles { TransferFilesOnDisk = true,
                DestinationForFile = local => Path.Combine(root, "library", local.Book.MediaType.ToString(), Path.GetFileName(local.Path)) };
            if (copyFailure) mover.FailSourcePath = partial ? secondPart : PathFor(other);
            var converter = new SuccessfulM4bConversionService();
            var terminalConverter = new BoundaryM4bConverter { Success = !scenario.StartsWith("conversion-failed", StringComparison.Ordinal) };
            var jobs = converting ? CreateConversionJobs(terminalConversion || runningConversion ? terminalConverter : converter, config: CreateConfigService(), media: media, database: db) : null;
            if (scenario.StartsWith("conversion-cancelled", StringComparison.Ordinal))
                terminalConverter.BeforeReturn = _ => Assert.That(jobs.Cancel("MIXED-DOWNLOAD", ConversionRemovalOrigin.UserAction), Is.True);
            var realFormats = new CustomFormatCalculationService(InterfaceProxy.Proxy<ICustomFormatService>((_, _) =>
                releaseEvidence ? new List<CustomFormat> { releaseFormat } :
                scenario == "conversion-cf" || sourceRefused || requestedSource || scenario is "multipart-sum" or "partial-size-retry" ? new List<CustomFormat> { sizeFormat } : new List<CustomFormat>()), logger);
            var checkedSizes = new List<long>();
            var bonusPolicyInputs = new List<LocalBook>();
            var approved = new ImportApprovedBooks(media, tags, new StubMediaInfoExtractor(), authorService, bookService, editionService,
                new StubRecycleBinProvider(), Proxy<IExtraService>(), mover, history, downloads, events,
                Proxy<IManageCommandQueue>(), new SeriesBookLinkService(new SeriesBookLinkRepository(db, events)), Proxy<ISeriesService>(), Proxy<IQualityProfileService>(),
                converter, logger, configService: converting ? CreateConfigService() : null, conversionJobService: jobs,
                customFormatCalculationService: InterfaceProxy.Proxy<ICustomFormatCalculationService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo("ParseCustomFormat"));
                    if (args[0] is not LocalBook local) return new List<CustomFormat>();
                    if (local.IsIncludedMedia) bonusPolicyInputs.Add(local.ShallowClone());
                    if (local.Book.MediaType == BookMediaType.Audiobook)
                    {
                        checkedSizes.Add(local.Size);
                    }
                    return realFormats.ParseCustomFormat(local);
                }));
            var diskScan = new DiskScanService(config, disk, null, media, tags, null, null, authorService,
                InterfaceProxy.Proxy<IRootFolderService>((_, _) => null), null, null, events, logger);
            var queued = new List<NzbDrone.Core.Queue.Queue>();
            if (scenario is "in-flight" or "failed-grab" or "same-grab")
                queued.Add(new NzbDrone.Core.Queue.Queue
                {
                    DownloadId = scenario == "same-grab" ? "MIXED-DOWNLOAD" : "OTHER-DOWNLOAD",
                    TargetBookIds = new() { included.Id },
                    TrackedDownloadState = scenario == "failed-grab" ? TrackedDownloadState.DownloadFailed : TrackedDownloadState.Downloading
                });
            if (scenario is "failed-grab" or "removed-grab")
                histories.Insert(new EntityHistory { AuthorId = author.Id, BookId = included.Id, EditionId = included.Editions.First().Id,
                    DownloadId = "OTHER-DOWNLOAD", EventType = EntityHistoryEventType.Grabbed, Date = DateTime.UtcNow.AddHours(-1),
                    SourceTitle = "Frank Herbert - Dune", Data = new(),
                    Quality = new QualityModel(other == BookMediaType.Audiobook ? Quality.M4B : Quality.EPUB) });
            var queueReads = 0;
            var queue = InterfaceProxy.Proxy<NzbDrone.Core.Queue.IQueueService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("GetQueue"));
                Assert.That(args[0], Is.False, "Import must not poll conversion progress while checking active grabs");
                queueReads++;
                return queued;
            });
            var importer = new DownloadedBooksImportService(disk, diskScan, matcher, tags, approved, bookService, authorService, editionService,
                null, null, null, config, history, events, InterfaceProxy.Proxy<IRuntimeInfo>((_, _) => false), new StubMediaInfoExtractor(), logger, queueService: queue, conversionJobService: jobs ?? ImportLifecycleStubs.NoConversionJobs());
            var item = new DownloadClientItem { DownloadId = "MIXED-DOWNLOAD", Title = "Frank Herbert - Dune", OutputPath = new OsPath(source),
                Status = DownloadItemStatus.Completed, FilePaths = new() { PathFor(requested), PathFor(other) }, DownloadForced = !requestedConversionOnly && !requestedSource,
                FileListConfidence = DownloadClientFileListConfidence.Authoritative,
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 1, Name = "SAB", Type = "Sabnzbd" } };
            if (partial || multipart) item.FilePaths.Add(secondPart);
            if (multipart) item.FilePaths.Add(thirdPart);
            if (scenario == "requested-only") item.FilePaths.Remove(PathFor(other));
            if (scenario == "wrong-only") item.FilePaths.Remove(PathFor(requested));
            var grabbed = histories.Insert(new EntityHistory { AuthorId = author.Id, BookId = target.Id, EditionId = target.Editions.First().Id,
                DownloadId = item.DownloadId, EventType = EntityHistoryEventType.Grabbed, Date = DateTime.UtcNow.AddMinutes(-5),
                Quality = new QualityModel(requested == BookMediaType.Audiobook ? requestedConversionOnly || requestedSource ? Quality.MP3 : Quality.M4B : Quality.EPUB), SourceTitle = item.Title,
                Data = new() { ["Indexer"] = "fixture-indexer", ["ReleaseSource"] = requestedConversionOnly || requestedSource ? "Rss" : "InteractiveSearch",
                    ["CustomFormatScore"] = "500", ["DownloadForced"] = (!requestedConversionOnly && !requestedSource).ToString() } });
            downloads.Handle(new BookGrabbedEvent(new RemoteBook { Author = author, Books = new() { target },
                ParsedBookInfo = new ParsedBookInfo { Quality = grabbed.Quality }, Release = new ReleaseInfo { Title = item.Title } }) { DownloadId = item.DownloadId });
            var tracked = new TrackedDownload { State = TrackedDownloadState.ImportPending, DownloadItem = item,
                RemoteBook = new RemoteBook { Author = author, Books = new() { target }, ParsedBookInfo = new ParsedBookInfo { Quality = grabbed.Quality } } };
            if (releaseEvidence)
            {
                tracked.RemoteBook.ParsedBookInfo.ReleaseTitle = "Frank Herbert - Dune [Retail]";
                tracked.RemoteBook.ParsedBookInfo.ReleaseGroup = "FixtureGroupX";
                tracked.RemoteBook.Release = new ReleaseInfo { Title = "Frank Herbert - Dune [Retail]", IndexerFlags = IndexerFlags.Freeleech };
            }
            var snapshots = new DownloadClientFileSnapshotService(new DownloadClientFileSnapshotRepository(db, events), disk, logger);
            snapshots.CaptureClientList(item);
            var noteWrites = 0;
            var completionHistory = noteFailure ? InterfaceProxy.Proxy<IHistoryService>((method, args) =>
            {
                if (method.Name == nameof(IHistoryService.UpdateMany))
                {
                    noteWrites++;
                    throw new InvalidOperationException("History note write unavailable");
                }

                return method.Invoke(history, args);
            }) : history;
            var subject = new CompletedDownloadService(events, completionHistory, InterfaceProxy.Proxy<IProvideImportItemService>((_, args) => ((DownloadClientItem)args[0]).Clone()),
                importer, InterfaceProxy.Proxy<IDownloadImportModeResolver>((_, _) => moving ? ImportMode.Move : ImportMode.Copy), new TrackedDownloadAlreadyImported(logger, media), logger, snapshots);
            string acceptedBonusOutput = null;
            void PinAnotherEdition()
            {
                included.AnyEditionOk = false; bookRows.Update(included);
                var previous = included.Editions.First(); previous.Monitored = false; editionRows.Update(previous);
                editionRows.Insert(new Edition { BookId = included.Id, Title = "Dune: pinned recording", MatchingTitle = "dune pinned recording",
                    ForeignEditionId = "local:pinned-recording", ReadingFormatId = 2, Monitored = true, ManualAdd = true, Narrator = "Different Narrator" });
            }
            void AssertPinRefusal()
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(fileRows.GetFilesByBook(target.Id), Has.Count.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported), Is.Empty);
                Assert.That(jobs.Get(item.DownloadId), Is.Null);
                Assert.That(acceptedBonusOutput, Is.Not.Null);
                Assert.That(File.Exists(acceptedBonusOutput), Is.False);
                Assert.That(Directory.Exists(Path.GetDirectoryName(acceptedBonusOutput)), Is.False);
                Assert.That(File.Exists(PathFor(other)), Is.True);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.EqualTo(1));
            }
            subject.Import(tracked);
            acceptedBonusOutput = jobs?.Get(item.DownloadId)?.OutputPath;
            if (requestedConversionOnly)
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                Assert.That(fileRows.GetFilesByBook(target.Id), Is.Empty);
                Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(converter.ConvertCalls, Is.Zero);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.Zero);
                Assert.That(File.Exists(PathFor(requested)), Is.True);
                Assert.That(completed, Is.Zero);
                Assert.That(failed, Is.Zero);
                Assert.That(incompleteImports, Is.GreaterThan(0));
                Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains("MP3"));
                return;
            }
            if (scenario is "requested-source-cf-low" or "requested-no-profile" or "requested-multipart-low")
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(fileRows.GetFilesByBook(target.Id), Is.Empty);
                Assert.That(converter.ConvertCalls, Is.Zero);
                Assert.That(jobs.Get(item.DownloadId), Is.Null);
                Assert.That(Directory.GetDirectories(root, ".chaptarr-conversions", SearchOption.AllDirectories), Is.Empty);
                if (scenario != "requested-no-profile") Assert.That(checkedSizes, Is.Not.Empty.And.All.EqualTo(scenario == "requested-multipart-low" ? 40L : 16L));
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                return;
            }
            if (unavailable || scenario == "unreadable")
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(completed, Is.EqualTo(1));
                Assert.That(failed, Is.Zero);
                Assert.That(incompleteImports, Is.Zero);
                Assert.That(fileRows.GetFilesByBook(target.Id), Has.Count.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages),
                    Has.Some.Contains(unavailable ? "not available" : "cannot be read"));
                Assert.That(histories.GetByBook(target.Id, EntityHistoryEventType.BookFileImported).Single().GetDataValue("Message"),
                    Does.Contain(unavailable ? "not available" : "cannot be read"));
                return;
            }
            if (converting && !sourceRefused && scenario is not "conversion-quality" and not "conversion-no-profile" and not "multipart-unmatched")
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending), string.Join("; ", tracked.StatusMessages.SelectMany(message => message.Messages)));
                Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                Assert.That(completed, Is.Zero);
                if (scenario is "conversion-paused" or "conversion-policy-change" or "conversion-queued-pin")
                {
                    Assert.That(jobs.Get(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                    if (scenario == "conversion-paused") author.AudiobookMonitored = false;
                    else if (scenario == "conversion-queued-pin") PinAnotherEdition();
                    else audioProfile.MinFormatScore = 100;
                    subject.Import(tracked);
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending), "Accepted queued bonus ignores later pause/profile policy");
                    Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                    Assert.That(completed, Is.Zero);
                    Assert.That(converter.ConvertCalls, Is.Zero);
                    Assert.That(jobs.Get(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Queued));
                    Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                    Assert.That(File.ReadAllText(PathFor(other)), Is.EqualTo("included source"));
                }
                if (runningConversion)
                {
                    using var entered = new ManualResetEventSlim();
                    using var release = new ManualResetEventSlim();
                    terminalConverter.BeforeReturn = _ =>
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Conversion test did not release worker");
                    };
                    var worker = System.Threading.Tasks.Task.CompletedTask;
                    try
                    {
                        worker = ConversionJobServiceFixture.DispatchWorkers(jobs);
                        Assert.That(entered.Wait(TimeSpan.FromSeconds(15)), Is.True);
                        Assert.That(jobs.IsRunning(item.DownloadId), Is.True);
                        if (scenario == "conversion-running-paused") author.AudiobookMonitored = false;
                        if (scenario == "conversion-running-policy-change") audioProfile.MinFormatScore = 100;
                        if (scenario == "conversion-running-unmonitored") { included.SetMonitored(false); bookRows.Update(included); }
                        if (scenario == "conversion-running-no-profile") { author.AudiobookQualityProfileId = null; author.AudiobookQualityProfile = null; }
                        if (scenario == "conversion-running-pin") PinAnotherEdition();
                        subject.Import(tracked);
                        Assert.That(fileRows.GetFilesByBook(target.Id), Has.Count.EqualTo(1));
                        Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                        Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportPending));
                        Assert.That(subject.CanRemoveDownload(tracked), Is.False);
                        Assert.That(completed, Is.Zero);
                        var job = jobs.Get(item.DownloadId);
                        Assert.That(job.Status, Is.EqualTo(ConversionJobStatus.Converting));
                        Assert.That(job.RequestJson.Contains("\"DiscardRequested\":true", StringComparison.Ordinal), Is.False);
                        Assert.That(File.Exists(terminalConverter.OutputPath), Is.True, "An active worker's output is not removed underneath it");
                        Assert.That(File.Exists(PathFor(other)), Is.True);
                        Assert.That(failed, Is.Zero);
                        Assert.That(incompleteImports, Is.Zero);
                    }
                    finally
                    {
                        release.Set();
                        Assert.That(worker.Wait(TimeSpan.FromSeconds(15)), Is.True);
                    }
                    subject.Import(tracked);
                    if (scenario == "conversion-running-pin")
                    {
                        AssertPinRefusal();
                        Assert.That(terminalConverter.Calls, Is.EqualTo(1));
                        Assert.That(File.Exists(terminalConverter.OutputPath), Is.False);
                        return;
                    }
                    Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                    Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                    Assert.That(fileRows.GetFilesByBook(included.Id), Has.Count.EqualTo(1));
                    Assert.That(terminalConverter.Calls, Is.EqualTo(1));
                    Assert.That(jobs.Get(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                    Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported), Has.Count.EqualTo(1));
                    Assert.That(checkedSizes, Has.Count.EqualTo(1), "Accepted conversion does not rerun custom-format policy while running or ready");
                    return;
                }
                Assert.That(ConversionJobServiceFixture.DispatchWorkers(jobs).Wait(TimeSpan.FromSeconds(15)), Is.True);
                if (terminalConversion && scenario.EndsWith("-paused", StringComparison.Ordinal)) author.AudiobookMonitored = false;
                if (scenario == "conversion-finished-paused") author.AudiobookMonitored = false;
                if (scenario == "conversion-finished-policy-change") audioProfile.MinFormatScore = 100;
                if (scenario == "conversion-conflict-owned")
                {
                    var existingPath = Path.Combine(root, "arrived-meanwhile.m4b");
                    File.WriteAllText(existingPath, "keep existing recording");
                    fileRows.Insert(new BookFile { EditionId = included.Editions.First().Id, Edition = included.Editions.First(),
                        Path = existingPath, Size = new FileInfo(existingPath).Length, Quality = new QualityModel(Quality.M4B),
                        MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow });
                }
                if (scenario == "conversion-conflict-pin")
                {
                    PinAnotherEdition();
                }
                subject.Import(tracked);
            }
            if (scenario == "conversion-queued-pin")
            {
                AssertPinRefusal();
                Assert.That(converter.ConvertCalls, Is.EqualTo(1));
                return;
            }
            if (scenario.StartsWith("conversion-conflict-", StringComparison.Ordinal))
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(converter.ConvertCalls, Is.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(target.Id), Has.Count.EqualTo(1));
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported), Is.Empty);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.EqualTo(1), "Only the requested ebook may transfer after a new ownership/pin conflict");
                Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages), Has.Some.Contains("no longer missing"));
                if (scenario == "conversion-conflict-owned")
                {
                    var existing = fileRows.GetFilesByBook(included.Id).Single();
                    Assert.That(File.ReadAllText(existing.Path), Is.EqualTo("keep existing recording"));
                }
                else Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(File.Exists(PathFor(other)), Is.True);
                return;
            }
            if (terminalConversion)
            {
                Assert.That(terminalConverter.Calls, Is.EqualTo(1));
                Assert.That(jobs.Get(item.DownloadId), Is.Null);
                Assert.That(File.Exists(terminalConverter.OutputPath), Is.False);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(completed, Is.EqualTo(1));
                Assert.That(failed, Is.Zero);
                Assert.That(incompleteImports, Is.Zero);
                Assert.That(fileRows.GetFilesByBook(target.Id), Has.Count.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(tracked.StatusMessages, Is.Not.Empty);
                Assert.That(histories.GetByBook(target.Id, EntityHistoryEventType.BookFileImported).Single().GetDataValue("Message"),
                    Does.Contain("Skipped included file"));
                Assert.That(File.Exists(PathFor(other)), Is.True, "Normal download cleanup, not bonus import, owns source deletion");
                return;
            }
            if (scenario is "conversion-paused" or "conversion-policy-change" or "conversion-finished-paused" or "conversion-finished-policy-change")
            {
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                Assert.That(fileRows.GetFilesByBook(target.Id), Has.Count.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id), Has.Count.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id).Single().Quality.Quality, Is.EqualTo(Quality.M4B));
                Assert.That(converter.ConvertCalls, Is.EqualTo(1));
                Assert.That(checkedSizes, Has.Count.EqualTo(1), "Queued/ready accepted conversion must not evaluate changed custom-format policy again");
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(jobs.Get(item.DownloadId).Status, Is.EqualTo(ConversionJobStatus.Completed));
                Assert.That(File.Exists(PathFor(other)), Is.True);
                Assert.That(incompleteImports, Is.Zero);
                Assert.That(conversionFailures, Is.Zero);
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported), Has.Count.EqualTo(1));
                typeof(ConversionJobService).GetMethod("Reconcile", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(jobs, null);
                subject.Import(tracked);
                Assert.That(converter.ConvertCalls, Is.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id), Has.Count.EqualTo(1));
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported), Has.Count.EqualTo(1));
                return;
            }
            Assert.That(lockProbes, Is.Zero, "Download-client imports must not use exclusive file-lock probes");
            var successful = scenario is "release-title-positive" or "requested-multipart-sum" or "conversion-cf" or "requested-source-cf" or "multipart-sum" or "both" or "conversion" or "conversion-only" or "conversion-quality" or "move" or "move-conversion" or "locked" or "missing" or "failed-grab" or "removed-grab" or "same-grab";
            if (scenario is "in-flight" or "failed-grab" or "removed-grab" or "same-grab" or "cf-low") Assert.That(queueReads, Is.EqualTo(1));
            Assert.That(tracked.State, Is.EqualTo(scenario == "wrong-only" ? TrackedDownloadState.ImportBlocked : TrackedDownloadState.Imported),
                string.Join("; ", tracked.StatusMessages.SelectMany(message => message.Messages)));
            Assert.That(fileRows.GetFilesByBook(target.Id).Count, Is.EqualTo(scenario == "wrong-only" ? 0 : 1));
            Assert.That(fileRows.GetFilesByBook(included.Id).Count, Is.EqualTo(successful || scenario is "owned" or "race-owned" || partial ? 1 : 0),
                "Policy sizes: " + string.Join(",", checkedSizes) + Environment.NewLine + string.Join(Environment.NewLine, sink.Logs));
            Assert.That(failed, Is.Zero);
            Assert.That(completed, Is.EqualTo(scenario == "wrong-only" ? 0 : 1));
            Assert.That(histories.FindByDownloadId(item.DownloadId).Count(row => row.EventType == EntityHistoryEventType.Grabbed), Is.EqualTo(successful || partial ? 2 : 1));
            var skipReason = scenario switch
            {
                "paused" => "the author format is paused", "no-root" => "no root folder is configured",
                "no-profile" or "conversion-no-profile" => "no quality profile is configured", "disallowed" => "the quality is not allowed",
                "unmonitored" => "the book is not monitored", "owned" => "the book already has files",
                "other-work" => "it is not the grabbed work's other format", "in-flight" => "another grab is still active",
                "mobi" => "the quality is not allowed", "multipart-quality" => "the quality is not allowed",
                "cf-low" or "source-cf-low" or "release-group-negative" or "history-note-failure" => "below Author profile minimum",
                "race-owned" => "no longer missing", _ => null
            };
            if (skipReason != null) Assert.That(sink.Logs.Any(message => message.Contains(skipReason, StringComparison.OrdinalIgnoreCase)), Is.True);
            if (scenario is "release-group-negative" or "disallowed" or "mobi" or "cf-low" or "source-cf-low" or "in-flight" or "race-owned" or "multipart-quality" or "history-note-failure")
            {
                Assert.That(tracked.Status, Is.EqualTo(TrackedDownloadStatus.Warning));
                Assert.That(tracked.StatusMessages.SelectMany(message => message.Messages)
                    .Any(message => message.Contains(skipReason, StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(subject.CanRemoveDownload(tracked), Is.True, "An optional policy skip must not block cleanup of the successful grab");
                Assert.That(incompleteImports, Is.Zero);
                Assert.That(conversionFailures, Is.Zero);
                var originalImport = histories.GetByBook(target.Id, EntityHistoryEventType.BookFileImported).Single();
                var persistedMessage = originalImport.GetDataValue("Message");
                if (noteFailure)
                {
                    Assert.That(noteWrites, Is.EqualTo(1));
                    Assert.That(persistedMessage, Is.Null);
                    Assert.That(sink.Logs, Has.Some.Contains("Could not save the included-file skip note").And.Contains(item.DownloadId));
                }
                else
                {
                    Assert.That(persistedMessage, Does.Contain("Skipped included file"));
                    if (scenario == "cf-low")
                    {
                        Assert.That(persistedMessage, Does.StartWith("Earlier import note"));
                        subject.Import(tracked);
                        var afterRetry = histories.GetByBook(target.Id, EntityHistoryEventType.BookFileImported).Single().GetDataValue("Message");
                        Assert.That(afterRetry, Is.EqualTo(persistedMessage), "Retry must not append the same optional skip again");
                        Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                    }
                    Assert.That(persistedMessage.Contains(skipReason, StringComparison.OrdinalIgnoreCase), Is.True);
                }
            }
            if (multipartRefused)
            {
                Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported), Is.Empty);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.EqualTo(1), "Only the requested ebook may transfer");
                Assert.That(converter.ConvertCalls, Is.Zero);
                Assert.That(tracked.StatusMessages, Has.Length.EqualTo(3));
                Assert.That(File.ReadAllText(PathFor(other)), Is.EqualTo("included source"));
                Assert.That(File.ReadAllText(secondPart), Is.EqualTo("short"));
                Assert.That(File.ReadAllText(thirdPart), Is.EqualTo("included third part"));
            }
            if (scenario is "source-cf-low" or "conversion-no-profile" or "multipart-unmatched" || multipartRefused)
            {
                Assert.That(converter.ConvertCalls, Is.Zero);
                Assert.That(jobs?.Get(item.DownloadId), Is.Null);
                Assert.That(Directory.GetDirectories(root, ".chaptarr-conversions", SearchOption.AllDirectories), Is.Empty);
                Assert.That(fileRows.GetFilesByBook(included.Id), Is.Empty);
                Assert.That(mover.CopyCalls + mover.MoveCalls, Is.EqualTo(1));
            }
            if (scenario is "conversion-cf" or "requested-source-cf" or "multipart-sum" or "requested-multipart-sum")
            {
                var audio = fileRows.GetFilesByBook(requestedSource ? target.Id : included.Id).Single();
                Assert.That(converter.ConvertCalls, Is.EqualTo(1));
                Assert.That(audio.Size, Is.EqualTo(13L), "Persist output size without reversing the source-based policy decision");
                var expectedSize = scenario == "requested-multipart-sum" ? 40L : scenario == "multipart-sum" ? 39L : requestedSource ? 16L : 15L;
                Assert.That(checkedSizes, Is.Not.Empty.And.All.EqualTo(expectedSize));
                Assert.That(audio.Quality.Quality, Is.EqualTo(Quality.M4B));
            }
            if (scenario == "conversion-only")
            {
                Assert.That(converter.ConvertCalls, Is.EqualTo(1));
                Assert.That(fileRows.GetFilesByBook(included.Id).Single().Quality.Quality, Is.EqualTo(Quality.M4B));
                Assert.That(checkedSizes, Is.Not.Empty.And.All.EqualTo(15L), "Policy checks use source bytes, never the thirteen-byte output");
                Assert.That(conversionFailures, Is.Zero);
            }
            if (scenario == "conversion-quality")
            {
                Assert.That(converter.ConvertCalls, Is.Zero, "The conversion planner already refuses a disallowed target");
                Assert.That(fileRows.GetFilesByBook(included.Id).Single().Quality.Quality, Is.EqualTo(Quality.MP3));
            }
            if (scenario == "race-owned")
            {
                Assert.That(raceSeeded, Is.True);
                Assert.That(sink.Logs.Any(message => message.Contains("no longer missing", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(checkedSizes, Is.Empty, "The fresh existing-file recheck must skip before apply-time custom formats");
                Assert.That(File.ReadAllText(fileRows.GetFilesByBook(included.Id).Single().Path), Is.EqualTo("keep other import"));
            }
            if (releaseEvidence)
            {
                Assert.That(bonusPolicyInputs.Count, Is.EqualTo(1), "Score the bonus recording once with its own media profile");
                var input = bonusPolicyInputs.Single();
                Assert.That(input.Book.Id, Is.EqualTo(included.Id));
                Assert.That(input.Book.MediaType, Is.EqualTo(other));
                Assert.That(input.Size, Is.EqualTo(15L));
                Assert.That(input.SceneName, Is.EqualTo("Frank Herbert - Dune [Retail]"));
                Assert.That(input.ReleaseGroup, Is.EqualTo("FixtureGroupX"));
                Assert.That(input.IndexerFlags, Is.EqualTo(default(IndexerFlags)));
                Assert.That(fileRows.GetFilesByBook(target.Id).Count, Is.EqualTo(1));
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
            }
            if (successful)
            {
                var includedGrab = histories.GetByBook(included.Id, EntityHistoryEventType.Grabbed).Single(row => row.DownloadId == item.DownloadId);
                Assert.That(includedGrab.GetDataValue("Indexer"), Is.EqualTo("fixture-indexer"));
                Assert.That(includedGrab.SourceTitle, Is.EqualTo(grabbed.SourceTitle));
                Assert.That(includedGrab.DownloadId, Is.EqualTo(grabbed.DownloadId));
                Assert.That(includedGrab.GetDataValue("IncludedFromGrabId"), Is.EqualTo(grabbed.Id.ToString()));
                Assert.That(includedGrab.GetDataValue("CustomFormatScore"), Is.Null);
                Assert.That(RemoteBook.GetCapturedGrabHistory(histories.FindByDownloadId(item.DownloadId)).Select(row => row.BookId), Is.EqualTo(new[] { target.Id }));
                var imported = histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported).Single();
                Assert.That(imported.DownloadId, Is.EqualTo(item.DownloadId.ToUpperInvariant()));
                Assert.That(imported.GetDataValue("IncludedFromGrabId"), Is.EqualTo(grabbed.Id.ToString()));
                Assert.That(imported.GetDataValue("Indexer"), Is.EqualTo("fixture-indexer"));
                Assert.That(imported.GetDataValue("CustomFormatScore"), Is.Null);
                Assert.That(File.Exists(PathFor(other)), Is.True);
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                if (moving) Assert.That(File.Exists(PathFor(requested)), Is.EqualTo(converting && requested == BookMediaType.Audiobook),
                    "Conversion imports its output and retains the original sources for normal download cleanup");
                subject.Import(tracked);
                Assert.That(fileRows.GetFilesByBook(included.Id).Count, Is.EqualTo(1));
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported).Count, Is.EqualTo(1));
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.Grabbed).Count(row => row.DownloadId == item.DownloadId), Is.EqualTo(1));
                if (scenario is "failed-grab" or "removed-grab")
                    Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.Grabbed).Count(row => row.DownloadId == "OTHER-DOWNLOAD"), Is.EqualTo(1), "Import must preserve the earlier grab history");
            }
            if (copyFailure)
            {
                Assert.That(incompleteImports, Is.Zero, "An optional copy failure must not fail the completed grab");
                Assert.That(File.Exists(PathFor(other)), Is.True);
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
                Assert.That(histories.GetByBook(target.Id, EntityHistoryEventType.BookFileImported).Single().GetDataValue("Message"),
                    Does.Contain("Skipped included file").And.Contain("Import failed"));
                if (scenario.StartsWith("cleanup-", StringComparison.Ordinal))
                {
                    var removalCalls = 0;
                    var definition = new DownloadClientDefinition { RemoveCompletedDownloads = scenario != "cleanup-disabled" };
                    var client = InterfaceProxy.Proxy<IDownloadClient>((method, args) =>
                    {
                        if (method.Name == "get_Definition") return definition;
                        if (method.Name == "get_Name") return "Fixture download client";
                        Assert.That(method.Name, Is.EqualTo(nameof(IDownloadClient.RemoveItem)));
                        Assert.That(args[1], Is.True, "Normal client cleanup controls removal of download data");
                        removalCalls++;
                        return null;
                    });
                    var hub = new DownloadEventHub(subject,
                        InterfaceProxy.Proxy<IProvideDownloadClient>((_, _) => client),
                        InterfaceProxy.Proxy<IDownloadImportModeResolver>((method, _) =>
                        {
                            Assert.That(method.Name, Is.EqualTo(nameof(IDownloadImportModeResolver.ShouldPreserveDownloadClientItem)));
                            return scenario == "cleanup-preserved";
                        }), logger);
                    tracked.DownloadItem.CanBeRemoved = true;
                    hub.Handle(new DownloadCanBeRemovedEvent(tracked));
                    Assert.That(removalCalls, Is.EqualTo(scenario == "cleanup-enabled" ? 1 : 0));
                    Assert.That(tracked.DownloadItem.Removed, Is.EqualTo(scenario == "cleanup-enabled"));
                    Assert.That(fileRows.GetFilesByBook(target.Id).All(file => File.Exists(file.Path)), Is.True);
                }
                if (scenario != "partial-size-retry") return;
                var firstIds = fileRows.GetFilesByBook(included.Id).Select(file => file.Id).ToArray();
                mover.FailSourcePath = null;
                // A deliberate retry while sources remain uses persisted file/history evidence.
                tracked = new TrackedDownload { State = TrackedDownloadState.ImportPending, DownloadItem = item.Clone(), RemoteBook = tracked.RemoteBook };
                subject.Import(tracked);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.Imported));
                var finalFiles = fileRows.GetFilesByBook(included.Id);
                Assert.That(finalFiles.Count, Is.EqualTo(partial ? 2 : 1));
                if (scenario == "partial-size-retry") Assert.That(checkedSizes, Is.Not.Empty.And.All.EqualTo(35L), "Retry size includes the stored first part and remaining part");
                Assert.That(firstIds.All(id => finalFiles.Any(file => file.Id == id)), Is.True);
                Assert.That(histories.GetByBook(included.Id, EntityHistoryEventType.BookFileImported).Count, Is.EqualTo(partial ? 2 : 1));
                Assert.That(subject.CanRemoveDownload(tracked), Is.True);
            }
        }
    }
}
