using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    /// <summary>
    /// Minimal implementation of IMakeImportDecision that bypasses identification
    /// and simply accepts whatever the user has manually selected
    /// </summary>
    public class SimpleImportDecisionMaker : IMakeImportDecision
    {
        private readonly IMetadataTagService _metadataTagService;
        private readonly IFileMatchingService _fileMatchingService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        private sealed class ManualPreviewCandidate
        {
            public LocalBook LocalBook { get; set; }
            public DiscoveredFileWithMetadata Discovered { get; set; }
            public BookMediaType MediaType { get; set; }
        }

        public SimpleImportDecisionMaker(IMetadataTagService metadataTagService,
                                         IFileMatchingService fileMatchingService,
                                         IAuthorService authorService,
                                         IBookService bookService,
                                         IEditionService editionService,
                                         IMediaFileService mediaFileService,
                                         Logger logger)
        {
            _metadataTagService = metadataTagService;
            _fileMatchingService = fileMatchingService;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public List<ImportDecision<LocalBook>> GetImportDecisions(
            List<IFileInfo> musicFiles, 
            IdentificationOverrides idOverrides, 
            ImportDecisionMakerInfo itemInfo, 
            ImportDecisionMakerConfig config,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decisions = new List<ImportDecision<LocalBook>>();

            if (config != null && config.Filter != FilterFilesType.None)
            {
                musicFiles = _mediaFileService.FilterUnchangedFiles(musicFiles, config.Filter);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var bookCache = new Dictionary<int, Book>();
            var editionCache = new Dictionary<int, Edition>();
            var manualPreviewCandidates = new List<ManualPreviewCandidate>();
            
            _logger.Debug("SimpleImportDecisionMaker processing {0} files", musicFiles.Count);

            foreach (var file in musicFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Detect quality from file extension
                    var fileExtension = Path.GetExtension(file.FullName);
                    var detectedQuality = MediaFileExtensions.GetQualityForExtension(fileExtension);
                    
                    // Read tags for all supported media types (field-agnostic, multi-value)
                    RawFileTags rawTags = null;
                    int? durationSeconds = null;
                    try
                    {
                        var (all, extractedDurationSeconds) = _metadataTagService.ReadAllTagsAndDuration(file);
                        durationSeconds = extractedDurationSeconds;
                        rawTags = new RawFileTags { AllTags = all ?? new Dictionary<string, List<string>>() };
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to read tags for file '{0}'", file.FullName);
                    }
                    
                    if (detectedQuality == Quality.Unknown)
                    {
                        _logger.Debug("Unknown quality for file {0} with extension {1}", file.FullName, fileExtension);
                    }
                    
                    var localBook = new LocalBook
                    {
                        Path = file.FullName,
                        Size = file.Length,
                        Modified = file.LastWriteTimeUtc,
                        DurationSeconds = durationSeconds,
                        RawTags = rawTags,
                        ExistingFile = !config.NewDownload,
                        Quality = new QualityModel(detectedQuality)
                    };

                    // Apply user's manual selections if provided
                    if (idOverrides != null)
                    {
                        localBook.Author = idOverrides.Author;
                        localBook.Book = idOverrides.Book;
                        localBook.Edition = idOverrides.Edition;
                    }

                    // For initial manual-import UI load (itemInfo != null), try to suggest a match from the library.
                    // For UpdateItems (itemInfo == null), do NOT auto-override user selections.
                    var shouldSuggestMatch = itemInfo != null;
                    if (shouldSuggestMatch && (localBook.Book == null || localBook.Edition == null))
                    {
                        var mediaType = MediaFileExtensions.AudioExtensions.Contains(fileExtension) ? BookMediaType.Audiobook :
                                        MediaFileExtensions.TextExtensions.Contains(fileExtension) ? BookMediaType.Ebook :
                                        (BookMediaType?)null;

                        if (mediaType != null)
                        {
                            var discoveredTags = ToCaseInsensitiveTags(rawTags?.AllTags);
                            var discovered = new DiscoveredFileWithMetadata
                            {
                                Path = file.FullName,
                                Size = file.Length,
                                Modified = file.LastWriteTimeUtc,
                                AllTags = discoveredTags,
                                Quality = localBook.Quality,
                                DurationSeconds = durationSeconds
                            };

                            manualPreviewCandidates.Add(new ManualPreviewCandidate
                            {
                                LocalBook = localBook,
                                Discovered = discovered,
                                MediaType = mediaType.Value
                            });
                        }
                    }

                    // If a book is known but an edition isn't, prefer a monitored edition (or first) as a safe UI default.
                    if (localBook.Book != null && localBook.Edition == null)
                    {
                        localBook.Edition = localBook.Book.Editions?.FirstOrDefault(e => e.Monitored)
                                            ?? localBook.Book.Editions?.FirstOrDefault();
                    }

                    // Create an approved decision
                    var decision = new ImportDecision<LocalBook>(localBook);
                    decisions.Add(decision);
                    
                    _logger.Trace("Created import decision for {0}", file.FullName);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error processing file {0}", file.FullName);
                    
                    // Create a rejected decision for files we can't process
                    var failedBook = new LocalBook { Path = file.FullName };
                    var rejection = new Rejection("Failed to process file: " + ex.Message);
                    decisions.Add(new ImportDecision<LocalBook>(failedBook, rejection));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ApplyManualPreviewMatches(manualPreviewCandidates, idOverrides, bookCache, editionCache, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var decision in decisions)
            {
                var localBook = decision.Item;
                if (localBook?.Book != null && localBook.Edition == null)
                {
                    localBook.Edition = localBook.Book.Editions?.FirstOrDefault(e => e.Monitored)
                                        ?? localBook.Book.Editions?.FirstOrDefault();
                }
            }

            _logger.Debug("SimpleImportDecisionMaker created {0} decisions", decisions.Count);
            return decisions;
        }

        private void ApplyManualPreviewMatches(
            List<ManualPreviewCandidate> candidates,
            IdentificationOverrides idOverrides,
            Dictionary<int, Book> bookCache,
            Dictionary<int, Edition> editionCache,
            CancellationToken cancellationToken)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return;
            }

            try
            {
                var restrictToAuthorId = idOverrides?.Author?.Id > 0 ? idOverrides.Author.Id : (int?)null;
                var context = MatchingContextPresets.ForManualPreview();
                context.CancellationToken = cancellationToken;
                var result = _fileMatchingService
                    .MatchFilesToLibraryAsync(candidates.Select(c => c.Discovered).ToArray(), restrictToAuthorId, context)
                    .GetAwaiter()
                    .GetResult();

                var matchesByPath = (result?.MatchedFiles ?? Array.Empty<FileMatch>())
                    .Where(m => m?.File?.Path != null)
                    .GroupBy(m => m.File.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var unmatchedByPath = (result?.UnmatchedFiles ?? Array.Empty<UnmatchedFile>())
                    .Where(u => u?.File?.Path != null)
                    .GroupBy(u => u.File.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var candidate in candidates)
                {
                    if (candidate?.LocalBook == null || candidate.Discovered?.Path == null)
                    {
                        continue;
                    }

                    if (matchesByPath.TryGetValue(candidate.Discovered.Path, out var match))
                    {
                        ApplyMatch(candidate.LocalBook, match, bookCache, editionCache);
                        continue;
                    }

                    if (!unmatchedByPath.TryGetValue(candidate.Discovered.Path, out var unmatched))
                    {
                        continue;
                    }

                    var suggestion = unmatched.PotentialAuthors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.ProviderId));
                    if (suggestion == null)
                    {
                        continue;
                    }

                    candidate.LocalBook.SuggestedForeignAuthorId = suggestion.ProviderId;
                    candidate.LocalBook.SuggestedAuthorName = suggestion.AuthorName;
                    candidate.LocalBook.SuggestedForeignBookId = suggestion.BookProviderId;
                    candidate.LocalBook.SuggestedBookTitle = suggestion.BookTitle;
                    candidate.LocalBook.SuggestedForeignEditionId = suggestion.EditionHardcoverId;
                    candidate.LocalBook.SuggestedEditionTitle = suggestion.EditionTitle;

                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Grouped manual-import preview matching failed; leaving rows for manual selection");
            }
        }

        private void ApplyMatch(
            LocalBook localBook,
            FileMatch match,
            Dictionary<int, Book> bookCache,
            Dictionary<int, Edition> editionCache)
        {
            if (localBook == null || match == null || match.BookId <= 0)
            {
                return;
            }

            try
            {
                if (!bookCache.TryGetValue(match.BookId, out var matchedBook))
                {
                    matchedBook = _bookService.GetBook(match.BookId);
                    bookCache[match.BookId] = matchedBook;
                }

                if (matchedBook == null)
                {
                    return;
                }

                localBook.Book = matchedBook;
                localBook.Author = matchedBook.Author;

                if (!editionCache.TryGetValue(match.EditionId, out var matchedEdition))
                {
                    if (matchedBook.Editions != null && matchedBook.Editions.Any())
                    {
                        matchedEdition = matchedBook.Editions.FirstOrDefault(e => e.Id == match.EditionId);
                    }

                    matchedEdition ??= _editionService.GetEdition(match.EditionId);
                    editionCache[match.EditionId] = matchedEdition;
                }

                localBook.Edition = matchedEdition;
                localBook.MatchProvenance = match.Provenance;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to load matched book/edition for file '{0}' (BookId={1}, EditionId={2})",
                    localBook.Path, match.BookId, match.EditionId);
            }
        }

        private static Dictionary<string, List<string>> ToCaseInsensitiveTags(Dictionary<string, List<string>> tags)
        {
            var output = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null || tags.Count == 0)
            {
                return output;
            }

            foreach (var kv in tags)
            {
                if (kv.Value == null)
                {
                    continue;
                }

                output[kv.Key] = kv.Value;
            }

            return output;
        }

    }
}
