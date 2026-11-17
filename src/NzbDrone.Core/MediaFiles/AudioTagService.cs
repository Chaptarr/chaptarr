using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NzbDrone.Core.Localization;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using TagLib;
using SixLabors.ImageSharp;

namespace NzbDrone.Core.MediaFiles
{
    public interface IAudioTagService
    {
        Dictionary<string, List<string>> ReadAllTags(string file);
        (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string file);
        void WriteTags(BookFile trackfile, bool newDownload, bool force = false);
        void SyncTags(List<Edition> tracks);
        List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId);
        List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId);
        TagBatch RetagFiles(RetagFilesCommand message, List<BookFile> files, TagBatch batch);
        TagBatch RetagAuthor(RetagAuthorCommand message, IReadOnlyList<Author> authors, TagBatch batch);
    }

    public class AudioTagService : IAudioTagService
    {
        private readonly IConfigService _configService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IFileMutationSafetyService _fileMutationSafetyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly Logger _logger;
        private readonly ILocalizationService _localizationService;
        private readonly TagExtraction.ITagExtractionService _tagExtractionService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly IMapCoversToLocal _coverService;

        public AudioTagService(IConfigService configService,
                               IMediaFileService mediaFileService,
                               IDiskProvider diskProvider,
                               IRootFolderWatchingService rootFolderWatchingService,
                               IFileMutationSafetyService fileMutationSafetyService,
                               IEventAggregator eventAggregator,
                               IMediaInfoExtractor mediaInfoExtractor,
                               Logger logger,
                               TagExtraction.ITagExtractionService tagExtractionService,
                               ISeriesBookLinkService seriesBookLinkService,
                               ILocalizationService localizationService,
                               IMapCoversToLocal coverService)
        {
            _configService = configService;
            _mediaFileService = mediaFileService;
            _diskProvider = diskProvider;
            _rootFolderWatchingService = rootFolderWatchingService;
            _fileMutationSafetyService = fileMutationSafetyService;
            _eventAggregator = eventAggregator;
            _mediaInfoExtractor = mediaInfoExtractor;
            _logger = logger;
            _tagExtractionService = tagExtractionService;
            _seriesBookLinkService = seriesBookLinkService;
            _localizationService = localizationService;
            _coverService = coverService;
        }

        public Dictionary<string, List<string>> ReadAllTags(string path)
        {
            // Route through the field-agnostic extraction service (TagLib# with FFprobe rescue).
            return _tagExtractionService.ExtractTags(path);
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string path)
        {
            var (tags, durationSeconds) = _tagExtractionService.ExtractTagsAndDuration(path);

            // Defensive fallback: if no duration was captured during tag extraction, compute it from media properties.
            if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
            {
                durationSeconds = MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(path));
            }

            return (tags ?? new Dictionary<string, List<string>>(), durationSeconds);
        }

        private Dictionary<string, string> BuildDesiredTagMap(BookFile trackfile, Dictionary<string, string> current, SeriesBookLink series, bool scrub, out string[] narrators)
        {
            narrators = trackfile?.Edition?.NarratorNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray() ?? Array.Empty<string>();
            if (trackfile?.Edition == null)
            {
                _logger.Warn($"Cannot build desired tags for file {trackfile?.Path} - no edition linked");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var edition = trackfile.Edition;
            var book = edition.Book;

            if (book == null)
            {
                _logger.Warn($"Cannot build desired tags for file {trackfile.Path} - no book linked to edition");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var author = book.Author;

            if (author == null)
            {
                _logger.Warn($"Cannot build desired tags for file {trackfile.Path} - no author linked to book");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(edition.Title)) desired["TITLE"] = edition.Title;
            if (!string.IsNullOrWhiteSpace(book.Title)) desired["ALBUM"] = book.Title;
            if (!string.IsNullOrWhiteSpace(author.Name)) desired["ARTIST"] = author.Name;

            // A confirmed header or unambiguous original membership supplies the
            // pair. Only non-scrub writes retain file series when authority is absent.
            desired["SERIES"] = series?.Series.Value.Title ?? (scrub ? string.Empty : current.GetValueOrDefault("SERIES")) ?? string.Empty;
            var storedSeries = current.GetValueOrDefault("SERIES");
            var changedSeries = series != null && !string.IsNullOrEmpty(storedSeries) &&
                !string.Equals(storedSeries, series.Series.Value.Title, StringComparison.OrdinalIgnoreCase);
            desired["SERIES-PART"] = !string.IsNullOrEmpty(series?.Position) ? series.Position : (scrub || changedSeries) ? string.Empty : current.GetValueOrDefault("SERIES-PART") ?? string.Empty;

            // Existing file numbering wins; Part only supplies a missing track.
            current.TryGetValue("TRACKNUMBER", out var storedTrack);
            uint.TryParse(storedTrack, out var track);
            if (track == 0 && trackfile.Part > 0)
            {
                track = (uint)trackfile.Part;
            }

            if (track > 0)
            {
                desired["TRACKNUMBER"] = track.ToString();
            }

            // Carry existing totals and disc numbering through scrub without inventing them.
            foreach (var key in new[] { "TRACKCOUNT", "DISCNUMBER", "DISCCOUNT" })
            {
                current.TryGetValue(key, out var stored);
                if (uint.TryParse(stored, out var number) && number > 0)
                {
                    desired[key] = stored;
                }
            }

            if (edition.ReleaseDate.HasValue)
            {
                desired["YEAR"] = edition.ReleaseDate.Value.Year.ToString();
            }

            if (!string.IsNullOrWhiteSpace(edition.Publisher))
            {
                desired["PUBLISHER"] = edition.Publisher;
            }

            var asin = BookEditionIdentity.GetTagAsin(edition);
            var isbn = BookEditionIdentity.GetTagIsbn(edition);
            if (!string.IsNullOrWhiteSpace(asin)) desired["ASIN"] = asin;
            if (!string.IsNullOrWhiteSpace(isbn)) desired["ISBN"] = isbn;

            if (narrators.Length > 0)
            {
                desired["COMPOSER"] = string.Join("; ", narrators);
            }

            return desired;
        }

        private void UpdateTrackfileSizeAndModified(BookFile trackfile, string path)
        {
            var fileInfo = _diskProvider.GetFileInfo(path);
            trackfile.Size = fileInfo.Length;
            trackfile.Modified = fileInfo.LastWriteTimeUtc;

            if (trackfile.Id > 0)
            {
                _mediaFileService.Update(trackfile);
            }
        }

        public void WriteTags(BookFile trackfile, bool newDownload, bool force = false)
        {
            var batch = new TagBatch();
            try
            {
                WriteTags(trackfile, newDownload, force, batch);
            }
            finally
            {
                batch.Timer.Stop();
                LogBatch("Import", batch);
            }
        }

        private void WriteTags(BookFile trackfile, bool newDownload, bool force, TagBatch batch)
        {
            var path = trackfile.Path;
            if (!force)
            {
                if (!ShouldWriteTags(_configService.WriteAudioTags, newDownload))
                {
                    batch.Skipped++;
                    return;
                }
            }

            var operation = "read audio tags";
            var saved = false;
            try
            {
                LoadSeries(new[] { trackfile.Edition?.Book });
                var scrub = _configService.ScrubAudioTags;
                var changes = ReadTagChanges(trackfile, scrub, batch);
                batch.Read++;
                if (!changes.WouldWrite)
                {
                    batch.Skipped++;
                    return;
                }

                if (changes.Conflict != null)
                {
                    operation = "scrub audio tags";
                    throw new IOException(changes.Conflict);
                }

                operation = "update audio tags";
                _rootFolderWatchingService.ReportFileSystemChangeBeginning(path);
                if (_fileMutationSafetyService.EnsureMutableFile(path))
                {
                    batch.Separated++;
                }

                var written = scrub ? changes.Desired : changes.Desired.Where(pair => changes.Diff.ContainsKey(pair.Key) ||
                    WritableFields.Any(field => field.Key == pair.Key && field.Incidental)).ToDictionary(pair => pair.Key, pair => pair.Value);
                WriteTagMap(path, written, changes.Narrators, scrub, changes.Cover);
                saved = true;
                batch.RestoredTotals += changes.RestoredTotals;
                operation = "persist file statistics after saving audio tags";
                UpdateTrackfileSizeAndModified(trackfile, path);
                operation = "publish the audio retag event";
                _eventAggregator.PublishEvent(new BookFileRetaggedEvent(trackfile.Author, trackfile, changes.Diff, scrub));
                batch.Written++;
            }
            catch (FileSymlinkMutationException ex)
            {
                batch.SkippedSymlinkPaths.Add(path);
                _logger.Debug("{0}", ex.Message);
            }
            catch (Exception ex)
            {
                LogFailure(ex, path, operation, batch, saved);
            }
        }

        private void LogFailure(Exception ex, string path, string operation, TagBatch batch, bool saved = false)
        {
            if (saved)
            {
                batch.AudioIncomplete++;
            }
            else
            {
                batch.Failed++;
            }
            if (ex is IOException or UnauthorizedAccessException or CorruptFileException or UnsupportedFormatException)
            {
                _logger.Warn(ex, "Unable to {0} for {1}", operation, path);
            }
            else
            {
                _logger.Error(ex, "Unexpected failure attempting to {0} for {1}", operation, path);
            }
        }

        private void LogComparisons(string operation, TagBatch batch)
        {
            _logger.Debug("Audio tag comparisons ({0}): {1} files in {2:F2} ms; {3} cover conversions in {4:F2} ms",
                operation, batch.AudioComparisons, batch.AudioComparisonMilliseconds, batch.AudioCoverConversions, batch.AudioCoverConversionMilliseconds);
        }

        private void LogBatch(string operation, TagBatch batch)
        {
            LogComparisons(operation, batch);
            if (!batch.HasProblems) return;
            _logger.Warn("Audio tags {0}: {1} read, {2} skipped, {3} failed, {4} separated, {5} written in {6} ms, {7} stored totals preserved through scrub, {8} incomplete",
                operation, batch.Read, batch.Skipped + batch.SkippedSymlinks + batch.Unsupported, batch.Failed, batch.Separated, batch.Written, batch.Timer.ElapsedMilliseconds, batch.RestoredTotals, batch.Incomplete);
        }

        private void RetagBatch(List<BookFile> files, TagBatch batch)
        {
            PreloadSeries(files.Select(file => file.Edition?.Book));
            foreach (var file in files)
            {
                if (!MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(file.Path)))
                {
                    batch.Unsupported++;
                    _logger.Debug("Audio tag writing is unsupported for {0}; skipping file", file.Path);
                    continue;
                }

                WriteTags(file, false, true, batch);
            }
        }

        public void SyncTags(List<Edition> editions)
        {
            if (_configService.WriteAudioTags != WriteAudioTagsType.Sync)
            {
                return;
            }

            var batch = new TagBatch();
            var hydrated = _mediaFileService.HydrateEditionBookFiles(editions).ToList();
            PreloadSeries(hydrated.Where(edition => edition.Book?.MediaType == BookMediaType.Audiobook &&
                (edition.BookFiles ?? new List<BookFile>()).Any(file => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(file.Path))))
                .Select(edition => edition.Book));
            foreach (var edition in hydrated)
            {
                foreach (var file in (edition.BookFiles ?? new List<BookFile>()).Where(x => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(x.Path))))
                {
                    file.Edition = edition;
                    WriteTags(file, false, false, batch);
                }
            }

            LogBatch("Sync", batch);
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId)
        {
            var files = RetagMediaScope.Select(_mediaFileService.GetFilesByAuthor(authorId), BookMediaType.Audiobook).ToList();

            return GetPreviews(files).ToList();
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId)
        {
            var files = RetagMediaScope.Select(_mediaFileService.GetFilesByBook(bookId), BookMediaType.Audiobook).ToList();

            return GetPreviews(files).ToList();
        }

        private IEnumerable<RetagBookFilePreview> GetPreviews(List<BookFile> files)
        {
            var batch = new TagBatch();
            var previews = new List<RetagBookFilePreview>();
            files = files.Where(x => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(x.Path)) && x.Edition != null).ToList();
            PreloadSeries(files.Select(file => file.Edition.Book));
            foreach (var file in files)
            {
                try
                {
                    var changes = ReadTagChanges(file, _configService.ScrubAudioTags, batch);
                    batch.Read++;
                    if (changes.WouldWrite)
                    {
                        if (changes.Conflict != null)
                        {
                            _logger.Info("Unable to preview audio scrub for {0}: {1}", file.Path, changes.Conflict);
                        }
                        previews.Add(new RetagBookFilePreview
                        {
                            AuthorId = file.Author.Id,
                            BookId = file.Edition.BookId,
                            BookFileId = file.Id,
                            Title = file.Edition.Title,
                            Path = file.Path,
                            IsFileSymlink = FileMutationSafetyService.IsFileSymlink(file.Path),
                            Changes = changes.Diff,
                            SkipReason = changes.Conflict != null ? RetagSkipReason.PreviewFailed : null
                        });
                    }
                    else
                    {
                        batch.Skipped++;
                    }
                }
                catch (Exception ex)
                {
                    LogFailure(ex, file.Path, "preview audio tags", batch);
                    previews.Add(new RetagBookFilePreview
                    {
                        AuthorId = file.Author?.Id ?? 0,
                        BookId = file.Edition.BookId,
                        BookFileId = file.Id,
                        Title = file.Edition.Title,
                        Path = file.Path,
                        SkipReason = RetagSkipReason.PreviewFailed
                    });
                }
            }

            LogComparisons("Preview", batch);
            return previews;
        }

        internal static bool ShouldWriteTags(WriteAudioTagsType mode, bool newDownload) =>
            mode != WriteAudioTagsType.No && (mode != WriteAudioTagsType.NewFiles || newDownload);

        private void LoadSeries(IEnumerable<Book> books) => LoadSeries(books, _seriesBookLinkService, _logger);

        private void PreloadSeries(IEnumerable<Book> books)
        {
            try
            {
                LoadSeries(books);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to preload audio series memberships; individual files will retry the read");
            }
        }

        internal static void LoadSeries(IEnumerable<Book> books, ISeriesBookLinkService service, Logger logger)
        {
            BookSeries.LoadLinks(books, ids =>
            {
                var timer = Stopwatch.StartNew();
                var links = service.GetLinksByBook(ids);
                logger.Debug("Audio tags loaded {0} series memberships for {1} books in {2} ms", links.Count, ids.Count, timer.ElapsedMilliseconds);
                return links;
            });
        }

        public TagBatch RetagFiles(RetagFilesCommand message, List<BookFile> files, TagBatch batch)
        {
            files = RetagMediaScope.Select(files, BookMediaType.Audiobook, batch).ToList();
            _logger.ProgressInfo("Re-tagging {0} audio files", files.Count);
            RetagBatch(files, batch);
            LogComparisons("Retag files", batch);
            return batch;
        }

        public TagBatch RetagAuthor(RetagAuthorCommand message, IReadOnlyList<Author> authors, TagBatch batch)
        {
            RetagMediaScope.ForEachAuthor(authors, _mediaFileService, BookMediaType.Audiobook, batch, files =>
            {
                _logger.ProgressInfo("Re-tagging {0} audio files for {1}", files.Count, batch.CurrentAuthor);
                RetagBatch(files, batch);
            });

            LogComparisons("Retag authors", batch);
            return batch;
        }

        // Both comparisons and writes use the same fields; file values are comparison evidence,
        // while desired metadata comes from the linked edition, book and author.
        private sealed record WritableField(string Key, Func<TagLib.File, string> Read, Action<TagLib.File, string, string[]> Write, bool Incidental = false);

        private static readonly WritableField[] WritableFields =
        {
            new("TITLE", f => ReadText(f, "©nam", f.Tag.Title), (f, v, _) => f.Tag.Title = v),
            new("ALBUM", f => ReadText(f, "©alb", f.Tag.Album), (f, v, _) => f.Tag.Album = v),
            new("SERIES", f => ReadSeries(f, "SERIES"), (f, v, _) => WriteSeries(f, "SERIES", v)),
            new("SERIES-PART", f => ReadSeries(f, "SERIES-PART"), (f, v, _) => WriteSeries(f, "SERIES-PART", v)),
            new("ARTIST", f => ReadText(f, "©ART", string.Join("; ", f.Tag.Performers)), (f, v, _) => f.Tag.Performers = new[] { v }),
            // ID3v2.3 treats a slash as a list separator; preserve existing tag versions.
            new("COMPOSER", f => ReadText(f, "©wrt", string.Join("; ", f.Tag.Composers)), (f, v, narrators) => f.Tag.Composers = narrators),
            new("TRACKNUMBER", f => f.Tag.Track.ToString(), (f, v, _) => f.Tag.Track = uint.Parse(v)),
            new("TRACKCOUNT", f => f.Tag.TrackCount.ToString(), (f, v, _) => f.Tag.TrackCount = uint.Parse(v)),
            new("DISCNUMBER", f => f.Tag.Disc.ToString(), (f, v, _) => f.Tag.Disc = uint.Parse(v)),
            new("DISCCOUNT", f => f.Tag.DiscCount.ToString(), (f, v, _) => f.Tag.DiscCount = uint.Parse(v)),
            new("YEAR", ReadYear, (f, v, _) => f.Tag.Year = uint.Parse(v)),
            new("PUBLISHER", ReadPublisher, (f, v, _) => WritePublisher(f, v)),
            new("ASIN", f => ReadIdentity(f, "ASIN"), (f, v, _) => WriteIdentity(f, "ASIN", v), Incidental: true),
            new("ISBN", f => ReadIdentity(f, "ISBN"), (f, v, _) => WriteIdentity(f, "ISBN", v), Incidental: true)
        };

        private static readonly TagTypes[] TagPriority = { TagTypes.FlacMetadata, TagTypes.Xiph, TagTypes.Apple, TagTypes.Asf, TagTypes.Id3v2, TagTypes.Ape };

        private static IEnumerable<Tag> NativeTags(TagLib.File file, bool pictures = false) =>
            TagPriority.Where(type => pictures || type != TagTypes.FlacMetadata)
                .Select(type => file.GetTag(type)).Where(tag => tag != null).Distinct();

        private static Tag PrimaryTag(TagLib.File file, bool pictures = false) =>
            NativeTags(file, pictures).FirstOrDefault() ?? throw new NotSupportedException("No writable audio tag container");

        private static IEnumerable<(string Key, string Value)> CustomTextFields(Tag tag) => tag switch
        {
            TagLib.Ogg.XiphComment xiph => xiph.SelectMany(key => xiph.GetField(key).Select(value => (key, value))),
            TagLib.Mpeg4.AppleTag apple => TagExtraction.AppleTagFields.Read(apple).Select(field => (field.Mean + ":" + field.Name, field.Value)),
            TagLib.Asf.Tag asf => asf.Select(field => (field.Name, field.ToString())),
            TagLib.Id3v2.Tag id3 => id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().SelectMany(frame => frame.Text.Select(value => (frame.Description, value))),
            TagLib.Ape.Tag ape => ape.Select(key => (key, ape.GetItem(key)?.ToString())),
            _ => Enumerable.Empty<(string, string)>()
        };

        private static string ReadCustomText(Tag tag, string key) => string.Join("; ", CustomTextFields(tag)
            .Where(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(field => field.Value).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal));

        private static void WriteCustomText(Tag tag, string key, string value)
        {
            var keys = CustomTextFields(tag).Where(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Key).Append(key).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var existing in keys)
            {
                switch (tag)
                {
                    case TagLib.Ogg.XiphComment xiph: xiph.RemoveField(existing); break;
                    case TagLib.Mpeg4.AppleTag apple:
                        var parts = existing.Split(':', 2);
                        TagExtraction.AppleTagFields.SetText(apple, parts[0], parts[1], null);
                        break;
                    case TagLib.Asf.Tag asf: asf.RemoveDescriptors(existing); break;
                    case TagLib.Id3v2.Tag id3:
                        foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().Where(frame => frame.Description == existing).ToArray()) id3.RemoveFrame(frame);
                        break;
                    case TagLib.Ape.Tag ape: ape.RemoveItem(existing); break;
                }
            }
            if (string.IsNullOrEmpty(value)) return;
            switch (tag)
            {
                case TagLib.Ogg.XiphComment xiph: xiph.SetField(key, value); break;
                case TagLib.Mpeg4.AppleTag apple:
                    var parts = key.Split(':', 2); TagExtraction.AppleTagFields.SetText(apple, parts[0], parts[1], value); break;
                case TagLib.Asf.Tag asf: asf.SetDescriptorString(value, key); break;
                case TagLib.Id3v2.Tag id3: TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, TagLib.Id3v2.Tag.DefaultEncoding, true).Text = new[] { value }; break;
                case TagLib.Ape.Tag ape: ape.SetValue(key, value); break;
            }
        }

        private static IEnumerable<(Tag Tag, string Key, string Value)> IdentityFields(TagLib.File file, string canonical) =>
            NativeTags(file).SelectMany(tag =>
            {
                var aliases = TagExtraction.TagCanonicalizer.AudioIdentityKeys(tag.TagTypes, canonical);
                return CustomTextFields(tag).Where(field => aliases.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
                    .Select(field => (tag, field.Key, field.Value));
            });

        private static string ReadIdentity(TagLib.File file, string canonical) => string.Join("; ", IdentityFields(file, canonical)
            .Select(field => field.Value).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal));

        private static void WriteIdentity(TagLib.File file, string canonical, string value)
        {
            var fields = IdentityFields(file, canonical).ToArray();
            var tag = fields.FirstOrDefault().Tag ?? PrimaryTag(file);
            var key = fields.FirstOrDefault().Key ?? TagExtraction.TagCanonicalizer.AudioIdentityKeys(tag.TagTypes, canonical).First();
            foreach (var field in fields) WriteCustomText(field.Tag, field.Key, null);
            WriteCustomText(tag, key, value);
        }

        private static string ReadText(TagLib.File file, string id, string fallback)
        {
            return file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple
                ? TagExtraction.TagLibSharpExtractor.ReadAppleText(apple, id) : fallback;
        }

        private const string AsfYearField = "WM/Year";

        private static string ReadYear(TagLib.File file)
        {
            // ASF's generic getter discards years shorter than four digits, although its setter saves them.
            if (file.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf && uint.TryParse(asf.GetDescriptorString(AsfYearField), out var year))
            {
                return year.ToString();
            }

            return file.Tag.Year.ToString();
        }

        private static string ReadPublisher(TagLib.File file)
        {
            var tag = PrimaryTag(file);
            return tag is TagLib.Mpeg4.AppleTag apple ? TagExtraction.AppleTagFields.ReadPublisher(apple)
                : tag is TagLib.Id3v2.Tag ? tag.Publisher : ReadCustomText(tag, tag is TagLib.Asf.Tag ? "WM/Publisher" : "PUBLISHER");
        }

        private static string ReadSeries(TagLib.File file, string key)
        {
            var tag = PrimaryTag(file);
            return tag is TagLib.Mpeg4.AppleTag apple ? string.Join("; ", TagExtraction.AppleTagFields.Read(apple)
                .Where(field => TagExtraction.AppleTagFields.SeriesKey(field.Mean, field.Name) == key).Select(field => field.Value)
                .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal)) : ReadCustomText(tag, key);
        }

        private static void WriteSeries(TagLib.File file, string key, string value)
        {
            var tag = PrimaryTag(file);
            if (tag is TagLib.Mpeg4.AppleTag apple)
            {
                var fields = TagExtraction.AppleTagFields.Read(apple).Where(field => TagExtraction.AppleTagFields.SeriesKey(field.Mean, field.Name) != null).ToArray();
                var mean = fields.Any(field => string.Equals(field.Mean, TagExtraction.AppleTagFields.ToneNamespace, StringComparison.OrdinalIgnoreCase))
                    ? TagExtraction.AppleTagFields.ToneNamespace : "com.apple.iTunes";
                foreach (var field in fields.Where(field => TagExtraction.AppleTagFields.SeriesKey(field.Mean, field.Name) == key))
                    WriteCustomText(tag, field.Mean + ":" + field.Name, null);
                key = mean + ":" + TagExtraction.AppleTagFields.FieldName(mean, key);
            }
            WriteCustomText(tag, key, value);
        }

        private static void WritePublisher(TagLib.File file, string value)
        {
            var tag = PrimaryTag(file);
            if (tag is TagLib.Mpeg4.AppleTag apple) TagExtraction.AppleTagFields.WritePublisher(apple, value);
            else if (tag is TagLib.Id3v2.Tag) tag.Publisher = value;
            else
            {
                var key = tag is TagLib.Asf.Tag ? "WM/Publisher" : "PUBLISHER";
                if (tag is TagLib.Ape.Tag)
                    key = CustomTextFields(tag).FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)).Key ?? "Publisher";
                WriteCustomText(tag, key, value);
            }
        }

        private (Dictionary<string, string> Desired, Dictionary<string, Tuple<string, string>> Diff, string[] Narrators, string Conflict, int RestoredTotals, bool WouldWrite, (bool Ready, IPicture Picture) Cover)
            ReadTagChanges(BookFile trackfile, bool scrub, TagBatch batch)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                return CompareTagChanges(trackfile, scrub, batch);
            }
            finally
            {
                batch.AudioComparisons++;
                batch.AudioComparisonMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        private (Dictionary<string, string> Desired, Dictionary<string, Tuple<string, string>> Diff, string[] Narrators, string Conflict, int RestoredTotals, bool WouldWrite, (bool Ready, IPicture Picture) Cover)
            CompareTagChanges(BookFile trackfile, bool scrub, TagBatch batch)
        {
            using var file = TagLib.File.Create(trackfile.Path);
            if (file.PossiblyCorrupt)
            {
                throw new CorruptFileException("TagLib marked this file as possibly corrupt");
            }

            var series = BookSeries.SelectForAudioTags(trackfile.Edition?.Book);
            var current = ReadWritableTagMap(file, scrub, out var conflict, out var restoredTotals);
            var desired = BuildDesiredTagMap(trackfile, current, series, scrub, out var narrators);
            var diff = ComputeDiff(current, desired);
            // An empty map signals an incomplete edition/book/author graph; never scrub it.
            if (desired.Count == 0) return (desired, diff, narrators, conflict, restoredTotals, false, default);

            var embedCover = _configService.EmbedCoverArt;
            var wouldWrite = diff.Count > 0;
            (bool Ready, IPicture Picture) cover = default;
            if (scrub && conflict == null && !wouldWrite)
            {
                // Compare native metadata without art serialization/padding. Compare picture bytes
                // separately so our own cover is stable and adding a missing cover never triggers a write.
                var pictures = PictureTags(file).Select(tag => (Tag: tag, Pictures: tag.Pictures)).Where(group => group.Pictures.Length > 0).ToList();
                if (embedCover && pictures.Count > 0)
                {
                    cover = ReadCover(trackfile.Edition, batch);
                    wouldWrite = ArtworkChanges(file, cover, scrub);
                }
                if (!wouldWrite)
                {
                    foreach (var tag in PictureTags(file)) tag.Pictures = Array.Empty<IPicture>();
                    // Xiph picture setters defer native field changes until render.
                    (file.GetTag(TagTypes.Xiph) as TagLib.Ogg.XiphComment)?.Render(false);
                    var before = ReadTagContents(file);
                    var existingDesired = desired.Where(pair => !WritableFields.Any(field => field.Key == pair.Key && field.Incidental) ||
                        !string.IsNullOrEmpty(current.GetValueOrDefault(pair.Key))).ToDictionary(pair => pair.Key, pair => pair.Value);
                    ApplyTagMap(file, existingDesired, narrators, true, (true, null));
                    wouldWrite = !before.SequenceEqual(ReadTagContents(file));
                }
                foreach (var group in pictures) group.Tag.Pictures = group.Pictures;
            }

            if (wouldWrite)
            {
                if (embedCover && conflict == null) cover = ReadCover(trackfile.Edition, batch);
                foreach (var field in WritableFields.Where(field => field.Incidental))
                {
                    current.TryGetValue(field.Key, out var before);
                    desired.TryGetValue(field.Key, out var after);
                    if ((scrub || after != null) && !string.Equals(before ?? string.Empty, after ?? string.Empty, StringComparison.Ordinal))
                        diff[field.Key] = Tuple.Create(before, after);
                }
                if (embedCover && ArtworkChanges(file, cover, scrub))
                {
                    diff["COVER"] = Tuple.Create(
                        _localizationService.GetLocalizedString(PictureTags(file).Any(tag => tag.Pictures.Length > 0) ? "AudioTagsExistingArtwork" : "AudioTagsNoArtwork"),
                        _localizationService.GetLocalizedString(cover.Picture == null ? "AudioTagsNoArtwork" : "AudioTagsEditionArtwork"));
                }
                if (scrub)
                {
                    diff["SCRUB"] = Tuple.Create<string, string>(null,
                        _localizationService.GetLocalizedString("AudioTagsScrubChanges"));
                }
            }
            return (desired, diff, narrators, conflict, restoredTotals, wouldWrite, cover);
        }

        private static bool ArtworkChanges(TagLib.File file, (bool Ready, IPicture Picture) cover, bool scrub)
        {
            if (!cover.Ready) return false;
            var pictures = PictureTags(file).Where(tag => tag.Pictures.Length > 0).Select(tag => (Tag: tag, Pictures: tag.Pictures)).ToArray();
            if (cover.Picture == null) return scrub && pictures.Length > 0;
            var primary = PrimaryPictureTag(file);
            var oldPrimary = primary.Pictures;
            IPicture[] intended;
            try
            {
                primary.Pictures = new[] { cover.Picture };
                intended = primary.Pictures;
            }
            finally { primary.Pictures = oldPrimary; }
            return pictures.Length != 1 || pictures[0].Tag != primary || pictures[0].Pictures.Length != intended.Length ||
                pictures[0].Pictures.Zip(intended).Any(pair => !pair.First.Data.Equals(pair.Second.Data) || pair.First.Type != pair.Second.Type ||
                    !string.Equals(pair.First.Description ?? string.Empty, pair.Second.Description ?? string.Empty, StringComparison.Ordinal));
        }

        private (bool Ready, IPicture Picture) ReadCover(Edition edition, TagBatch batch)
        {
            var key = (edition.BookId, edition.Id);
            if (batch.AudioCoverEdition != key)
            {
                // Retain only the current edition's artwork, shared by its audio parts.
                batch.AudioCover = default;
                batch.AudioCoverEdition = key;
                try
                {
                    var result = _coverService.ReadLocalBookCover(edition);
                    if (!result.Ready) return batch.AudioCover;
                    var bytes = result.Data;
                    if (bytes != null)
                    {
                        var format = Image.DetectFormat(bytes);
                        if (format.DefaultMimeType != "image/jpeg" && format.DefaultMimeType != "image/png")
                        {
                            var conversion = Stopwatch.StartNew();
                            using var image = Image.Load(bytes);
                            using var output = new MemoryStream();
                            image.SaveAsJpeg(output);
                            bytes = output.ToArray();
                            batch.AudioCoverConversions++;
                            batch.AudioCoverConversionMilliseconds += conversion.Elapsed.TotalMilliseconds;
                        }
                    }
                    batch.AudioCover = (true, bytes == null ? null : new Picture(bytes)
                    {
                        Type = PictureType.FrontCover,
                        Description = string.Empty
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageFormatException or ImageProcessingException or NotSupportedException)
                {
                    _logger.Debug(ex, "Unable to embed local artwork for edition {0}; keeping the file's artwork", edition.Id);
                }
            }
            return batch.AudioCover;
        }

        private static readonly Regex ChapterKey = new("^(?:CHAPTER[0-9]+(?:NAME|URL)?|CUESHEET)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static Tag PrimaryPictureTag(TagLib.File file) => PrimaryTag(file, pictures: true);
        private static IEnumerable<Tag> PictureTags(TagLib.File file) => NativeTags(file, pictures: true);

        private static string[] ReadTagContents(TagLib.File file)
        {
            var contents = new List<string>();
            void Add(string type, ByteVector data) => contents.Add(type + Convert.ToHexString(SHA256.HashData(data.Data)));
            if (file.GetTag(TagTypes.Id3v1) is TagLib.Id3v1.Tag id3v1) Add("ID3v1", id3v1.Render());
            if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                foreach (var frame in id3.GetFrames())
                {
                    // Text encoding is representation, not metadata that scrub removes.
                    if (frame is TagLib.Id3v2.TextInformationFrame text)
                        contents.Add("ID3v2" + frame.FrameId.ToString() + "\0" + (text is TagLib.Id3v2.UserTextInformationFrame user ? user.Description + "\0" : string.Empty) + string.Join("\0", text.Text));
                    else Add("ID3v2", frame.Render(id3.Version));
                }
            if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                foreach (var key in xiph) contents.Add("Xiph" + key + "\0" + string.Join("\0", xiph.GetField(key)));
            if (file.GetTag(TagTypes.Ape) is TagLib.Ape.Tag ape)
                foreach (var key in ape) Add("APE", ape.GetItem(key).Render());
            if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                foreach (var box in apple) Add("Apple", box.Render());
            if (file.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf)
            {
                Add("ASF-content", asf.ContentDescriptionObject.Render());
                foreach (var descriptor in asf) Add("ASF-descriptor", descriptor.Render());
                foreach (var record in asf.MetadataLibraryObject) Add("ASF-metadata", record.Render());
            }
            foreach (var type in new[] { TagTypes.RiffInfo, TagTypes.MovieId })
            {
                if (file.GetTag(type) is not TagLib.Riff.ListTag riff) continue;
                foreach (var field in new TagLib.Riff.List(riff.Render()))
                    foreach (var value in field.Value) Add(type + field.Key.ToString(), value);
            }
            return contents.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static Dictionary<string, string> ReadWritableTagMap(TagLib.File file, bool scrub, out string conflict, out int restoredTotals)
        {
            conflict = null;
            restoredTotals = 0;
            var current = WritableFields.ToDictionary(field => field.Key, field => field.Read(file), StringComparer.OrdinalIgnoreCase);
            if (scrub && (file.Tag.TrackCount == 0 || file.Tag.DiscCount == 0))
            {
                // Reuse the scanner's existing interpretation, not another raw-field list or file read.
                var stored = TagExtraction.TagLibSharpExtractor.ExtractTags(file);
                TagExtraction.TagCanonicalizer.AddCanonicalKeys(stored);
                foreach (var (field, canonical) in new[] { ("TRACKCOUNT", "TOTALTRACKS"), ("DISCCOUNT", "TOTALDISCS") })
                {
                    if (current[field] != "0" || !stored.TryGetValue(canonical, out var values)) continue;
                    var numbers = values.Select(value => uint.TryParse(value, out var number) ? number : 0u)
                        .Where(number => number > 0).Distinct().Take(2).ToArray();
                    if (numbers.Length > 1)
                    {
                        conflict = $"Conflicting stored numbering: {canonical} {string.Join(" vs ", numbers)}; refusing to discard or guess a total";
                    }
                    if (numbers.Length == 1)
                    {
                        current[field] = numbers[0].ToString();
                        restoredTotals++;
                    }
                }
            }

            return current;
        }

        private static Dictionary<string, Tuple<string, string>> ComputeDiff(Dictionary<string, string> current, Dictionary<string, string> desired)
        {
            var diff = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in desired)
            {
                if (WritableFields.Any(field => field.Key == kv.Key && field.Incidental)) continue;
                current.TryGetValue(kv.Key, out var oldVal);
                var newVal = kv.Value ?? string.Empty;
                if (!string.Equals(oldVal ?? string.Empty, newVal, StringComparison.Ordinal))
                {
                    diff[kv.Key] = Tuple.Create(oldVal, newVal);
                }
            }
            return diff;
        }

        private sealed class PreservingMpeg4File : TagLib.Mpeg4.File
        {
            public PreservingMpeg4File(string path) : base(path)
            {
                // TagLib's UnknownBox reads after BoxHeader's 32-byte probe instead of
                // seeking to the payload. Restore opaque user data (including Nero chapters)
                // from the native headers before Save re-renders the udta container.
                Mode = AccessMode.Read;
                try
                {
                    foreach (var container in UdtaBoxes.Where(box => box.ParentTree?.Length > 0))
                    {
                        var parent = container.ParentTree.Last();
                        var position = parent.Position + parent.HeaderSize;
                        foreach (var child in container.Children)
                        {
                            if (position >= parent.Position + parent.TotalBoxSize) break;
                            var header = new TagLib.Mpeg4.BoxHeader(this, position);
                            if (header.TotalBoxSize <= 0 || position + header.TotalBoxSize > parent.Position + parent.TotalBoxSize)
                                throw new CorruptFileException("Invalid MP4 user-data box size");
                            if (child is TagLib.Mpeg4.UnknownBox)
                            {
                                Seek(position + header.HeaderSize);
                                child.Data = ReadBlock(checked((int)header.DataSize));
                                if (child.Data.Count != header.DataSize) throw new CorruptFileException("Incomplete MP4 user-data box");
                            }
                            position += header.TotalBoxSize;
                        }
                    }
                }
                finally { Mode = AccessMode.Closed; }
            }
        }

        private static void WriteTagMap(string path, Dictionary<string, string> desired, string[] narrators, bool scrub, (bool Ready, IPicture Picture) cover)
        {
            using var detected = TagLib.File.Create(path);
            using var preserved = detected is TagLib.Mpeg4.File ? new PreservingMpeg4File(path) : null;
            var file = preserved ?? detected;
            ApplyTagMap(file, desired, narrators, scrub, cover);
            file.Save();
        }

        private static void ApplyTagMap(TagLib.File file, Dictionary<string, string> desired, string[] narrators, bool scrub, (bool Ready, IPicture Picture) cover)
        {
            var keptPictures = scrub && !cover.Ready
                ? PictureTags(file).Select(tag => (Tag: tag, Pictures: tag.Pictures)).ToList() : null;
            var id3 = file.GetTag(TagTypes.Id3v2) as TagLib.Id3v2.Tag;
            var xiph = file.GetTag(TagTypes.Xiph) as TagLib.Ogg.XiphComment;
            var chapters = scrub ? id3?.GetFrames().Where(frame => frame.FrameId.ToString() is "CHAP" or "CTOC").ToArray() : null;
            var chapterFields = scrub ? xiph?.Where(key => ChapterKey.IsMatch(key)).ToDictionary(key => key, key => xiph.GetField(key)) : null;
            var identities = scrub ? new[] { "ASIN", "ISBN" }.Where(desired.ContainsKey)
                .Select(key => IdentityFields(file, key).FirstOrDefault()).Where(field => field.Tag != null).ToArray() : null;
            var apePublisher = scrub && desired.ContainsKey("PUBLISHER") && PrimaryTag(file) is TagLib.Ape.Tag ape
                ? CustomTextFields(ape).FirstOrDefault(field => string.Equals(field.Key, "PUBLISHER", StringComparison.OrdinalIgnoreCase)) : default;
            if (scrub)
            {
                // Keep writable containers; removing them would discard subsequent assignments.
                file.Tag.Clear();
                // TagLib 2.2 FLAC Metadata.Clear removes pictures but leaves its Xiph fields.
                file.GetTag(TagTypes.Xiph)?.Clear();
                // ASF Clear only removes picture records from its separate metadata library.
                if (file.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf)
                {
                    foreach (var record in asf.MetadataLibraryObject.Where(record => record.StreamNumber == 0).ToArray())
                        asf.MetadataLibraryObject.RemoveRecords(record.LanguageListIndex, record.StreamNumber, record.Name);
                }
            }

            if (identities != null) foreach (var field in identities) WriteCustomText(field.Tag, field.Key, field.Value);
            if (apePublisher.Key != null) WriteCustomText(file.GetTag(TagTypes.Ape), apePublisher.Key, apePublisher.Value);
            if (chapters != null) foreach (var chapter in chapters) id3.AddFrame(chapter);
            if (chapterFields != null) foreach (var pair in chapterFields) xiph.SetField(pair.Key, pair.Value);

            if (keptPictures != null)
            {
                foreach (var group in keptPictures) group.Tag.Pictures = group.Pictures;
            }
            else if (cover.Ready && (scrub || cover.Picture != null))
            {
                foreach (var tag in PictureTags(file)) tag.Pictures = Array.Empty<IPicture>();
                if (cover.Picture != null) PrimaryPictureTag(file).Pictures = new[] { cover.Picture };
            }

            foreach (var field in WritableFields)
            {
                if (desired.TryGetValue(field.Key, out var value))
                {
                    field.Write(file, value, narrators);
                }
            }
        }
    }
}
