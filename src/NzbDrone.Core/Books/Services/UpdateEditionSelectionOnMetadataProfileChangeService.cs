using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Metadata.Events;

namespace NzbDrone.Core.Books.Services
{
    public class UpdateEditionSelectionOnMetadataProfileChangeService : IHandle<MetadataProfileUpdatedEvent>
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionSelector _editionSelector;
        private readonly IEditionMetadataProfileFilter _editionMetadataProfileFilter;
        private readonly Logger _logger;

        public UpdateEditionSelectionOnMetadataProfileChangeService(
            IAuthorService authorService,
            IBookService bookService,
            IEditionService editionService,
            IMediaFileService mediaFileService,
            IEditionSelector editionSelector,
            IEditionMetadataProfileFilter editionMetadataProfileFilter,
            Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _editionSelector = editionSelector;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
            _logger = logger;
        }

        public void Handle(MetadataProfileUpdatedEvent message)
        {
            var profile = message.MetadataProfile;

            if (profile == null)
            {
                return;
            }

            // Only process if the effective language filter changed. Older callers may not
            // provide a previous profile, so keep the legacy behavior in that case.
            if (!AllowedLanguagesChanged(message.PreviousMetadataProfile, profile))
            {
                _logger.Debug("Metadata profile '{0}' language settings unchanged, skipping edition re-selection", profile.Name);
                return;
            }

            if (string.IsNullOrWhiteSpace(profile.AllowedLanguages))
            {
                _logger.Debug("Metadata profile '{0}' has no language restrictions, skipping edition re-selection", profile.Name);
                return;
            }

            _logger.Info("Metadata profile '{0}' (ID: {1}) language settings changed to: {2}, updating edition selection for affected books",
                profile.Name, profile.Id, profile.AllowedLanguages);

                // Find all authors using this metadata profile
                var affectedAuthors = _authorService.GetAllAuthors()
                    .Where(a => a.AudiobookMetadataProfileId == profile.Id ||
                               a.EbookMetadataProfileId == profile.Id)
                    .ToList();

            if (!affectedAuthors.Any())
            {
                _logger.Debug("No authors are using metadata profile {0}, nothing to update", profile.Id);
                return;
            }

            _logger.Info("Found {0} authors using metadata profile {1}", affectedAuthors.Count, profile.Id);

            foreach (var author in affectedAuthors)
            {
                try
                {
                    UpdateAuthorEditionSelection(author, profile);
                }
                catch (System.Exception ex)
                {
                    _logger.Error(ex, "Error updating edition selection for author {0}", author.Name);
                }
            }
        }

        private static bool AllowedLanguagesChanged(MetadataProfile previousProfile, MetadataProfile profile)
        {
            if (previousProfile == null)
            {
                return true;
            }

            return !string.Equals(
                NormalizeAllowedLanguages(previousProfile.AllowedLanguages),
                NormalizeAllowedLanguages(profile.AllowedLanguages),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAllowedLanguages(string allowedLanguages)
        {
            EditionMetadataProfileFilter.ParseAllowedLanguages(
                allowedLanguages,
                out var languages,
                out var allowUnknownLanguage,
                out var configured,
                out _);

            if (!configured)
            {
                return string.Empty;
            }

            var tokens = languages
                .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allowUnknownLanguage)
            {
                tokens.Add("null");
            }

            return string.Join(",", tokens);
        }

        private void UpdateAuthorEditionSelection(Author author, MetadataProfile changedProfile)
        {
            var allBooks = _bookService.GetBooksByAuthor(author.Id);
            if (!allBooks.Any())
            {
                return;
            }

            // Determine which media types are affected by this profile change.
            // Authors can legitimately use the same metadata profile for both audiobook and ebook.
            var affectedMediaTypes = new System.Collections.Generic.HashSet<BookMediaType>();
            if (author.AudiobookMetadataProfileId == changedProfile.Id)
            {
                affectedMediaTypes.Add(BookMediaType.Audiobook);
            }

            if (author.EbookMetadataProfileId == changedProfile.Id)
            {
                affectedMediaTypes.Add(BookMediaType.Ebook);
            }

            if (affectedMediaTypes.Count == 0)
            {
                return;
            }

            foreach (var book in allBooks)
            {
                // Skip books with physical files - we show what we have regardless of language preference
                if (_mediaFileService.GetFilesByBook(book.Id).Any())
                {
                    _logger.Debug("Skipping book '{0}' - has physical files", book.Title);
                    continue;
                }

                // Skip if not the right media type for this profile
                if (!affectedMediaTypes.Contains(book.MediaType))
                {
                    continue;
                }

                // Get all editions for this book
                var editions = _editionService.GetEditionsByBook(book.Id);
                if (!editions.Any())
                {
                    continue;
                }

                // Skip if any edition is manually selected
                if (editions.Any(e => e.ManualAdd))
                {
                    _logger.Debug("Skipping book '{0}' - has manually selected edition", book.Title);
                    continue;
                }

                var filteredEditions = _editionMetadataProfileFilter.Apply(editions, changedProfile);
                var retainedSelection = _editionSelector.SelectRetainedEditions(
                    book.MediaType,
                    filteredEditions);

                var eligibleEditions = retainedSelection?.RetainedEditions?.ToList() ?? filteredEditions;
                if (!eligibleEditions.Any())
                {
                    continue;
                }

                var bestEdition = _editionSelector.SelectBestEdition(eligibleEditions, book.MediaType);

                if (bestEdition != null)
                {
                    // Check if the selected edition changed
                    var currentSelectedEdition = editions.FirstOrDefault(e => e.Monitored);

                    if (currentSelectedEdition == null || currentSelectedEdition.Id != bestEdition.Id)
                    {
                        _logger.Info("Book '{0}': Changing selected edition from '{1}' to '{2}' based on retained edition ranking",
                            book.Title,
                            currentSelectedEdition?.Title ?? "none",
                            bestEdition.Title);

                        // Update monitored status on editions
                        foreach (var edition in editions)
                        {
                            edition.Monitored = (edition.Id == bestEdition.Id);
                        }

                        _editionService.UpdateMany(editions);

                        if (!string.Equals(book.ForeignEditionId, bestEdition.ForeignEditionId, StringComparison.OrdinalIgnoreCase))
                        {
                            book.ForeignEditionId = bestEdition.ForeignEditionId;
                            _bookService.UpdateBook(book);
                        }

                        // Update book title if it should reflect the new edition
                        if (bestEdition.Title != book.Title)
                        {
                            _logger.Debug("Updating book title from '{0}' to '{1}' to match selected edition",
                                book.Title, bestEdition.Title);
                            // Note: We might want to preserve the original book title and only change display
                            // This depends on how the system handles book titles vs edition titles
                        }
                    }
                }
            }
        }
    }
}
