using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
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
        void RetagFiles(RetagFilesCommand message);
        void RetagAuthor(RetagAuthorCommand message);
    }

    public class AudioTagService : IAudioTagService
    {
        private readonly IConfigService _configService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderWatchingService _rootFolderWatchingService;
        private readonly IFileMutationSafetyService _fileMutationSafetyService;
        private readonly IAuthorService _authorService;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly Logger _logger;
        private readonly TagExtraction.ITagExtractionService _tagExtractionService;

        public AudioTagService(IConfigService configService,
                               IMediaFileService mediaFileService,
                               IDiskProvider diskProvider,
                               IRootFolderWatchingService rootFolderWatchingService,
                               IFileMutationSafetyService fileMutationSafetyService,
                               IAuthorService authorService,
                               IMapCoversToLocal mediaCoverService,
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
            _authorService = authorService;
            _mediaCoverService = mediaCoverService;
            _eventAggregator = eventAggregator;
            _mediaInfoExtractor = mediaInfoExtractor;
            _logger = logger;
            _tagExtractionService = tagExtractionService;
        }

        private Dictionary<string, List<string>> ReadAllTagMap(string path)
        {
            return _tagExtractionService.ExtractTags(path) ?? new Dictionary<string, List<string>>();
        }

        // Removed legacy ReadTags (ParsedTrackInfo) path; use ReadAllTags for field-agnostic tags

        public Dictionary<string, List<string>> ReadAllTags(string path)
        {
            // Route through the field-agnostic extraction service (TagLib# with FFprobe rescue).
            return _tagExtractionService.ExtractTags(path);
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(string path)
        {
            // Tag extraction and duration share one primary TagLib# read.
            return ReadAllTagsAndDurationInternal(path);
        }

        private (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDurationInternal(string path)
        {
            var (tags, durationSeconds) = _tagExtractionService.ExtractTagsAndDuration(path);

            // Defensive fallback: if no duration was captured during tag extraction, compute it from media properties.
            if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
            {
                durationSeconds = MediaDuration.FromTimeSpan(_mediaInfoExtractor.GetDuration(path));
            }

            return (tags ?? new Dictionary<string, List<string>>(), durationSeconds);
        }

        private Dictionary<string, string> BuildDesiredTagMap(BookFile trackfile)
        {
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

            var partCount = edition.BookFiles?.Count ?? 0;

            var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = edition.Title ?? string.Empty,
                ["ALBUM"] = book.Title ?? string.Empty,
                ["ARTIST"] = author.Name ?? string.Empty,
                ["TRACKNUMBER"] = (trackfile.Part > 0 ? trackfile.Part : 1).ToString(),
                ["TRACKCOUNT"] = (partCount > 0 ? partCount : 0).ToString(),
                ["YEAR"] = (edition.ReleaseDate?.Year ?? 0).ToString(),
            };

            if (!string.IsNullOrWhiteSpace(edition.Publisher))
            {
                desired["PUBLISHER"] = edition.Publisher;
            }

            if (edition.NarratorNames != null && edition.NarratorNames.Any())
            {
                desired["COMPOSER"] = string.Join("; ", edition.NarratorNames);
                desired["NARRATOR"] = desired["COMPOSER"];
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

        public void RemoveAllTags(string path)
        {
            TagLib.File file = null;
            try
            {
                file = TagLib.File.Create(path);
                file.RemoveTags(TagLib.TagTypes.AllTags);
                file.Save();
            }
            catch (CorruptFileException ex)
            {
                _logger.Warn(ex, $"Tag removal failed for {path}.  File is corrupt");
            }
            catch (Exception ex)
            {
                _logger.ForWarnEvent()
                    .Exception(ex)
                    .Message($"Tag removal failed for {path}")
                    .WriteSentryWarn("Tag removal failed")
                    .Log();
            }
            finally
            {
                file?.Dispose();
            }
        }

        public void WriteTags(BookFile trackfile, bool newDownload, bool force = false)
        {
            if (MediaFileExtensions.IsMatroskaAudioExtension(Path.GetExtension(trackfile?.Path)))
            {
                _logger.Debug("Skipping tag write for Matroska audio file: {0}", trackfile?.Path);
                return;
            }

            if (!force)
            {
                if (_configService.WriteAudioTags == WriteAudioTagsType.No ||
                    (_configService.WriteAudioTags == WriteAudioTagsType.NewFiles && !newDownload))
                {
                    return;
                }
            }

            var desired = BuildDesiredTagMap(trackfile);
            var path = trackfile.Path;

            var currentFlat = Flatten(ReadAllTagMap(path));
            var diff = ComputeDiff(currentFlat, desired);

            if (!diff.Any())
            {
                _logger.Debug("No tags update for {0} due to no difference", trackfile);
                return;
            }

            _rootFolderWatchingService.ReportFileSystemChangeBeginning(path);
            _fileMutationSafetyService.EnsureMutableFile(path);

            if (_configService.ScrubAudioTags)
            {
                _logger.Debug($"Scrubbing tags for {trackfile}");
                RemoveAllTags(path);
            }

            _logger.Debug($"Writing tags for {trackfile}");
            WriteTagMap(path, desired);

            UpdateTrackfileSizeAndModified(trackfile, path);

            _eventAggregator.PublishEvent(new BookFileRetaggedEvent(trackfile.Author, trackfile, diff, _configService.ScrubAudioTags));
        }

        public void SyncTags(List<Edition> editions)
        {
            if (_configService.WriteAudioTags != WriteAudioTagsType.Sync)
            {
                return;
            }

            var hydratedEditions = HydrateEditionBookFiles(editions);

            foreach (var edition in hydratedEditions)
            {
                var bookFiles = edition.BookFiles ?? new List<BookFile>();

                _logger.Debug($"Syncing audio tags for {bookFiles.Count} files");

                foreach (var file in bookFiles.Where(x => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(x.Path))))
                {
                    // populate tracks (which should also have release/book/author set) because
                    // not all of the updates will have been committed to the database yet
                    file.Edition = edition;
                    WriteTags(file, false);
                }
            }
        }

        private List<Edition> HydrateEditionBookFiles(List<Edition> editions)
        {
            var safeEditions = editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            var missingBookFiles = safeEditions.Where(e => e.BookFiles == null && e.BookId > 0).ToList();

            if (!missingBookFiles.Any())
            {
                return safeEditions;
            }

            var bookIds = missingBookFiles.Select(e => e.BookId).Distinct().ToList();
            var filesByEditionId = (_mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>())
                .Where(file => file != null && file.EditionId > 0)
                .GroupBy(file => file.EditionId)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var edition in missingBookFiles)
            {
                edition.BookFiles = filesByEditionId.TryGetValue(edition.Id, out var editionFiles)
                    ? editionFiles
                    : new List<BookFile>();
            }

            return safeEditions;
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId)
        {
            var files = _mediaFileService.GetFilesByAuthor(authorId);

            return GetPreviews(files).OrderBy(b => b.BookId).ThenBy(b => b.Path).ToList();
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId)
        {
            var files = _mediaFileService.GetFilesByBook(bookId);

            return GetPreviews(files).OrderBy(b => b.BookId).ThenBy(b => b.Path).ToList();
        }

        private IEnumerable<RetagBookFilePreview> GetPreviews(List<BookFile> files)
        {
            foreach (var f in files.Where(x => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(x.Path)) && x.Edition != null).OrderBy(x => x.Edition?.Title ?? string.Empty))
            {
                var file = f;

                var currentFlat = Flatten(ReadAllTagMap(f.Path));
                var desired = BuildDesiredTagMap(f);
                var diff = ComputeDiff(currentFlat, desired);

                if (diff.Any())
                {
                    yield return new RetagBookFilePreview
                    {
                        AuthorId = file.Author.Id,
                        BookId = file.Edition.Id,
                        BookFileId = file.Id,
                        Path = file.Path,
                        Changes = diff
                    };
                }
            }
        }

        public void RetagFiles(RetagFilesCommand message)
        {
            var author = _authorService.GetAuthor(message.AuthorId);
            var bookFiles = _mediaFileService.Get(message.Files);
            var audioFiles = bookFiles.Where(x => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(x.Path))).ToList();

            _logger.ProgressInfo("Re-tagging {0} audio files for {1}", audioFiles.Count, author.Name);
            foreach (var file in audioFiles)
            {
                WriteTags(file, false, force: true);
            }

            _logger.ProgressInfo("Selected audio files re-tagged for {0}", author.Name);
        }

        public void RetagAuthor(RetagAuthorCommand message)
        {
            _logger.Debug("Re-tagging all audio files for selected authors");
            var authorToRename = _authorService.GetAuthors(message.AuthorIds);

            foreach (var author in authorToRename)
            {
                var bookFiles = _mediaFileService.GetFilesByAuthor(author.Id);
                var audioFiles = bookFiles.Where(x => MediaFileExtensions.CanWriteAudioTags(Path.GetExtension(x.Path))).ToList();

                _logger.ProgressInfo("Re-tagging all audio files for author: {0}", author.Name);
                foreach (var file in audioFiles)
                {
                    WriteTags(file, false, force: true);
                }

                _logger.ProgressInfo("All audio files re-tagged for {0}", author.Name);
            }
        }

        private static Dictionary<string, string> Flatten(Dictionary<string, List<string>> multi)
        {
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (multi == null) return flat;
            foreach (var kv in multi)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                var joined = string.Join("; ", kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)));
                flat[kv.Key] = joined;
            }
            return flat;
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

        private void WriteTagMap(string path, Dictionary<string, string> desired)
        {
            TagLib.File file = null;
            try
            {
                file = TagLib.File.Create(path);
                if (desired.TryGetValue("TITLE", out var title)) file.Tag.Title = title;
                if (desired.TryGetValue("ARTIST", out var artist)) file.Tag.Performers = new[] { artist };
                if (desired.TryGetValue("ALBUM", out var album)) file.Tag.Album = album;
                if (desired.TryGetValue("TRACKNUMBER", out var trackStr) && uint.TryParse(trackStr, out var trackNo)) file.Tag.Track = trackNo;
                if (desired.TryGetValue("TRACKCOUNT", out var trackCntStr) && uint.TryParse(trackCntStr, out var trackCnt)) file.Tag.TrackCount = trackCnt;
                if (desired.TryGetValue("YEAR", out var yearStr) && uint.TryParse(yearStr, out var year)) file.Tag.Year = year;
                if (desired.TryGetValue("COMPOSER", out var composers)) file.Tag.Composers = composers.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                if (desired.TryGetValue("PUBLISHER", out var publisher)) file.Tag.Publisher = publisher;

                file.Save();
            }
            catch (Exception ex)
            {
                _logger.ForWarnEvent().Exception(ex).Message($"Failed to write tags for {path}").WriteSentryWarn("Tag write failed").Log();
            }
            finally
            {
                file?.Dispose();
            }
        }

        private string CleanNarratorValue(string narrator)
        {
            if (string.IsNullOrWhiteSpace(narrator))
            {
                return null;
            }

            // Clean up common narrator prefixes/suffixes
            narrator = narrator.Trim();
            narrator = narrator.Replace("Narrated by ", "", StringComparison.OrdinalIgnoreCase);
            narrator = narrator.Replace("Read by ", "", StringComparison.OrdinalIgnoreCase);
            narrator = narrator.Replace("Performed by ", "", StringComparison.OrdinalIgnoreCase);

            // Handle multiple narrators - take the first one
            if (narrator.Contains(";"))
            {
                narrator = narrator.Split(';')[0].Trim();
            }
            else if (narrator.Contains(",") && narrator.Split(',').Length <= 3) // Only split if reasonable number
            {
                narrator = narrator.Split(',')[0].Trim();
            }

            return narrator;
        }

    }
}
