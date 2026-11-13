using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using TagLib;

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
        private readonly TagExtraction.ITagExtractionService _tagExtractionService;

        public AudioTagService(IConfigService configService,
                               IMediaFileService mediaFileService,
                               IDiskProvider diskProvider,
                               IRootFolderWatchingService rootFolderWatchingService,
                               IFileMutationSafetyService fileMutationSafetyService,
                               IEventAggregator eventAggregator,
                               IMediaInfoExtractor mediaInfoExtractor,
                               Logger logger,
                               TagExtraction.ITagExtractionService tagExtractionService)
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

        private Dictionary<string, string> BuildDesiredTagMap(BookFile trackfile, Dictionary<string, string> current, out string[] narrators)
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

            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = edition.Title ?? string.Empty,
                ["ALBUM"] = book.Title ?? string.Empty,
                ["ARTIST"] = author.Name ?? string.Empty,
            };

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
                var mode = _configService.WriteAudioTags;
                if (mode == WriteAudioTagsType.No || (mode == WriteAudioTagsType.NewFiles && !newDownload))
                {
                    batch.Skipped++;
                    return;
                }
            }

            var operation = "read audio tags";
            var saved = false;
            try
            {
                var scrub = _configService.ScrubAudioTags;
                var current = ReadWritableTagMap(path, scrub, out var conflict, out var restoredTotals);
                batch.Read++;
                var desired = BuildDesiredTagMap(trackfile, current, out var narrators);
                var diff = ComputeDiff(current, desired);
                if (!diff.Any())
                {
                    batch.Skipped++;
                    return;
                }

                if (conflict != null)
                {
                    operation = "scrub audio tags";
                    throw new IOException(conflict);
                }

                operation = "update audio tags";
                _rootFolderWatchingService.ReportFileSystemChangeBeginning(path);
                if (_fileMutationSafetyService.EnsureMutableFile(path))
                {
                    batch.Separated++;
                }

                WriteTagMap(path, scrub ? desired : diff.ToDictionary(pair => pair.Key, pair => pair.Value.Item2), narrators, scrub);
                saved = true;
                batch.RestoredTotals += restoredTotals;
                operation = "persist file statistics after saving audio tags";
                UpdateTrackfileSizeAndModified(trackfile, path);
                operation = "publish the audio retag event";
                _eventAggregator.PublishEvent(new BookFileRetaggedEvent(trackfile.Author, trackfile, diff, scrub));
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

        private void LogBatch(string operation, TagBatch batch)
        {
            if (!batch.HasProblems) return;
            _logger.Warn("Audio tags {0}: {1} read, {2} skipped, {3} failed, {4} separated, {5} written in {6} ms, {7} stored totals preserved through scrub, {8} incomplete",
                operation, batch.Read, batch.Skipped + batch.SkippedSymlinks + batch.Unsupported, batch.Failed, batch.Separated, batch.Written, batch.Timer.ElapsedMilliseconds, batch.RestoredTotals, batch.Incomplete);
        }

        private void RetagBatch(List<BookFile> files, TagBatch batch)
        {
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
            foreach (var edition in _mediaFileService.HydrateEditionBookFiles(editions))
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
            foreach (var file in files)
            {
                try
                {
                    var current = ReadWritableTagMap(file.Path, _configService.ScrubAudioTags, out _, out _);
                    batch.Read++;
                    var diff = ComputeDiff(current, BuildDesiredTagMap(file, current, out _));
                    if (diff.Any())
                    {
                        previews.Add(new RetagBookFilePreview
                        {
                            AuthorId = file.Author.Id,
                            BookId = file.Edition.BookId,
                            BookFileId = file.Id,
                            Title = file.Edition.Title,
                            Path = file.Path,
                            IsFileSymlink = FileMutationSafetyService.IsFileSymlink(file.Path),
                            Changes = diff
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

            return previews;
        }

        public TagBatch RetagFiles(RetagFilesCommand message, List<BookFile> files, TagBatch batch)
        {
            files = RetagMediaScope.Select(files, BookMediaType.Audiobook, batch).ToList();
            _logger.ProgressInfo("Re-tagging {0} audio files", files.Count);
            RetagBatch(files, batch);
            return batch;
        }

        public TagBatch RetagAuthor(RetagAuthorCommand message, IReadOnlyList<Author> authors, TagBatch batch)
        {
            RetagMediaScope.ForEachAuthor(authors, _mediaFileService, BookMediaType.Audiobook, batch, files =>
            {
                _logger.ProgressInfo("Re-tagging {0} audio files for {1}", files.Count, batch.CurrentAuthor);
                RetagBatch(files, batch);
            });

            return batch;
        }

        // Both comparisons and writes use the same fields; file values are comparison evidence,
        // while desired metadata comes from the linked edition, book and author.
        private static readonly (string Key, Func<TagLib.File, string> Read, Action<TagLib.File, string, string[]> Write)[] WritableFields =
        {
            ("TITLE", f => ReadText(f, "©nam", f.Tag.Title), (f, v, _) => f.Tag.Title = v),
            ("ALBUM", f => ReadText(f, "©alb", f.Tag.Album), (f, v, _) => f.Tag.Album = v),
            ("ARTIST", f => ReadText(f, "©ART", string.Join("; ", f.Tag.Performers)), (f, v, _) => f.Tag.Performers = new[] { v }),
            // ID3v2.3 treats a slash as a list separator; preserve existing tag versions.
            ("COMPOSER", f => ReadText(f, "©wrt", string.Join("; ", f.Tag.Composers)), (f, v, narrators) => f.Tag.Composers = narrators),
            ("TRACKNUMBER", f => f.Tag.Track.ToString(), (f, v, _) => f.Tag.Track = uint.Parse(v)),
            ("TRACKCOUNT", f => f.Tag.TrackCount.ToString(), (f, v, _) => f.Tag.TrackCount = uint.Parse(v)),
            ("DISCNUMBER", f => f.Tag.Disc.ToString(), (f, v, _) => f.Tag.Disc = uint.Parse(v)),
            ("DISCCOUNT", f => f.Tag.DiscCount.ToString(), (f, v, _) => f.Tag.DiscCount = uint.Parse(v)),
            ("YEAR", ReadYear, (f, v, _) => f.Tag.Year = uint.Parse(v)),
            ("PUBLISHER", ReadPublisher, (f, v, _) => WritePublisher(f, v))
        };

        private static string ReadText(TagLib.File file, string id, string fallback)
        {
            return file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple
                ? TagExtraction.TagLibSharpExtractor.ReadAppleText(apple, id) : fallback;
        }

        private static string ReadYear(TagLib.File file)
        {
            // ASF's generic getter discards years shorter than four digits, although its setter saves them.
            if (file.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf && uint.TryParse(asf.GetDescriptorString("WM/Year"), out var year))
            {
                return year.ToString();
            }

            return file.Tag.Year.ToString();
        }

        private static readonly (TagTypes Type, Func<Tag, string> Read, Action<Tag, string> Write)[] PublisherFields =
        {
            (TagTypes.Xiph, t => string.Join("; ", ((TagLib.Ogg.XiphComment)t).GetField("PUBLISHER")), (t, v) => ((TagLib.Ogg.XiphComment)t).SetField("PUBLISHER", v)),
            (TagTypes.Apple, t => t.Publisher, (t, v) => t.Publisher = v),
            (TagTypes.Asf, t => ((TagLib.Asf.Tag)t).GetDescriptorString("WM/Publisher"), (t, v) => ((TagLib.Asf.Tag)t).SetDescriptorString(v, "WM/Publisher")),
            (TagTypes.Id3v2, t => t.Publisher, (t, v) => t.Publisher = v),
            (TagTypes.Ape, t => ((TagLib.Ape.Tag)t).GetItem("Publisher")?.ToString(), (t, v) => ((TagLib.Ape.Tag)t).SetValue("Publisher", v))
        };

        private static string ReadPublisher(TagLib.File file)
        {
            foreach (var field in PublisherFields)
            {
                if (file.GetTag(field.Type) is Tag tag) return field.Read(tag);
            }

            return null;
        }

        private static void WritePublisher(TagLib.File file, string value)
        {
            foreach (var field in PublisherFields)
            {
                if (file.GetTag(field.Type) is Tag tag)
                {
                    field.Write(tag, value);
                    return;
                }
            }
        }

        private static Dictionary<string, string> ReadWritableTagMap(string path, bool scrub, out string conflict, out int restoredTotals)
        {
            conflict = null;
            restoredTotals = 0;
            using var file = TagLib.File.Create(path);
            if (file.PossiblyCorrupt)
            {
                throw new CorruptFileException("TagLib marked this file as possibly corrupt");
            }

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
                current.TryGetValue(kv.Key, out var oldVal);
                var newVal = kv.Value ?? string.Empty;
                if (!string.Equals(oldVal ?? string.Empty, newVal, StringComparison.Ordinal))
                {
                    diff[kv.Key] = Tuple.Create(oldVal, newVal);
                }
            }
            return diff;
        }

        private static void WriteTagMap(string path, Dictionary<string, string> desired, string[] narrators, bool scrub)
        {
            using var file = TagLib.File.Create(path);
            if (scrub)
            {
                // Keep writable containers; removing them would discard subsequent assignments.
                file.Tag.Clear();
                // TagLib 2.2 FLAC Metadata.Clear removes pictures but leaves its Xiph fields.
                file.GetTag(TagTypes.Xiph)?.Clear();
            }

            foreach (var field in WritableFields)
            {
                if (desired.TryGetValue(field.Key, out var value))
                {
                    field.Write(file, value, narrators);
                }
            }

            file.Save();
        }
    }
}
