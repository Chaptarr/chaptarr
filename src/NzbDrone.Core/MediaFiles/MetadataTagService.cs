using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMetadataTagService
    {
        Dictionary<string, List<string>> ReadAllTags(IFileInfo file);
        (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(IFileInfo file);
        string ReadAllTagsAsJson(IFileInfo file);
        void WriteTags(BookFile trackfile, bool newDownload, bool force = false);
        void SyncTags(List<Edition> books);
        List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId, BookMediaType? mediaType = null);
        List<RetagBookFilePreview> GetRetagPreviewsByBook(int authorId, BookMediaType? mediaType = null);
    }

    public class MetadataTagService : IMetadataTagService,
        IExecute<RetagFilesCommand>,
        IExecute<RetagAuthorCommand>
    {
        private const int MaxCachedTagExtractions = 1000;
        private const long UnixEpochTicks = 621355968000000000L;

        private readonly IAudioTagService _audioTagService;
        private readonly IEBookTagService _eBookTagService;
        private readonly ConcurrentDictionary<string, TagCacheEntry> _tagCache = new ConcurrentDictionary<string, TagCacheEntry>(PathEqualityComparer.Instance);
        private readonly IFileTagCacheRepository _fileTagCache;
        private readonly Logger _logger;
        private readonly ILocalizationService _localizationService;
        private readonly ICommandResultReporter _commandResultReporter;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAuthorService _authorService;

        public MetadataTagService(IAudioTagService audioTagService,
            IEBookTagService eBookTagService,
            Logger logger,
            ILocalizationService localizationService,
            ICommandResultReporter commandResultReporter,
            IMediaFileService mediaFileService,
            IAuthorService authorService,
            IFileTagCacheRepository fileTagCache = null)
        {
            _audioTagService = audioTagService;
            _eBookTagService = eBookTagService;
            _fileTagCache = fileTagCache;

            _logger = logger;
            _localizationService = localizationService;
            _commandResultReporter = commandResultReporter ?? throw new ArgumentNullException(nameof(commandResultReporter));
            _mediaFileService = mediaFileService;
            _authorService = authorService;
        }

        // Removed ReadTags (ParsedTrackInfo) path; use ReadAllTags for field-agnostic tags

        public Dictionary<string, List<string>> ReadAllTags(IFileInfo file)
        {
            if (TryGetCachedTags(file, out var cached))
            {
                return cached;
            }

            if (TryGetPersistedTagsAndDuration(file, out var persistedTags, out var persistedDurationSeconds, out var persistedDisposition))
            {
                CacheTagsAndDuration(file, persistedTags, persistedDurationSeconds, persistedDisposition, persist: false);
                return persistedTags;
            }

            var stopwatch = Stopwatch.StartNew();
            Dictionary<string, List<string>> result;
            var isAudio = MediaFileExtensions.AudioExtensions.Contains(file.Extension);

            if (isAudio)
            {
                result = _audioTagService.ReadAllTags(file.FullName);
            }
            else
            {
                result = _eBookTagService.ReadAllTags(file);
            }

            CleanupTags(result);
            if (!isAudio)
            {
                CacheTagsAndDuration(file, result, null, TagExtractionResult.Classify(result));
            }

            stopwatch.Stop();
            _logger.Debug("[PERFORMANCE] ReadAllTags for '{0}' took {1}ms, found {2} tags",
                file.Name, stopwatch.ElapsedMilliseconds, result?.Count ?? 0);
            return result;
        }

        public (Dictionary<string, List<string>> Tags, int? DurationSeconds) ReadAllTagsAndDuration(IFileInfo file)
        {
            if (TryGetCachedTagsAndDuration(file, out var cachedTags, out var cachedDurationSeconds))
            {
                return (cachedTags, cachedDurationSeconds);
            }

            if (TryGetPersistedTagsAndDuration(file, out var persistedTags, out var persistedDurationSeconds, out var persistedDisposition))
            {
                CacheTagsAndDuration(file, persistedTags, persistedDurationSeconds, persistedDisposition, persist: false);
                return (persistedTags, persistedDurationSeconds);
            }

            var stopwatch = Stopwatch.StartNew();

            Dictionary<string, List<string>> tags;
            int? durationSeconds = null;

            if (MediaFileExtensions.AudioExtensions.Contains(file.Extension))
            {
                (tags, durationSeconds) = _audioTagService.ReadAllTagsAndDuration(file.FullName);
            }
            else
            {
                tags = _eBookTagService.ReadAllTags(file);
            }

            CleanupTags(tags);

            stopwatch.Stop();
            _logger.Debug("[PERFORMANCE] ReadAllTagsAndDuration for '{0}' took {1}ms, tags={2}, duration={3}",
                file.Name,
                stopwatch.ElapsedMilliseconds,
                tags?.Count ?? 0,
                durationSeconds.HasValue ? $"{durationSeconds.Value}s" : "null");

            CacheTagsAndDuration(file, tags, durationSeconds, TagExtractionResult.Classify(tags));

            return (tags, durationSeconds);
        }

        public string ReadAllTagsAsJson(IFileInfo file)
        {
            var tags = ReadAllTags(file);
            if (tags == null)
            {
                return "{}";
            }

            // Convert to JSON with compact formatting
            return JsonConvert.SerializeObject(tags, Formatting.None);
        }

        private void CleanupTags(Dictionary<string, List<string>> tags)
        {
            // Global cleanup: drop synthetic extraction noise keys upfront for all media types.
            // Keep this aligned with the canonical policy helper so raw extraction stays stable.
            try
            {
                if (tags == null || tags.Count == 0)
                {
                    return;
                }

                var toRemove = new List<string>();
                foreach (var k in tags.Keys)
                {
                    if (string.IsNullOrWhiteSpace(k))
                    {
                        continue;
                    }

                    if (TagExclusionPolicy.IsExtractionNoiseKey(k))
                    {
                        toRemove.Add(k);
                    }
                }

                foreach (var k in toRemove)
                {
                    tags.Remove(k);
                }
            }
            catch (System.Exception ex)
            {
                _logger.Trace(ex, "[TAG-EXTRACT] Failed to remove encode keys (global) from tags");
            }
        }

        private bool TryGetCachedTags(IFileInfo file, out Dictionary<string, List<string>> cachedTags)
        {
            cachedTags = null;
            if (!TryGetCacheKey(file, out var key))
            {
                return false;
            }

            if (_tagCache.TryGetValue(key, out var cached) &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc &&
                cached.FileSize == file.Length)
            {
                cachedTags = CloneTags(cached.Tags);
                return true;
            }

            return false;
        }

        private bool TryGetCachedTagsAndDuration(IFileInfo file, out Dictionary<string, List<string>> cachedTags, out int? cachedDurationSeconds)
        {
            cachedTags = null;
            cachedDurationSeconds = null;
            if (!TryGetCacheKey(file, out var key))
            {
                return false;
            }

            if (_tagCache.TryGetValue(key, out var cached) &&
                cached.LastWriteTimeUtc == file.LastWriteTimeUtc &&
                cached.FileSize == file.Length)
            {
                cachedTags = CloneTags(cached.Tags);
                cachedDurationSeconds = cached.DurationSeconds;
                return true;
            }

            return false;
        }

        private bool TryGetPersistedTagsAndDuration(
            IFileInfo file,
            out Dictionary<string, List<string>> tags,
            out int? durationSeconds,
            out TagExtractionDisposition disposition)
        {
            tags = null;
            durationSeconds = null;
            disposition = TagExtractionDisposition.Failed;

            if (_fileTagCache == null || !TryGetFileIdentity(file, out var path, out var mtimeNs, out var sizeBytes))
            {
                return false;
            }

            if (!_fileTagCache.TryGet(path, mtimeNs, sizeBytes, out var tagsJson, out durationSeconds, out var extractionStatus))
            {
                return false;
            }

            tags = DeserializeTags(tagsJson);
            CleanupTags(tags);

            if (!TagExtractionResult.TryParseStorageValue(extractionStatus, out disposition))
            {
                // Existing nonempty cache rows predate explicit dispositions and are still usable.
                // Existing empty rows are ambiguous (tagless vs transient failure), so force one honest re-read.
                disposition = TagExtractionResult.Classify(tags);
                if (disposition == TagExtractionDisposition.Tagless)
                {
                    tags = null;
                    durationSeconds = null;
                    return false;
                }
            }

            return true;
        }

        private void CacheTagsAndDuration(
            IFileInfo file,
            Dictionary<string, List<string>> tags,
            int? durationSeconds,
            TagExtractionDisposition disposition,
            bool persist = true)
        {
            if (!TryGetCacheKey(file, out var key))
            {
                return;
            }

            // Store a copy to protect the cache from accidental caller mutations.
            var entry = new TagCacheEntry(CloneTags(tags), durationSeconds, disposition, file.LastWriteTimeUtc, file.Length);
            _tagCache[key] = entry;

            if (persist && _fileTagCache != null && TryGetFileIdentity(file, out var path, out var mtimeNs, out var sizeBytes))
            {
                _fileTagCache.Upsert(
                    path,
                    mtimeNs,
                    sizeBytes,
                    SerializeTags(tags),
                    durationSeconds,
                    TagExtractionResult.ToStorageValue(disposition));
            }

            TrimCacheIfNeeded();
        }

        private static string SerializeTags(Dictionary<string, List<string>> tags)
        {
            if (tags == null)
            {
                return "{}";
            }

            var json = JsonConvert.SerializeObject(tags, Formatting.None);
            return string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }

        private static Dictionary<string, List<string>> DeserializeTags(string tagsJson)
        {
            if (string.IsNullOrWhiteSpace(tagsJson))
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var deserialized = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(tagsJson);
                if (deserialized == null)
                {
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var tags = new Dictionary<string, List<string>>(deserialized.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in deserialized)
                {
                    tags[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
                }

                return tags;
            }
            catch
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static bool TryGetFileIdentity(IFileInfo file, out string path, out long mtimeNs, out long sizeBytes)
        {
            path = null;
            mtimeNs = 0;
            sizeBytes = 0;

            if (file == null || string.IsNullOrWhiteSpace(file.FullName))
            {
                return false;
            }

            path = file.FullName;
            mtimeNs = Math.Max(0, file.LastWriteTimeUtc.Ticks - UnixEpochTicks) * 100;
            sizeBytes = file.Length;
            return true;
        }

        private void TrimCacheIfNeeded()
        {
            if (_tagCache.Count <= MaxCachedTagExtractions)
            {
                return;
            }

            // Simple cap: clear the cache if it grows unbounded (most callers process a bounded set of files per session).
            _logger.Debug("[TAG-EXTRACT] Tag extraction cache exceeded {0} entries, clearing", MaxCachedTagExtractions);
            _tagCache.Clear();
        }

        private static Dictionary<string, List<string>> CloneTags(Dictionary<string, List<string>> tags)
        {
            if (tags == null)
            {
                return null;
            }

            var copy = new Dictionary<string, List<string>>(tags.Count, tags.Comparer);
            foreach (var pair in tags)
            {
                copy[pair.Key] = pair.Value == null ? null : new List<string>(pair.Value);
            }

            return copy;
        }

        private static bool TryGetCacheKey(IFileInfo file, out string key)
        {
            key = null;
            if (file == null || string.IsNullOrWhiteSpace(file.FullName))
            {
                return false;
            }

            key = file.FullName;
            return true;
        }

        private sealed class TagCacheEntry
        {
            public TagCacheEntry(
                Dictionary<string, List<string>> tags,
                int? durationSeconds,
                TagExtractionDisposition disposition,
                DateTime lastWriteTimeUtc,
                long fileSize)
            {
                Tags = tags;
                DurationSeconds = durationSeconds;
                Disposition = disposition;
                LastWriteTimeUtc = lastWriteTimeUtc;
                FileSize = fileSize;
            }

            public Dictionary<string, List<string>> Tags { get; }
            public int? DurationSeconds { get; }
            public TagExtractionDisposition Disposition { get; }
            public DateTime LastWriteTimeUtc { get; }
            public long FileSize { get; }
        }

        public void WriteTags(BookFile bookFile, bool newDownload, bool force = false)
        {
            var extension = Path.GetExtension(bookFile.Path);
            if (MediaFileExtensions.CanWriteAudioTags(extension))
            {
                _audioTagService.WriteTags(bookFile, newDownload, force);
            }
            else if (bookFile.CalibreId > 0)
            {
                _eBookTagService.WriteTags(bookFile, newDownload, force);
            }
        }

        public void SyncTags(List<Edition> editions)
        {
            _audioTagService.SyncTags(editions);
            _eBookTagService.SyncTags(editions);
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByAuthor(int authorId, BookMediaType? mediaType = null)
        {
            var previews = mediaType != BookMediaType.Ebook
                ? _audioTagService.GetRetagPreviewsByAuthor(authorId)
                : new List<RetagBookFilePreview>();
            if (mediaType != BookMediaType.Audiobook)
            {
                previews.AddRange(_eBookTagService.GetRetagPreviewsByAuthor(authorId));
            }

            return previews.OrderBy(preview => preview.Title).ThenBy(preview => preview.Path).ToList();
        }

        public List<RetagBookFilePreview> GetRetagPreviewsByBook(int bookId, BookMediaType? mediaType = null)
        {
            var previews = mediaType != BookMediaType.Ebook
                ? _audioTagService.GetRetagPreviewsByBook(bookId)
                : new List<RetagBookFilePreview>();
            if (mediaType != BookMediaType.Audiobook)
            {
                previews.AddRange(_eBookTagService.GetRetagPreviewsByBook(bookId));
            }

            return previews.OrderBy(preview => preview.Title).ThenBy(preview => preview.Path).ToList();
        }

        public void Execute(RetagFilesCommand message)
        {
            var ids = (message.Files ?? new List<int>()).Distinct().ToList();
            var files = _mediaFileService.GetExisting(ids);
            var selection = new TagBatch();
            selection.MissingFiles.UnionWith(ids.Except(files.Select(file => file.Id)));
            var ebook = RunRetag(batch => _eBookTagService.RetagFiles(message, files, batch), true);
            var audio = RunRetag(batch => _audioTagService.RetagFiles(message, files, batch), false);
            ReportRetagResults(message, ebook, audio, selection);
        }

        public void Execute(RetagAuthorCommand message)
        {
            var mediaType = BookMediaTypeParser.ParseOptional(message.MediaType);
            var ids = (message.AuthorIds ?? new List<int>()).Distinct().ToList();
            var authors = _authorService.GetExistingAuthors(ids);
            var selection = new TagBatch();
            selection.MissingAuthors.UnionWith(ids.Except(authors.Select(author => author.Id)));
            var ebook = mediaType == BookMediaType.Audiobook ? null
                : RunRetag(batch => _eBookTagService.RetagAuthor(message, authors, batch), true);
            var audio = mediaType == BookMediaType.Ebook ? null
                : RunRetag(batch => _audioTagService.RetagAuthor(message, authors, batch), false);
            ReportRetagResults(message, ebook, audio, selection);
        }

        private void ReportRetagResults(RetagCommand command, TagBatch ebook, TagBatch audio, TagBatch selection)
        {
            var results = new[] { ebook, audio, selection }.Where(batch => batch != null).ToList();
            var messages = new List<string>();
            if (ebook != null)
            {
                AddSummary(messages, "RetagEbookPass", ebook);
            }
            if (audio != null)
            {
                AddSummary(messages, "RetagAudioPass", audio);
            }
            AddCountMessage(messages, "RetagMissingFiles", selection.MissingFiles.Count);
            AddCountMessage(messages, "RetagMissingAuthors", selection.MissingAuthors.Count);
            AddCountMessage(messages, "RetagUnlinkedFiles", results.SelectMany(batch => batch.UnlinkedFiles).Distinct().Count());
            AddCountMessage(messages, "RetagEbookRequiresCalibre", ebook?.NotInCalibre ?? 0);
            AddCountMessage(messages, "RetagUnsupportedFiles", audio?.Unsupported ?? 0);
            AddCountMessage(messages, "RetagRootNotLinkedToCalibre", ebook?.RootNotLinkedToCalibre ?? 0);
            AddCountMessage(messages, "RetagMissingRootFolder", ebook?.MissingRootFolder ?? 0);
            if (ebook != null)
            {
                foreach (var (root, count) in ebook.InvalidCalibreSettings)
                {
                    messages.Add(_localizationService.GetLocalizedString("RetagInvalidCalibreSettings", new Dictionary<string, object> { ["rootFolderPath"] = root, ["count"] = count }));
                }

                foreach (var (library, count) in ebook.MissingCalibreBooks)
                {
                    messages.Add(_localizationService.GetLocalizedString("RetagMissingCalibreBook", new Dictionary<string, object> { ["library"] = LibraryLabel(library), ["count"] = count }));
                }
            }
            AddCountMessage(messages, "RetagSkippedSymlinkWrites", results.SelectMany(batch => batch.SkippedSymlinkPaths).Distinct(PathEqualityComparer.Instance).Count());
            var now = DateTime.UtcNow;
            foreach (var batch in results)
            {
                if (batch.Stopped)
                {
                    messages.Add(_localizationService.GetLocalizedString(batch.CurrentAuthor == null ? "RetagFilePassStopped" : "RetagAuthorPassStopped", new Dictionary<string, object>
                    {
                        ["pass"] = _localizationService.GetLocalizedString(batch == ebook ? "RetagEbookPass" : "RetagAudioPass"),
                        ["author"] = batch.CurrentAuthor,
                    }));
                    AddCountMessage(messages, "RetagLaterAuthorsNotAttempted", batch.UnattemptedAuthors);
                }

                foreach (var library in batch.UnavailableLibraries)
                {
                    messages.Add(_localizationService.GetLocalizedString(library.Value.RetryAfter <= now ? "RetagCalibreRetryNow" : "RetagCalibreBackoff", new Dictionary<string, object>
                    {
                        ["library"] = LibraryLabel(library.Key), ["count"] = library.Value.Count,
                        ["retryAfter"] = library.Value.RetryAfter.ToString("u")
                    }));
                    if (library.Value.TriggerFile != null)
                    {
                        messages.Add(_localizationService.GetLocalizedString("RetagBackoffTrigger", new Dictionary<string, object> { ["file"] = library.Value.TriggerFile }));
                    }
                }
            }

            if (results.Any(batch => batch.UnavailableLibraries.Values.Any(library => library.RetryAfter > now)))
            {
                messages.Add(_localizationService.GetLocalizedString("RetagRunAgain"));
            }
            command.ResultMessage = messages.Count > 0 ? string.Join(" ", messages) : _localizationService.GetLocalizedString("SuccessMyWorkIsDoneNoFilesToRetag");
            if (results.Any(batch => batch.Unsuccessful))
            {
                _commandResultReporter.Report(CommandResult.Unsuccessful);
                _logger.ProgressWarn(command.ResultMessage);
            }
            else
            {
                _logger.ProgressInfo(command.ResultMessage);
            }
        }

        private TagBatch RunRetag(Func<TagBatch, TagBatch> retag, bool ebook)
        {
            var batch = new TagBatch();
            try
            {
                return retag(batch);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to complete {0} Retag pass; continuing other selected media", ebook ? "ebook" : "audio");
                batch.Stopped = true;
                return batch;
            }
            finally
            {
                batch.Timer.Stop();
            }
        }

        private void AddSummary(List<string> messages, string pass, TagBatch batch)
        {
            var parts = new List<string>();
            AddCountMessage(parts, "RetagWrittenCount", batch.Written);
            AddCountMessage(parts, "RetagFailedCount", batch.Failed);
            AddCountMessage(parts, "RetagUnconfirmedCount", batch.Unconfirmed);
            AddCountMessage(parts, "RetagAudioIncompleteCount", batch.AudioIncomplete);
            AddCountMessage(parts, "RetagCalibreIncompleteCount", batch.CalibreIncomplete);
            AddCountMessage(parts, "RetagCalibreDetailsIncompleteCount", batch.CalibreDetailsIncomplete);
            AddCountMessage(parts, "RetagSkippedCount", batch.Skipped);
            AddCountMessage(parts, "RetagRestoredTotalsCount", batch.RestoredTotals);
            if (parts.Count > 0)
            {
                messages.Add(_localizationService.GetLocalizedString("RetagPassSummary", new Dictionary<string, object>
                {
                    ["pass"] = _localizationService.GetLocalizedString(pass),
                    ["outcomes"] = string.Join(_localizationService.GetLocalizedString("RetagOutcomeSeparator"), parts),
                    ["elapsed"] = batch.Timer.ElapsedMilliseconds
                }));
            }
        }

        private string LibraryLabel(string library) => _localizationService.GetLocalizedString("RetagCalibreLibrary", new Dictionary<string, object> { ["library"] = library });

        private void AddCountMessage(List<string> messages, string key, int count)
        {
            if (count > 0)
            {
                messages.Add(_localizationService.GetLocalizedString(key, new Dictionary<string, object> { ["count"] = count }));
            }
        }
    }
}
