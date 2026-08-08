using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport.Aggregation.Aggregators
{
    public class AggregateNarratorFromMetadata : IAggregate<LocalEdition>
    {
        private readonly INarratorSearchService _narratorSearchService;
        private readonly Logger _logger;

        public AggregateNarratorFromMetadata(
            INarratorSearchService narratorSearchService,
            Logger logger)
        {
            _narratorSearchService = narratorSearchService;
            _logger = logger;
        }

        public LocalEdition Aggregate(LocalEdition localEdition, bool otherFiles)
        {
            _logger.Debug("AggregateNarratorFromMetadata called for edition with {0} books", localEdition.LocalBooks.Count);

            // Only proceed if we have at least author and title information
            var firstBook = localEdition.LocalBooks.FirstOrDefault();
            if (firstBook == null)
            {
                _logger.Debug("No local books in edition, skipping narrator enrichment");
                return localEdition;
            }

            // IMPORTANT: Skip narrator enrichment for ebooks - they don't have narrators!
            if (IsEbookFile(firstBook))
            {
                _logger.Debug("Detected ebook file - skipping narrator enrichment (ebooks don't have narrators)");
                return localEdition;
            }

            // IMPORTANT: Only enrich narrator for books with physical files
            // Missing books should have NULL narrator until user manually selects one
            var hasPhysicalFiles = localEdition.LocalBooks.Any(lb => !string.IsNullOrEmpty(lb.Path));
            if (!hasPhysicalFiles)
            {
                _logger.Debug("No physical files in edition, skipping narrator enrichment for missing book");
                return localEdition;
            }

            var authorName = firstBook.Author?.Name;
            var bookTitle = firstBook.Book?.Title;

            _logger.Debug("Narrator enrichment - Author: {0}, Title: {1}", authorName ?? "null", bookTitle ?? "null");

            if (authorName.IsNullOrWhiteSpace() || bookTitle.IsNullOrWhiteSpace())
            {
                _logger.Debug("Cannot enrich narrator - missing author or title information");
                return localEdition;
            }

            // IMPORTANT: Skip ALL external narrator API calls during import
            // Narrator matching will be done locally during edition selection
            if (localEdition.IsImportContext)
            {
                _logger.Debug("Skipping external narrator APIs during import - will use local database matching during edition selection");
                return localEdition;
            }

            // IMPORTANT: We ignore any narrator info from local file tags
            // Only use narrator data from trusted metadata sources
            _logger.Debug("Searching for narrator from metadata sources for {0} by {1}", bookTitle, authorName);

            try
            {
                _logger.Debug("Starting narrator search for {0} by {1}", bookTitle, authorName);
                _logger.ProgressInfo("Searching for narrator: {0} by {1}", bookTitle, authorName);

                var narrators = _narratorSearchService.SearchForNarrators(authorName, bookTitle, useCache: true);
                _logger.Debug("Narrator search (author/title) returned {0} narrators", narrators?.Count ?? 0);

                var primaryNarrator = narrators?.FirstOrDefault();

                if (narrators != null && narrators.Any())
                {
                    _logger.Debug("Found narrator '{0}' for book '{1}' by '{2}'", primaryNarrator, bookTitle, authorName);

                    // Apply narrator to all local books in this edition
                    foreach (var localBook in localEdition.LocalBooks)
                    {
                        if (localBook.Narrator.IsNullOrWhiteSpace())
                        {
                            localBook.Narrator = primaryNarrator;
                        }
                    }

                    // Store narrator options for later use
                    if (localEdition.Edition?.Book?.Id > 0)
                    {
                        _narratorSearchService.StoreNarratorOptions(localEdition.Edition.Book.Id, narrators, "import");
                    }
                }
                else
                {
                    _logger.Debug("No narrator information found for {0} by {1}", bookTitle, authorName);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to enrich narrator information for {0} by {1}", bookTitle, authorName);
            }

            return localEdition;
        }

        private bool IsEbookFile(LocalBook localBook)
        {
            // Check by file extension first
            if (!string.IsNullOrEmpty(localBook.Path))
            {
                var extension = Path.GetExtension(localBook.Path)?.ToLowerInvariant();
                if (MediaFileExtensions.TextExtensions.Contains(extension))
                {
                    return true;
                }
            }

            // Check by quality if available
            if (localBook.Quality?.Quality != null)
            {
                var qualityId = localBook.Quality.Quality.Id;
                if (qualityId == Quality.PDF.Id ||
                    qualityId == Quality.MOBI.Id ||
                    qualityId == Quality.EPUB.Id ||
                    qualityId == Quality.AZW3.Id)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
