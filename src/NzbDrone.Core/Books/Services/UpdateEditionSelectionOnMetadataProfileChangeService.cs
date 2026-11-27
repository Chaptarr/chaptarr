using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Metadata.Events;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;

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
        private readonly IManageCommandQueue _commandQueue;
        private readonly IDeferredCoverService _deferredCoverService;
        private readonly IEventAggregator _eventAggregator;

        public UpdateEditionSelectionOnMetadataProfileChangeService(
            IAuthorService authorService,
            IBookService bookService,
            IEditionService editionService,
            IMediaFileService mediaFileService,
            IEditionSelector editionSelector,
            IEditionMetadataProfileFilter editionMetadataProfileFilter,
            Logger logger,
            IDeferredCoverService deferredCoverService,
            IManageCommandQueue commandQueue,
            IEventAggregator eventAggregator)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _editionSelector = editionSelector;
            _editionMetadataProfileFilter = editionMetadataProfileFilter;
            _logger = logger;
            _commandQueue = commandQueue;
            _deferredCoverService = deferredCoverService;
            _eventAggregator = eventAggregator;
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

            var timer = Stopwatch.StartNew();
            var affectedAuthors = _authorService.GetAllAuthors()
                .Where(a => a.AudiobookMetadataProfileId == profile.Id || a.EbookMetadataProfileId == profile.Id)
                .ToList();

            if (!affectedAuthors.Any())
            {
                _logger.Debug("No authors are using metadata profile {0}, nothing to update", profile.Id);
                return;
            }

            var examinedBooks = 0;
            var changedBookIds = new List<int>();
            try
            {
                foreach (var author in affectedAuthors)
                {
                    try
                    {
                        UpdateAuthorEditionSelection(author, profile, changedBookIds, ref examinedBooks);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Error(ex, "Error updating edition selection for author {0}", author.Name);
                    }
                }
            }
            finally
            {
                DownloadBookMediaCommand.NotifyBookSelectionsChanged(changedBookIds, _commandQueue, _deferredCoverService, _logger);
                _logger.Info("Metadata profile {0}: {1} authors, {2} affected books, {3} changed selections in {4}ms",
                    profile.Id, affectedAuthors.Count, examinedBooks, changedBookIds.Count, timer.ElapsedMilliseconds);
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

        private void UpdateAuthorEditionSelection(Author author, MetadataProfile changedProfile, ICollection<int> changedBookIds, ref int examinedBooks)
        {
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

            // GetBooksByAuthor already batch-loads the editions; do not read them again per book.
            var books = _bookService.GetBooksByAuthor(author.Id).Where(book => affectedMediaTypes.Contains(book.MediaType)).ToList();
            examinedBooks += books.Count;
            if (books.Count == 0)
            {
                return;
            }

            var booksWithFiles = _mediaFileService.GetFilesByBooks(books.Select(book => book.Id).ToList())
                .Select(file => file.Edition.BookId).ToHashSet();

            foreach (var book in books)
            {
                try
                {
                    // Skip books with physical files - we show what we have regardless of language preference
                    if (booksWithFiles.Contains(book.Id))
                    {
                        _logger.Debug("Skipping book '{0}' - has physical files", book.Title);
                        continue;
                    }

                    var editions = book.Editions;
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

                            var storedBook = BookService.CloneStoredBook(book);

                            // Update monitored status on editions
                            foreach (var edition in editions)
                            {
                                edition.Monitored = (edition.Id == bestEdition.Id);
                            }

                            _editionService.UpdateSelection(editions);
                            changedBookIds.Add(book.Id);

                            // ForeignEditionId is not persisted on Books; the edition selection is authoritative.
                            // Keep the existing UI/statistics notifications without rewriting an older Book snapshot.
                            book.ForeignEditionId = bestEdition.ForeignEditionId;
                            var change = new BookEditedEvent(book, storedBook);
                            _eventAggregator.PublishEvent(change);
                            _eventAggregator.PublishEvent(new BooksEditedEvent(new[] { change }));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn(ex, "Unable to update edition selection for book {0} ({1}); continuing with the next book", book.Title, book.Id);
                }
            }
        }
    }
}
