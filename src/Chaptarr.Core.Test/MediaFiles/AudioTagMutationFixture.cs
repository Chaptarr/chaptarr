using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Messaging.Events;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    [NonParallelizable]
    public partial class AudioTagMutationFixture
    {
        private string _directory;
        private LogFactory _logs;
        private MemoryTarget _warnings;
        private MemoryTarget _allLogs;
        private Action _beforeUpdate;
        private Action<int> _beforeAuthorRead;
        private static string _instrumentedPath;
        private static Action<TagLib.File> _beforeSave;

        private sealed class ObservedAudioFile : TagLib.Mpeg.AudioFile
        {
            public ObservedAudioFile(TagLib.File.IFileAbstraction file, TagLib.ReadStyle style) : base(file, style)
            {
            }

            public override void Save()
            {
                _beforeSave?.Invoke(this);
                base.Save();
            }
        }

        [OneTimeSetUp]
        public void ObserveFixtureSaves() => TagLib.File.AddFileTypeResolver((file, _, style) =>
            file.Name == _instrumentedPath ? new ObservedAudioFile(file, style) : null);

        private IDiskProvider _disk;
        private AudioTagService _service;
        private IMediaFileService _media;
        private IConfigService _config;
        private readonly List<BookFile> _files = new List<BookFile>();
        private readonly List<BookFileRetaggedEvent> _events = new List<BookFileRetaggedEvent>();
        private Func<string, bool> _prepare;
        private bool _scrub;
        private int _scrubReads;
        private WriteAudioTagsType _mode;
        private Func<WriteAudioTagsType> _readMode;
        private Func<List<int>, List<SeriesBookLink>> _seriesRead;
        private int _queries;
        private int _updates;
        private int _guards;
        private static readonly string[] TagFormats = { ".mp3", ".m4b", ".flac", ".ogg", ".opus", ".wma", ".wv" };

        private bool ReadScrubSetting()
        {
            _scrubReads++;
            return _scrub;
        }

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_directory);
            _files.Clear();
            _events.Clear();
            _seriesRead = null;
            _scrub = false;
            _scrubReads = 0;
            _mode = WriteAudioTagsType.Sync;
            _readMode = () => _mode;
            _queries = _updates = _guards = 0;
            _prepare = null;
            _beforeUpdate = null;
            _beforeAuthorRead = null;
            _instrumentedPath = null;
            _beforeSave = null;
            _logs = new LogFactory();
            _warnings = new MemoryTarget { Layout = "${level}: ${message}${exception:format=ToString}" };
            _allLogs = new MemoryTarget { Layout = "${level}: ${message}${exception:format=ToString}" };
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Trace, LogLevel.Fatal, _allLogs);
            logging.AddRule(LogLevel.Warn, LogLevel.Fatal, _warnings);
            _logs.Configuration = logging;
            var logger = _logs.GetLogger("tag-fixture");
            _disk = RealDiskProvider.Create(logger);
            var config = Proxy<IConfigService>((m, _) => m.Name switch
            {
                "get_WriteAudioTags" => _readMode(),
                "get_ScrubAudioTags" => ReadScrubSetting(),
                "get_SetPermissionsLinux" => false,
                _ => throw new NotImplementedException(m.Name)
            });
            _config = config;
            var media = Proxy<IMediaFileService>((m, args) =>
            {
                if (m.Name == "GetFilesByBooks") { _queries++; return _files.ToList(); }
                if (m.Name == "Update") { _beforeUpdate?.Invoke(); _updates++; return _files[0]; }
                if (m.Name == "GetFilesByAuthor") { _beforeAuthorRead?.Invoke((int)args[0]); return _files.Where(f => f.Author.Id == (int)args[0]).ToList(); }
                return _files.ToList();
            });
            _media = media;
            var safety = new FileMutationSafetyService(_disk, new DiskTransferService(_disk, logger), new MediaFileAttributeService(config, _disk, logger), logger);
            _service = new AudioTagService(config, media, _disk,
                Proxy<IRootFolderWatchingService>((_, _) => null),
                Proxy<IFileMutationSafetyService>((_, a) => { _guards++; return _prepare != null ? _prepare((string)a[0]) : safety.EnsureMutableFile((string)a[0]); }),
                Proxy<IEventAggregator>((_, a) => { _events.Add((BookFileRetaggedEvent)a[0]); return null; }),
                null, logger, new TagExtractionService(new[] { new TagLibSharpExtractor() }, null, logger),
                Proxy<ISeriesBookLinkService>((_, args) => _seriesRead != null ? _seriesRead((List<int>)args[0]) : throw new AssertionException("Fixture series must be loaded explicitly")));
        }

        private TagBatch RetagFiles(RetagFilesCommand command) => _service.RetagFiles(command,
            _files.Where(file => command.Files.Contains(file.Id)).ToList(), new TagBatch());

        private TagBatch RetagAuthors(RetagAuthorCommand command) => _service.RetagAuthor(command,
            _files.Select(file => file.Author).DistinctBy(author => author.Id).Where(author => command.AuthorIds.Contains(author.Id)).ToList(), new TagBatch());

        [TearDown]
        public void TearDown()
        {
            _instrumentedPath = null;
            _beforeSave = null;
            _logs.Dispose();
            Directory.Delete(_directory, true);
        }

        private BookFile Add(string extension = ".mp3", uint track = 0, uint count = 0)
        {
            var path = Path.Combine(_directory, _files.Count + extension);
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "silence" + extension), path);
            using (var audio = TagLib.File.Create(path))
            {
                audio.Tag.Track = track;
                audio.Tag.TrackCount = count;
                audio.Save();
            }

            var author = new Author { Id = 1, Name = "Fixture author" };
            var book = new Book { Id = 2, Author = author, Title = "Fixture book", MediaType = BookMediaType.Audiobook, SeriesLinks = new List<SeriesBookLink>() };
            var edition = new Edition { Id = 3, BookId = 2, Book = book, Title = "Fixture title", Publisher = "Fixture publisher", NarratorNames = new List<string> { " First narrator ", "", "Second narrator" }, ReleaseDate = new DateTime(2024, 1, 1) };
            var file = new BookFile { Id = _files.Count + 1, EditionId = 3, Edition = edition, Author = author, Path = path };
            _files.Add(file);
            return file;
        }

        public static IEnumerable<TestCaseData> FormatCases()
        {
            foreach (var extension in TagFormats)
            {
                yield return new TestCaseData(extension, false);
                yield return new TestCaseData(extension, true);
            }
        }

        [TestCaseSource(nameof(FormatCases))]
        public void publisher_roundtrip_and_real_change_should_preserve_download_then_become_noop(string extension, bool scrub)
        {
            _scrub = scrub;
            var file = Add(extension);
            var download = Path.Combine(_directory, "download" + extension);
            if (!_disk.TryCreateHardLink(file.Path, download)) Assert.Ignore("Filesystem does not support hardlinks");
            var original = File.ReadAllBytes(download);
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(File.ReadAllBytes(download), Is.EqualTo(original));
            Assert.That(_service.ReadAllTags(file.Path)["PUBLISHER"], Does.Contain(file.Edition.Publisher));
            File.Delete(download);
            Assert.That(_disk.TryCreateHardLink(file.Path, download), Is.True);
            var written = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            Assert.Multiple(() =>
            {
                Assert.That(_events, Has.Count.EqualTo(1));
                Assert.That(_guards, Is.EqualTo(1));
                Assert.That(_queries, Is.Zero, "Single-file writes use their caller's loaded evidence");
                Assert.That(_disk.GetFileLinkCount(file.Path), Is.EqualTo(2));
                Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(written));
                Assert.That(_warnings.Logs, Is.Empty);
            });
        }

        [TestCase("Worlds of Exile and Illusion; Planet of Exile")]
        [TestCase("Ποιος είναι εκεί;")]
        public void full_mp4_text_must_converge_without_hiding_a_changed_suffix(string title)
        {
            var file = Add(".m4b");
            file.Edition.Title = title;
            _service.WriteTags(file, false);
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
            file.Edition.Title = "Book; Part A";
            _service.WriteTags(file, false);
            file.Edition.Title = "Book; Part B";
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(3));
            Assert.That(_service.ReadAllTags(file.Path)["TITLE"], Does.Contain("Book; Part B"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unknown_parts_keep_existing_tracks_without_loading_file_counts(bool scrub)
        {
            _scrub = scrub;
            var files = new[] { Add(track: 1, count: 3), Add(track: 2, count: 3), Add(track: 3, count: 3) };
            var previews = _service.GetRetagPreviewsByBook(2);
            Assert.That(previews.All(p => !p.Changes.ContainsKey("TRACKNUMBER") && !p.Changes.ContainsKey("TRACKCOUNT")), Is.True);
            Assert.That(_queries, Is.Zero);
            RetagFiles(new RetagFilesCommand { Files = files.Select(f => f.Id).ToList() });
            for (var i = 0; i < files.Length; i++)
            {
                using var audio = TagLib.File.Create(files[i].Path);
                Assert.That(audio.Tag.Track, Is.EqualTo(i + 1));
                Assert.That(audio.Tag.TrackCount, Is.EqualTo(3));
            }
            Assert.That(_queries, Is.Zero);
        }

        public static IEnumerable<TestCaseData> PreservedTrackCases()
        {
            foreach (var extension in TagFormats)
            {
                yield return new TestCaseData(extension, 0, 26u, 27u);
                yield return new TestCaseData(extension, 1, 23u, 45u);
                yield return new TestCaseData(extension, 0, 18u, 0u);
                yield return new TestCaseData(extension, 64, 1u, 0u);
                yield return new TestCaseData(extension, 0, 0u, 0u);
                yield return new TestCaseData(extension, 0, 0u, 7u);
                yield return new TestCaseData(extension, 1, 1u, extension == ".m4b" ? ushort.MaxValue : uint.MaxValue);
            }
        }

        [TestCaseSource(nameof(PreservedTrackCases))]
        public void existing_numbering_must_not_cause_retag_or_hardlink_separation(string extension, int part, uint track, uint total)
        {
            var file = Add(extension, track, total);
            file.Part = part;
            file.Edition.BookFiles = new List<BookFile> { file };
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Title = file.Edition.Title;
                audio.Tag.Album = file.Edition.Book.Title;
                audio.Tag.Performers = new[] { file.Author.Name };
                audio.Tag.Composers = new[] { "First narrator", "Second narrator" };
                audio.Tag.Year = 2024;
                if (audio.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                    xiph.SetField("PUBLISHER", file.Edition.Publisher);
                else if (audio.GetTag(TagLib.TagTypes.Asf) is TagLib.Asf.Tag asf)
                    asf.SetDescriptorString(file.Edition.Publisher, "WM/Publisher");
                else audio.Tag.Publisher = file.Edition.Publisher;
                audio.Save();
            }

            var seed = Path.Combine(_directory, "seed" + extension);
            if (!_disk.TryCreateHardLink(file.Path, seed)) Assert.Ignore("Filesystem does not support hardlinks");
            var before = File.ReadAllBytes(file.Path);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            RetagFiles(new RetagFilesCommand { Files = new List<int> { file.Id } });
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(before));
                Assert.That(File.ReadAllBytes(seed), Is.EqualTo(before));
                Assert.That(_disk.GetFileLinkCount(file.Path), Is.EqualTo(2));
                Assert.That(_guards, Is.Zero);
                Assert.That(_queries, Is.Zero);
                Assert.That(_events, Is.Empty);
            });
        }

        [TestCaseSource(nameof(PreservedTrackCases))]
        public void scrub_must_preserve_existing_numbering_during_a_real_tag_change(string extension, int part, uint track, uint total)
        {
            _scrub = true;
            var file = Add(extension, track, total);
            file.Part = part;
            file.Edition.BookFiles = new List<BookFile> { file };
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            Assert.Multiple(() =>
            {
                Assert.That(audio.Tag.Track, Is.EqualTo(track));
                Assert.That(audio.Tag.TrackCount, Is.EqualTo(total));
                Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
                Assert.That(_events, Has.Count.EqualTo(1));
            });
        }

        [TestCase(".flac", 0u, 0u, 14u)]
        [TestCase(".ogg", 0u, 0u, 14u)]
        [TestCase(".opus", 0u, 0u, 14u)]
        [TestCase(".flac", 27u, 27u, 27u)]
        [TestCase(".ogg", 27u, 27u, 27u)]
        [TestCase(".opus", 27u, 27u, 27u)]
        [TestCase(".flac", 0u, 0u, uint.MaxValue)]
        [TestCase(".flac", 14u, 0u, 14u)]
        [TestCase(".flac", 0u, 14u, 14u)]
        public void totals_already_known_to_the_scanner_survive_scrub(string extension, uint genericTotal, uint genericDiscTotal, uint storedTotal)
        {
            _scrub = true;
            var file = Add(extension, track: 3, count: genericTotal);
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Disc = 2;
                audio.Tag.DiscCount = genericDiscTotal;
                var xiph = (TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph);
                xiph.SetField("TOTALTRACKS", storedTotal.ToString());
                xiph.SetField("TOTALDISCS", storedTotal.ToString());
                audio.Save();
            }

            Assert.That(_service.ReadAllTags(file.Path)["TOTALTRACKS"], Does.Contain(storedTotal.ToString()));
            Assert.That(_service.ReadAllTags(file.Path)["TOTALDISCS"], Does.Contain(storedTotal.ToString()));
            var seed = Path.Combine(_directory, "download" + extension);
            if (!_disk.TryCreateHardLink(file.Path, seed)) Assert.Ignore("Filesystem does not support hardlinks");
            var original = File.ReadAllBytes(seed);
            _service.WriteTags(file, false);
            using (var updated = TagLib.File.Create(file.Path))
            {
                var expected = genericTotal > 0 ? genericTotal : storedTotal;
                Assert.That((updated.Tag.Track, updated.Tag.TrackCount, updated.Tag.Disc, updated.Tag.DiscCount), Is.EqualTo((3u, expected, 2u, genericDiscTotal > 0 ? genericDiscTotal : storedTotal)));
                Assert.That(updated.Tag.Title, Is.EqualTo(file.Edition.Title));
            }
            Assert.That(File.ReadAllBytes(seed), Is.EqualTo(original));
            _service.WriteTags(file, false);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_guards, Is.EqualTo(1));
            Assert.That(_queries, Is.Zero);
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [Test]
        public void changing_new_files_to_no_must_not_retag_an_existing_file()
        {
            var file = Add(".flac", track: 3);
            var before = System.IO.File.ReadAllBytes(file.Path);
            var reads = 0;
            _readMode = () => reads++ == 0 ? WriteAudioTagsType.NewFiles : WriteAudioTagsType.No;

            _service.WriteTags(file, false);

            Assert.Multiple(() =>
            {
                Assert.That(System.IO.File.ReadAllBytes(file.Path), Is.EqualTo(before));
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(_guards, Is.Zero);
                Assert.That(_updates, Is.Zero);
                Assert.That(_events, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scrub_setting_is_consistent_when_changed_during_a_file_write(bool initial)
        {
            _scrub = initial;
            var file = Add(".flac", track: 3);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var xiph = (TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph);
                xiph.SetField("TOTALTRACKS", "14");
                xiph.SetField("TEST_MARKER", "keep unless scrubbed");
                audio.Save();
            }
            _prepare = _ => { _scrub = !initial; return false; };
            _service.WriteTags(file, false);
            using var updated = TagLib.File.Create(file.Path);
            var tags = (TagLib.Ogg.XiphComment)updated.GetTag(TagLib.TagTypes.Xiph);
            Assert.Multiple(() =>
            {
                Assert.That(_service.ReadAllTags(file.Path)["TOTALTRACKS"], Does.Contain("14"));
                Assert.That(tags.GetField("TEST_MARKER"), Has.Length.EqualTo(initial ? 0 : 1));
                Assert.That(_scrubReads, Is.EqualTo(1));
                Assert.That(_events, Has.Count.EqualTo(1));
                Assert.That(_events.Single().Scrubbed, Is.EqualTo(initial));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unchanged_conflicting_totals_are_a_noop(bool preview)
        {
            var file = Add(".flac", track: 3);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).SetField("TOTALTRACKS", new[] { "14", "15" });
                audio.Save();
            }
            _scrub = true;
            var original = File.ReadAllBytes(file.Path);
            if (preview) Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            else
            {
                var batch = RetagFiles(new RetagFilesCommand { Files = new List<int> { file.Id } });
                Assert.That((batch.Skipped, batch.Failed, batch.Written), Is.EqualTo((1, 0, 0)));
            }
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(original));
            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [Test]
        public void changed_conflicting_totals_remain_visible_in_preview()
        {
            _scrub = true;
            var file = Add(".flac", track: 3);
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).SetField("TOTALTRACKS", new[] { "14", "15" });
                audio.Save();
            }
            Assert.That(_service.GetRetagPreviewsByBook(2), Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_events, Is.Empty);
        }

        [TestCase("014")]
        [TestCase("14")]
        public void zero_generic_total_does_not_hide_a_stored_total(string stored)
        {
            _scrub = true;
            var file = Add(".flac", track: 3);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var xiph = (TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph);
                xiph.SetField("TRACKTOTAL", "0");
                xiph.SetField("TOTALTRACKS", new[] { stored, "14" });
                audio.Save();
            }
            var command = new RetagFilesCommand { Files = new List<int> { file.Id } };
            var logger = _logs.GetLogger("audio");
            var localizer = new LocalizationService(Proxy<IConfigService>((_, _) => 1),
                Proxy<NzbDrone.Common.EnvironmentInfo.IAppFolderInfo>((_, _) => TestContext.CurrentContext.TestDirectory), new NzbDrone.Common.Cache.CacheManager(), logger);
            TagServiceTestFactory.Metadata(_service, Proxy<IEBookTagService>((_, _) => new TagBatch()), logger, localizer, media: _media).Execute(command);
            using var updated = TagLib.File.Create(file.Path);
            Assert.That(updated.Tag.TrackCount, Is.EqualTo(14));
            Assert.That(command.CompletionMessage, Does.Contain("stored totals preserved: 1"));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase("TOTALTRACKS", true)]
        [TestCase("TOTALDISCS", true)]
        [TestCase("TOTALTRACKS", false)]
        [TestCase("TOTALDISCS", false)]
        public void conflicting_stored_totals_must_not_be_erased_or_guessed(string field, bool scrub)
        {
            _scrub = scrub;
            var file = Add(".flac", track: 3);
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).SetField(field, new[] { "14", "15" });
                audio.Save();
            }
            var original = File.ReadAllBytes(file.Path);
            _service.WriteTags(file, false);
            if (scrub)
            {
                Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(original));
                Assert.That(_events, Is.Empty);
                Assert.That(_guards, Is.Zero);
                Assert.That(_warnings.Logs.Single(line => line.Contains("scrub audio tags")), Does.StartWith("Warn:").And.Contain("scrub audio tags").And.Contain(field + " 14 vs 15"));
            }
            else
            {
                using var updated = TagLib.File.Create(file.Path);
                Assert.That(((TagLib.Ogg.XiphComment)updated.GetTag(TagLib.TagTypes.Xiph)).GetField(field), Is.EqualTo(new[] { "14", "15" }));
                Assert.That(_service.ReadAllTags(file.Path)["PUBLISHER"], Does.Contain(file.Edition.Publisher));
                Assert.That(_events, Has.Count.EqualTo(1));
                Assert.That(_warnings.Logs, Is.Empty);
            }
        }

        [TestCase(".flac")]
        [TestCase(".ogg")]
        [TestCase(".opus")]
        public void alternate_totals_do_not_force_a_write_and_remain_when_scrub_is_off(string extension)
        {
            var file = Add(extension, track: 3);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var xiph = (TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph);
                xiph.SetField("TOTALTRACKS", "14");
                xiph.SetField("TOTALDISCS", "7");
                audio.Save();
            }
            var original = File.ReadAllBytes(file.Path);
            Assert.That(_service.GetRetagPreviewsByBook(2), Is.Empty);
            _service.WriteTags(file, false);
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(original));
            file.Edition.Publisher = "Changed publisher";
            _service.WriteTags(file, false);
            var tags = _service.ReadAllTags(file.Path);
            Assert.That(tags["TOTALTRACKS"], Is.EqualTo(new[] { "14" }));
            Assert.That(tags["TOTALDISCS"], Is.EqualTo(new[] { "7" }));
            Assert.That(_events, Has.Count.EqualTo(2));
        }

        [TestCaseSource(nameof(TagFormats))]
        public void raw_extraction_is_identical_with_and_without_duration(string extension)
        {
            var file = Add(extension, track: 3, count: 14);
            var extractor = new TagLibSharpExtractor();
            var tags = extractor.ExtractTags(file.Path);
            var withDuration = extractor.ExtractTagsAndDuration(file.Path).Tags;
            Assert.That(withDuration.Keys, Is.EquivalentTo(tags.Keys));
            foreach (var key in tags.Keys) Assert.That(withDuration[key], Is.EqualTo(tags[key]), key);
        }

        [TestCase(4, 0u, 4u)]
        [TestCase(4, 10u, 10u)]
        [TestCase(0, 0u, 0u)]
        public void part_only_fills_a_missing_track_without_inventing_a_total(int part, uint existing, uint expected)
        {
            var file = Add(track: existing);
            file.Part = part;
            file.Edition.BookFiles = new List<BookFile> { file };
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            Assert.That(audio.Tag.Track, Is.EqualTo(expected));
            Assert.That(audio.Tag.TrackCount, Is.Zero);
        }

        [TestCaseSource(nameof(FormatCases))]
        public void part_fills_missing_track_and_preserves_existing_total(string extension, bool scrub)
        {
            _scrub = scrub;
            var file = Add(extension, count: 27);
            file.Part = 4;
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            Assert.That((audio.Tag.Track, audio.Tag.TrackCount), Is.EqualTo((4u, 27u)));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCaseSource(nameof(FormatCases))]
        public void disc_number_and_total_survive_retag_with_disc_relative_tracks(string extension, bool scrub)
        {
            _scrub = scrub;
            foreach (var disc in new[] { 1u, 2u })
            {
                var file = Add(extension, track: 2, count: 6);
                file.Part = disc == 1 ? 7 : 8;
                using (var audio = TagLib.File.Create(file.Path))
                {
                    audio.Tag.Disc = disc;
                    audio.Tag.DiscCount = 7;
                    audio.Save();
                }

                _service.WriteTags(file, false);
                using var result = TagLib.File.Create(file.Path);
                Assert.That((result.Tag.Track, result.Tag.TrackCount, result.Tag.Disc, result.Tag.DiscCount),
                    Is.EqualTo((2u, 6u, disc, 7u)));
            }

            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_events, Has.Count.EqualTo(2));
        }

        [Test]
        public void persisted_tag_bytes_must_not_be_reported_as_a_skipped_file_when_statistics_save_fails()
        {
            var file = Add();
            _beforeUpdate = () => throw new IOException("fixture statistics write failed");
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            Assert.That(audio.Tag.Title, Is.EqualTo(file.Edition.Title));
            Assert.That(_warnings.Logs.Single(line => line.Contains("persist file statistics")), Does.Contain("persist file statistics").And.Not.Contain("skipping file"));
            Assert.That(_events, Is.Empty);
        }

        [TestCase("Preview")]
        [TestCase("Sync")]
        [TestCase("RetagFiles")]
        [TestCase("RetagAuthor")]
        public void unreadable_file_should_not_stop_the_batch(string route)
        {
            var bad = Add();
            var good = Add();
            File.WriteAllText(bad.Path, "invalid audio");
            if (route == "Preview")
            {
                var rows = _service.GetRetagPreviewsByBook(2);
                Assert.That(rows.Select(p => p.Path), Is.EquivalentTo(new[] { bad.Path, good.Path }));
                Assert.That(rows.Single(p => p.Path == bad.Path).SkipReason, Is.EqualTo(RetagSkipReason.PreviewFailed));
                Assert.That(rows.Single(p => p.Path == good.Path).SkipReason, Is.Null);
            }
            else if (route == "Sync") _service.SyncTags(new List<Edition> { bad.Edition });
            else if (route == "RetagFiles") RetagFiles(new RetagFilesCommand { Files = _files.Select(f => f.Id).ToList() });
            else RetagAuthors(new RetagAuthorCommand { AuthorIds = new List<int> { 1 } });
            Assert.That(_queries, Is.EqualTo(route == "Sync" ? 1 : 0));
            Assert.That(_warnings.Logs, Has.Count.EqualTo(route == "Sync" ? 2 : 1));
            Assert.That(_warnings.Logs[0], Does.Contain(bad.Path).And.Contain(route == "Preview" ? "preview audio tags" : "read audio tags"));
            if (route == "Sync") Assert.That(_warnings.Logs[1], Does.Match(@"^Warn: Audio tags Sync: 1 read, 0 skipped, 1 failed, 0 separated, 1 written in \d+ ms, 0 stored totals preserved through scrub, 0 incomplete$"));
            Assert.That(_guards, Is.EqualTo(route == "Preview" ? 0 : 1));
            Assert.That(_events.Select(e => e.BookFile.Path), Is.EqualTo(route == "Preview" ? Array.Empty<string>() : new[] { good.Path }));
        }

        [TestCase(false, "Sync")]
        [TestCase(true, "Sync")]
        [TestCase(false, "RetagFiles")]
        [TestCase(true, "RetagAuthor")]
        public void file_symlink_must_skip_at_debug_preserve_target_and_continue_batch(bool hardlinkedTarget, string route)
        {
            var bad = Add();
            var target = bad.Path;
            var original = File.ReadAllBytes(target);
            if (hardlinkedTarget) Assert.That(_disk.TryCreateHardLink(target, Path.Combine(_directory, "download.mp3")), Is.True);
            bad.Path = Path.Combine(_directory, "linked.mp3");
            SymbolicLinkTest.CreateFile(bad.Path, target);
            var good = Add();
            TagBatch batch = null;
            if (route == "Sync") _service.SyncTags(new List<Edition> { bad.Edition });
            else if (route == "RetagFiles") batch = RetagFiles(new RetagFilesCommand { Files = _files.Select(f => f.Id).ToList() });
            else batch = RetagAuthors(new RetagAuthorCommand { AuthorIds = new List<int> { 1 } });
            Assert.That(File.ReadAllBytes(target), Is.EqualTo(original));
            Assert.That(new FileInfo(bad.Path).LinkTarget, Is.EqualTo(target));
            Assert.That(_disk.GetFileLinkCount(target), Is.EqualTo(hardlinkedTarget ? 2 : 1));
            Assert.That(_events.Select(e => e.BookFile.Path), Is.EqualTo(new[] { good.Path }));
            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_allLogs.Logs, Does.Contain($"Debug: Skipping tag writes through file symlink '{bad.Path}'."));
            if (route == "Sync") Assert.That(_allLogs.Logs, Has.None.Contains("Audio tags Sync:")); // absent-literal: LogBatch formats the operation into the message at runtime.
            else Assert.That((batch.SkippedSymlinks, batch.Failed, batch.Separated, batch.Written), Is.EqualTo((1, 0, 0, 1)));
            Assert.That(_allLogs.Logs, Has.None.Contains("Separated hardlinked file"));
            Assert.That(Directory.GetFiles(_directory, "*.chaptarr-tmp"), Is.Empty);
        }

        [TestCase("Sync")]
        [TestCase("RetagFiles")]
        [TestCase("RetagAuthor")]
        public void linked_audio_remains_in_preview_and_only_commands_report_the_localized_skip_count(string route)
        {
            var file = Add();
            var target = file.Path;
            file.Path = Path.Combine(_directory, "preview-link.mp3");
            SymbolicLinkTest.CreateFile(file.Path, target);
            var regular = Add();
            var logger = _logs.GetLogger("audio");
            var ebook = Proxy<IEBookTagService>((_, _) => new TagBatch());
            var localizer = Proxy<ILocalizationService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo("GetLocalizedString"));
                if ((string)args[0] != "RetagSkippedSymlinkWrites") return (string)args[0];
                Assert.That(((Dictionary<string, object>)args[1])["count"], Is.EqualTo(1));
                return "Localized message for one skipped write";
            });
            var service = TagServiceTestFactory.Metadata(_service, ebook, logger, localizer, media: _media, authors: Proxy<IAuthorService>((_, _) => _files.Select(f => f.Author).DistinctBy(a => a.Id).ToList()));
            var preview = new RetagBookController(service).GetBooks(null, 2, "audiobook");
            Assert.That(preview.Select(row => row.BookFileId), Is.EquivalentTo(new[] { file.Id, regular.Id }));
            Assert.That(preview.Select(row => row.BookId), Is.All.EqualTo(file.Edition.BookId), "Book identity must not be replaced with the edition ID");
            Assert.That(preview.Single(row => row.BookFileId == file.Id).IsFileSymlink, Is.True);
            Assert.That(preview.Single(row => row.BookFileId == file.Id).IsCalibreFormat, Is.False);
            Assert.That(preview.Single(row => row.BookFileId == regular.Id).IsFileSymlink, Is.False);

            _files.Remove(regular);
            file.Edition.BookFiles = null;
            if (route == "Sync")
            {
                _service.SyncTags(new List<Edition> { file.Edition });
                Assert.That(_allLogs.Logs, Has.None.Contains("Localized message")); // absent-literal: Test localizer sentinel must not leak into automatic Sync logs.
                Assert.That(_allLogs.Logs, Has.None.Contains("Audio tags Sync:")); // absent-literal: LogBatch formats the operation into the message at runtime.
            }
            else
            {
                if (route == "RetagFiles") service.Execute(new RetagFilesCommand { Files = new List<int> { file.Id } });
                else service.Execute(new RetagAuthorCommand { AuthorIds = new List<int> { 1 }, MediaType = "audiobook" });
                Assert.That(_allLogs.Logs.Last(), Does.EndWith("Localized message for one skipped write"));
                Assert.That(_allLogs.Logs.Count(line => line.Contains("Localized message")), Is.EqualTo(1));
            }

            Assert.That(_warnings.Logs, Is.Empty);
            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void audio_in_a_symlinked_library_directory_must_still_be_tagged()
        {
            var file = Add();
            var originalPath = file.Path;
            var linked = Path.Combine(_directory, "linked-library");
            SymbolicLinkTest.CreateDirectory(linked, _directory);
            file.Path = Path.Combine(linked, Path.GetFileName(originalPath));
            _service.WriteTags(file, false);
            Assert.That(_events.Select(e => e.BookFile.Path), Is.EqualTo(new[] { file.Path }));
            using var audio = TagLib.File.Create(originalPath);
            Assert.That(audio.Tag.Album, Is.EqualTo("Fixture book"));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [Test]
        public void failed_separation_should_skip_one_file_and_continue()
        {
            var bad = Add();
            var good = Add();
            _prepare = path => path == bad.Path ? throw new IOException("disk full") : false;
            RetagAuthors(new RetagAuthorCommand { AuthorIds = new List<int> { 1 } });
            Assert.That(_events.Select(e => e.BookFile.Path), Is.EqualTo(new[] { good.Path }));
            Assert.That(_warnings.Logs.Single(), Does.Contain(bad.Path));
        }

        [Test]
        public void mp4_publisher_prefers_the_field_we_write_but_keeps_legacy_evidence()
        {
            var file = Add(".m4b");
            using (var audio = TagLib.File.Create(file.Path))
            {
                var apple = (TagLib.Mpeg4.AppleTag)audio.GetTag(TagLib.TagTypes.Apple);
                apple.SetText(new TagLib.ByteVector(System.Text.Encoding.Latin1.GetBytes("©pub")), "Legacy publisher");
                audio.Save();
            }
            Assert.That(_service.ReadAllTags(file.Path)["PUBLISHER"], Is.EqualTo(new[] { "Legacy publisher" }));
            _service.WriteTags(file, false);
            var tags = _service.ReadAllTags(file.Path);
            Assert.That(tags["PUBLISHER"], Is.EqualTo(new[] { file.Edition.Publisher }));
            Assert.That(tags["MP4:©pub"], Is.EqualTo(new[] { "Legacy publisher" }));
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCase("ORGANIZATION")]
        [TestCase("LABEL")]
        public void legacy_vorbis_publisher_is_read_but_upgraded_to_the_written_field_once(string field)
        {
            var file = Add(".flac");
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Ogg.XiphComment)audio.GetTag(TagLib.TagTypes.Xiph)).SetField(field, "Legacy publisher");
                audio.Save();
            }
            Assert.That(_service.ReadAllTags(file.Path)["PUBLISHER"], Is.EqualTo(new[] { "Legacy publisher" }));
            _service.WriteTags(file, false);
            _service.WriteTags(file, false);
            var tags = _service.ReadAllTags(file.Path);
            Assert.That(tags["XIPH:PUBLISHER"], Is.EqualTo(new[] { file.Edition.Publisher }));
            Assert.That(tags["XIPH:" + field], Is.EqualTo(new[] { "Legacy publisher" }));
            Assert.That(tags["PUBLISHER"], Is.EqualTo(new[] { file.Edition.Publisher }));
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [Test]
        public void unknown_file_count_must_not_erase_an_existing_total()
        {
            var file = Add(track: 3, count: 10);
            file.Edition.BookFiles = new List<BookFile>();
            _scrub = true;
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            Assert.That(audio.Tag.Track, Is.EqualTo(3));
            Assert.That(audio.Tag.TrackCount, Is.EqualTo(10));
        }

        [TestCase(1)]
        [TestCase(42)]
        [TestCase(999)]
        public void wma_small_years_should_compare_the_complete_stored_year(int year)
        {
            var file = Add(".wma");
            file.Edition.ReleaseDate = new DateTime(year, 1, 1);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(((TagLib.Asf.Tag)audio.GetTag(TagLib.TagTypes.Asf)).GetDescriptorString("WM/Year"), Is.EqualTo(year.ToString()));
            }
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [Test]
        public void mp3_publisher_must_update_id3_even_when_an_ape_tag_is_also_present()
        {
            var file = Add();
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Id3v2.Tag)audio.GetTag(TagLib.TagTypes.Id3v2, true)).Publisher = "Old ID3 publisher";
                ((TagLib.Ape.Tag)audio.GetTag(TagLib.TagTypes.Ape, true)).SetValue("Publisher", "Old APE publisher");
                audio.Save();
            }
            _service.WriteTags(file, false);
            Assert.That(_service.ReadAllTags(file.Path)["PUBLISHER"], Is.EqualTo(new[] { file.Edition.Publisher }));
            Assert.That(_service.ReadAllTags(file.Path)["APE:Publisher"], Is.EqualTo(new[] { "Old APE publisher" }));
            using var updated = TagLib.File.Create(file.Path);
            Assert.That(((TagLib.Id3v2.Tag)updated.GetTag(TagLib.TagTypes.Id3v2)).Publisher, Is.EqualTo(file.Edition.Publisher));
        }

        [Test]
        public void narrator_values_must_not_be_split_again_after_normalization()
        {
            var file = Add(".flac");
            file.Edition.NarratorNames = new List<string> { " First; narrator ", "", "Second narrator" };
            _service.WriteTags(file, false);
            using var audio = TagLib.File.Create(file.Path);
            Assert.That(audio.Tag.Composers, Is.EqualTo(new[] { "First; narrator", "Second narrator" }));
        }

        [Test]
        public void id3v23_tags_must_preserve_the_tag_version_and_converge()
        {
            var file = Add();
            file.Author.Name = "Author A";
            file.Edition.NarratorNames = new List<string> { "Voice A", "Voice B" };
            using (var audio = TagLib.File.Create(file.Path))
            {
                ((TagLib.Id3v2.Tag)audio.GetTag(TagLib.TagTypes.Id3v2, true)).Version = 3;
                audio.Save();
            }
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                Assert.That(((TagLib.Id3v2.Tag)audio.GetTag(TagLib.TagTypes.Id3v2)).Version, Is.EqualTo(3));
                Assert.That(audio.Tag.Performers, Is.EqualTo(new[] { "Author A" }));
                Assert.That(audio.Tag.Composers, Is.EqualTo(new[] { "Voice A", "Voice B" }));
            }
            _service.WriteTags(file, false);
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [Test]
        public void changing_publisher_must_preserve_full_date_and_untouched_frames()
        {
            var file = Add();
            file.Edition.ReleaseDate = new DateTime(2021, 1, 1);
            _service.WriteTags(file, false);
            using (var audio = TagLib.File.Create(file.Path))
            {
                var id3 = (TagLib.Id3v2.Tag)audio.GetTag(TagLib.TagTypes.Id3v2);
                TagLib.Id3v2.TextInformationFrame.Get(id3, "TDRC", true).Text = new[] { "2021-05-04" };
                audio.Save();
            }

            file.Edition.Publisher = "Changed publisher";
            _service.WriteTags(file, false);
            Assert.That(_events.Last().Diff.Keys, Is.EquivalentTo(new[] { "PUBLISHER" }));
            using var updated = TagLib.File.Create(file.Path);
            Assert.That(TagLib.Id3v2.TextInformationFrame.Get((TagLib.Id3v2.Tag)updated.GetTag(TagLib.TagTypes.Id3v2), "TDRC", false).Text, Is.EqualTo(new[] { "2021-05-04" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void scrub_must_attempt_one_complete_save(bool fail)
        {
            var file = Add();
            using (var audio = TagLib.File.Create(file.Path))
            {
                audio.Tag.Title = "Old title";
                audio.Tag.Comment = "Remove me";
                audio.Save();
            }

            var original = File.ReadAllBytes(file.Path);
            var saves = new List<(string Title, string Comment)>();
            _instrumentedPath = file.Path;
            _beforeSave = audio =>
            {
                saves.Add((audio.Tag.Title, audio.Tag.Comment));
                if (fail) throw new IOException("fixture save failure");
            };
            _scrub = true;
            _service.WriteTags(file, false);
            Assert.That(saves, Has.Count.EqualTo(1));
            Assert.That(saves[0].Title, Is.EqualTo(file.Edition.Title), "Never save the stripped intermediate state");
            Assert.That(saves[0].Comment, Is.Null.Or.Empty);
            Assert.That(_events.Count, Is.EqualTo(fail ? 0 : 1));
            if (fail) Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(original));
        }

        [Test]
        public void readable_but_possibly_corrupt_file_must_not_be_mutated()
        {
            var file = Add(".m4b");
            using (var stream = File.Open(file.Path, FileMode.Append))
            {
                stream.Write(new byte[] { 0, 0, 0, 4, (byte)'b', (byte)'a', (byte)'d', (byte)'!' });
            }
            using (var audio = TagLib.File.Create(file.Path)) Assert.That(audio.PossiblyCorrupt, Is.True);
            _service.WriteTags(file, false);
            Assert.That(_guards, Is.Zero);
            Assert.That(_events, Is.Empty);
            Assert.That(_warnings.Logs, Has.Some.StartsWith("Warn:").And.Some.Contains(file.Path));
            Assert.That(_allLogs.Logs.Count(l => l.Contains("Audio tags Import:")), Is.EqualTo(1));
        }

        [Test]
        public void no_work_sync_must_not_log_a_summary()
        {
            var file = Add();
            _service.SyncTags(new List<Edition> { file.Edition });
            Assert.That(_allLogs.Logs, Has.None.Contains("Audio tags Sync:")); // absent-literal: LogBatch formats the operation into the message at runtime.
            var bytes = File.ReadAllBytes(file.Path);
            _allLogs.Logs.Clear();
            _service.SyncTags(new List<Edition> { file.Edition });
            Assert.That(_allLogs.Logs, Has.None.Contains("Audio tags Sync:")); // absent-literal: LogBatch formats the operation into the message at runtime.
            Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes));
            Assert.That(_events, Has.Count.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void each_audio_file_has_one_outcome_even_when_post_save_work_fails(bool database)
        {
            var bad = Add();
            Add();
            if (database) _beforeUpdate = () => throw new InvalidOperationException("fixture database failure");
            else _prepare = path => path == bad.Path ? throw new InvalidOperationException("fixture programming error") : false;
            var batch = RetagFiles(new RetagFilesCommand { Files = _files.Select(f => f.Id).ToList() });
            Assert.That(_warnings.Logs[0], Does.StartWith("Error:").And.Contain(bad.Path));
            Assert.That(_warnings.Logs[0], Does.Contain(database ? "persist file statistics" : "update audio tags"));
            Assert.That(batch.Written, Is.EqualTo(database ? 0 : 1));
            Assert.That(batch.Incomplete, Is.EqualTo(database ? 2 : 0));
            Assert.That(batch.Failed, Is.EqualTo(database ? 0 : 1));
            Assert.That(batch.Written + batch.Incomplete + batch.Failed, Is.EqualTo(2));
        }

        [Test]
        public void author_retag_must_finish_one_author_before_loading_the_next()
        {
            var first = Add();
            var second = Add();
            second.Author.Id = 2;
            second.Author.Name = "Second author";
            second.Edition.Id = 4;
            second.EditionId = 4;
            second.Edition.BookId = 5;
            _beforeAuthorRead = id => { if (id == 2) Assert.That(_events.Select(e => e.BookFile), Is.EqualTo(new[] { first })); };
            RetagAuthors(new RetagAuthorCommand { AuthorIds = new List<int> { 1, 2 } });
            Assert.That(_events, Has.Count.EqualTo(2));
            Assert.That(_allLogs.Logs.Count(l => l.Contains("Audio tags Retag authors:")), Is.Zero);
            Assert.That(_allLogs.Logs.Any(l => l.Contains("Second author")), Is.True);
        }

        [Test]
        public void audio_preview_does_not_load_any_edition_file_counts()
        {
            Add();
            var ebook = Add();
            ebook.Path = Path.ChangeExtension(ebook.Path, ".epub");
            ebook.Edition.BookId = 99;
            _service.GetRetagPreviewsByAuthor(1);
            Assert.That(_queries, Is.Zero);
        }

        [TestCase(".flac")]
        [TestCase(".ogg")]
        [TestCase(".opus")]
        public void vorbis_written_tracktotal_must_be_visible_to_the_importer(string extension)
        {
            var file = Add(extension, track: 3, count: 12);
            var tags = _service.ReadAllTags(file.Path);
            Assert.That(tags["TOTALTRACKS"], Is.EqualTo(new[] { "12" }));
            Assert.That(tags["XIPH:TRACKTOTAL"], Is.EqualTo(new[] { "12" }));
        }

        [TestCase(WriteAudioTagsType.No, 0)]
        [TestCase(WriteAudioTagsType.NewFiles, 40)]
        public void committed_batch_must_not_load_file_counts(WriteAudioTagsType mode, int written)
        {
            _mode = mode;
            for (var i = 0; i < 40; i++) Add(track: (uint)i + 1, count: 45);
            Assert.That(TagCommittedFiles(), Is.EqualTo(40));
            Assert.That(_queries, Is.Zero);
            Assert.That(_events, Has.Count.EqualTo(written));
            foreach (var file in _files)
            {
                using var audio = TagLib.File.Create(file.Path);
                Assert.That(audio.Tag.TrackCount, Is.EqualTo(45));
                Assert.That(file.Edition.BookFiles, Is.Null);
            }
            Assert.That(_warnings.Logs, Is.Empty);
        }

        [TestCase(false, 1u, 7u)]
        [TestCase(true, 1u, 7u)]
        [TestCase(true, 0u, 7u)]
        [TestCase(true, 0u, 0u)]
        public void committed_upgrade_preserves_numbering_without_reloading_stale_file_lists(bool scrub, uint track, uint total)
        {
            _mode = WriteAudioTagsType.NewFiles;
            _scrub = scrub;
            var upgraded = Add(".m4b", track: track, count: total);
            upgraded.Edition.BookFiles = Enumerable.Range(1, 12)
                .Select(part => new BookFile { Part = part, EditionId = upgraded.EditionId }).ToList();
            var stale = upgraded.Edition.BookFiles;
            Assert.That(TagCommittedFiles(), Is.EqualTo(1));
            using var audio = TagLib.File.Create(upgraded.Path);
            Assert.That(audio.Tag.Track, Is.EqualTo(track));
            Assert.That(audio.Tag.TrackCount, Is.EqualTo(total));
            Assert.That(audio.Tag.Title, Is.EqualTo(upgraded.Edition.Title));
            Assert.That(_queries, Is.Zero);
            Assert.That(upgraded.Edition.BookFiles, Is.SameAs(stale));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_warnings.Logs, Is.Empty);
        }

        private int TagCommittedFiles()
        {
            var added = 0;
            var type = typeof(NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks);
            var commitType = type.GetNestedType("PendingFileCommit", System.Reflection.BindingFlags.NonPublic);
            var commits = Array.CreateInstance(commitType, _files.Count);
            for (var i = 0; i < _files.Count; i++)
            {
                var commit = Activator.CreateInstance(commitType, true);
                commitType.GetProperty("BookFile").SetValue(commit, _files[i]);
                commitType.GetProperty("DatabaseCommitted").SetValue(commit, true);
                commitType.GetProperty("NewDownload").SetValue(commit, true);
                commitType.GetProperty("PublishAddedEvent").SetValue(commit, true);
                commits.SetValue(commit, i);
            }

            var tags = Proxy<IMetadataTagService>((_, args) =>
            {
                var file = (BookFile)args[0];
                _service.WriteTags(file, (bool)args[1]);
                return null;
            });
            var importer = new NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks(_media, tags,
                null, null, null, null, null, null, null, null, null,
                Proxy<IEventAggregator>((_, _) => { added++; return null; }), null, null, null, null, null,
                _logs.GetLogger("import-fixture"), configService: _config);
            type.GetMethod("WriteTagsForCommittedFiles", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(importer, new object[] { commits });
            return added;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_save_should_not_publish_or_update_file_statistics(bool scrub)
        {
            _scrub = scrub;
            var file = Add();
            FileStream held = null;
            try
            {
                _prepare = path => { held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return false; };
                _service.WriteTags(file, false);
                Assert.That(_events, Is.Empty);
                Assert.That(_updates, Is.Zero);
                Assert.That(_warnings.Logs, Has.Count.EqualTo(2));
                Assert.That(_warnings.Logs[0], Does.Contain(file.Path));
                Assert.That(_warnings.Logs[1], Does.Contain("Audio tags Import:").And.Contain("1 failed"));
            }
            finally { held?.Dispose(); }
        }
    }
}
