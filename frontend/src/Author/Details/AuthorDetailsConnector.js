/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { setSelectedMediaType } from 'Store/Actions/appActions';
import { toggleAuthorMonitored, updateAuthorMediaType, fetchAuthorMediaTypeSize } from 'Store/Actions/authorActions';
import { clearBooks, fetchBooks } from 'Store/Actions/bookActions';
import { clearBookFiles, fetchBookFiles } from 'Store/Actions/bookFileActions';
import { saveBookEditor } from 'Store/Actions/bookIndexActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { clearQueueDetails, fetchQueueDetails } from 'Store/Actions/queueActions';
import { cancelFetchReleases, clearReleases } from 'Store/Actions/releaseActions';
import { clearSeries, fetchSeries } from 'Store/Actions/seriesActions';
import { fetchRootFolders } from 'Store/Actions/settingsActions';
import createAllAuthorSelector from 'Store/Selectors/createAllAuthorsSelector';
import createCommandsSelector from 'Store/Selectors/createCommandsSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createSortedSectionSelector from 'Store/Selectors/createSortedSectionSelector';
import { findCommand, isCommandExecuting } from 'Utilities/Command';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import { registerPagePopulator, unregisterPagePopulator } from 'Utilities/pagePopulator';
import { FolderType, coerceFolderType } from 'Helpers/Props/folderTypes';
import AuthorDetails from './AuthorDetails';

const selectBooks = createSelector(
  (state) => state.books,
  (state) => state.bookIndex,
  (books, index) => {
    const {
      items,
      isFetching,
      isPopulated,
      error
    } = books;

    const {
      isSaving,
      saveError,
      isDeleting,
      deleteError
    } = index;

    const hasBooks = !!items.length;
    const hasMonitoredBooks = items.some((e) => e.monitored);

    return {
      isBooksFetching: isFetching,
      isBooksPopulated: isPopulated,
      booksError: error,
      hasBooks,
      hasMonitoredBooks,
      isSaving,
      saveError,
      isDeleting,
      deleteError
    };
  }
);

const selectSeries = createSelector(
  createSortedSectionSelector('series', (a, b) => a.title.localeCompare(b.title)),
  (state) => state.series,
  (series) => {
    const {
      items,
      isFetching,
      isPopulated,
      error
    } = series;

    const hasSeries = !!items.length;

    return {
      isSeriesFetching: isFetching,
      isSeriesPopulated: isPopulated,
      seriesError: error,
      hasSeries,
      series: series.items
    };
  }
);

const selectBookFiles = createSelector(
  (state) => state.bookFiles,
  (bookFiles) => {
    const {
      items,
      isFetching,
      isPopulated,
      error
    } = bookFiles;

    const hasBookFiles = !!items.length;

    return {
      isBookFilesFetching: isFetching,
      isBookFilesPopulated: isPopulated,
      bookFilesError: error,
      hasBookFiles
    };
  }
);

function createMapStateToProps() {
  return createSelector(
    (state, props) => props.id,
    selectBooks,
    selectSeries,
    selectBookFiles,
    createAllAuthorSelector(),
    createCommandsSelector(),
    createDimensionsSelector(),
    (state) => state.settings.rootFolders,
    (state) => state.app.hideUnmonitoredMissing,
    (state) => state.app.selectedMediaType,
    (state) => state.books.items,
    (state) => state.bookFiles.items,
    (state) => state.authorDetails.filterValue,
    (authorId, books, series, bookFiles, allAuthors, commands, dimensions, rootFoldersState, hideUnmonitoredMissing = false, globalSelectedMediaType, allBookItems, allBookFileItems, filterValue) => {
      try {
        const sortedAuthor = _.orderBy(allAuthors, 'sortNameLastFirst');
        const authorIndex = _.findIndex(sortedAuthor, { id: authorId });
        const author = sortedAuthor[authorIndex];

        if (!author) {
          // Return minimal props to prevent undefined errors
          return {
            authorLoaded: false,
            selectedMediaType: 'audiobook',
            hideUnmonitoredMissing: hideUnmonitoredMissing ?? false,
            rootFoldersPopulated: rootFoldersState?.isPopulated || false,
            hasAudiobookRootFolder: false,
            hasEbookRootFolder: false,
            hasAudiobookRootFoldersAvailable: false,
            hasEbookRootFoldersAvailable: false,
            authorHasAudiobookConfig: false,
            authorHasEbookConfig: false
          };
        }

        const {
          isBooksFetching,
          isBooksPopulated,
          booksError,
          isSaving,
          saveError,
          isDeleting,
          deleteError
        } = books;

        const {
          isSeriesFetching,
          isSeriesPopulated,
          seriesError,
          hasSeries,
          series: seriesItems
        } = series;

	        const {
	          isBookFilesFetching,
	          isBookFilesPopulated,
	          bookFilesError
	        } = bookFiles;

        const previousAuthor = sortedAuthor[authorIndex - 1] || _.last(sortedAuthor);
        const nextAuthor = sortedAuthor[authorIndex + 1] || _.first(sortedAuthor);
        const isAuthorRefreshing = isCommandExecuting(findCommand(commands, { name: commandNames.REFRESH_AUTHOR, authorId: author.id }));
        const authorRefreshingCommand = findCommand(commands, { name: commandNames.REFRESH_AUTHOR });
        const allAuthorRefreshing = (
          authorRefreshingCommand &&
        isCommandExecuting(authorRefreshingCommand) &&
        authorRefreshingCommand.body &&
        !authorRefreshingCommand.body.authorId
        );
        const isRefreshing = isAuthorRefreshing || allAuthorRefreshing;
        const isSearching = isCommandExecuting(findCommand(commands, { name: commandNames.AUTHOR_SEARCH, authorId: author.id }));
        const isRenamingFiles = isCommandExecuting(findCommand(commands, { name: commandNames.RENAME_FILES, authorId: author.id }));
        const isRenamingAuthorCommand = findCommand(commands, { name: commandNames.RENAME_AUTHOR });
        const isRenamingAuthor = (
          isRenamingAuthorCommand &&
        isCommandExecuting(isRenamingAuthorCommand) &&
        isRenamingAuthorCommand.body &&
        isRenamingAuthorCommand.body.authorIds &&
        isRenamingAuthorCommand.body.authorIds.indexOf(author.id) > -1
        );

        const isFetching = isBooksFetching || isSeriesFetching || isBookFilesFetching;
        const isPopulated = isBooksPopulated && isSeriesPopulated && isBookFilesPopulated;

        const alternateTitles = _.reduce(author.alternateTitles, (acc, alternateTitle) => {
          if ((alternateTitle.seasonNumber === -1 || alternateTitle.seasonNumber === undefined) &&
            (alternateTitle.sceneSeasonNumber === -1 || alternateTitle.sceneSeasonNumber === undefined)) {
            acc.push(alternateTitle.title);
          }

          return acc;
        }, []);

        // Check root folder availability at two levels:
        const rootFolders = rootFoldersState?.items || [];
        const rootFoldersPopulated = rootFoldersState?.isPopulated || false;
        
        // Global availability: Do compatible root folders exist in the system?
        const hasAudiobookRootFoldersAvailable = rootFolders.some((f) => {
          const folderType = coerceFolderType(f.folderType);
          return folderType === FolderType.Audiobook || folderType === FolderType.Mixed;
        });

        const hasEbookRootFoldersAvailable = rootFolders.some((f) => {
          const folderType = coerceFolderType(f.folderType);
          return folderType === FolderType.Ebook || folderType === FolderType.Mixed;
        });
        
        // Author-level: Has this author configured a root folder for each media type?
        // Consider both per-type *RootFolderPath and per-type *Folder fields.
        // For mixed-content roots, the API may populate *Folder even when *RootFolderPath is null.
        const audiobookRootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, 'audiobook');
        const ebookRootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, 'ebook');

        const authorHasAudiobookConfig = audiobookRootFolderStatus.isConfigured;
        const authorHasEbookConfig = ebookRootFolderStatus.isConfigured;
        
        // Combined availability: Author can use this media type if global folders exist
        // (regardless of whether author has selected one yet)
        const hasAudiobookRootFolder = hasAudiobookRootFoldersAvailable;
        const hasEbookRootFolder = hasEbookRootFoldersAvailable;

        // Use global selected media type first, then prefer the author's last selected media type
        // only when that media type is configured for this author.
        let selectedMediaType = globalSelectedMediaType;

        if (!selectedMediaType) {
          const lastSelected = (author.lastSelectedMediaType || '').toLowerCase();
          if (lastSelected === 'ebook' && authorHasEbookConfig) {
            selectedMediaType = 'ebook';
          } else if (lastSelected === 'audiobook' && authorHasAudiobookConfig) {
            selectedMediaType = 'audiobook';
          }
        }

        // If only one media type is configured for this author, default to it.
        if (!selectedMediaType) {
          if (authorHasEbookConfig && !authorHasAudiobookConfig) {
            selectedMediaType = 'ebook';
          } else if (authorHasAudiobookConfig && !authorHasEbookConfig) {
            selectedMediaType = 'audiobook';
          }
        }
        
        // If no selected media type yet and root folders are populated, 
        // default based on what root folders are available
        if (!selectedMediaType && rootFoldersPopulated) {
          if (hasEbookRootFolder && !hasAudiobookRootFolder) {
            selectedMediaType = 'ebook';
          } else {
            selectedMediaType = 'audiobook';
          }
        } else if (!selectedMediaType) {
          // Root folders not populated yet, use audiobook as default
          selectedMediaType = 'audiobook';
        }

        // Determine the appropriate quality profile based on selected media type
        const qualityProfileId = selectedMediaType === 'audiobook' 
          ? author.audiobookQualityProfileId 
          : author.ebookQualityProfileId;

        const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, selectedMediaType);
        const effectiveMediaType = rootFolderStatus.mediaType;
        const hasRootFolder = rootFolderStatus.hasRootFolder;

        // Counts should match what's visible on this author details page and must not drift as
        // other authors are imported/refreshed (SignalR updates global stores).
        const authorBooks = Array.isArray(allBookItems)
          ? allBookItems.filter((book) => book && book.authorId === author.id)
          : [];

        const authorBooksForMediaType = effectiveMediaType
          ? authorBooks.filter((book) => (book.mediaType || '').toString().toLowerCase() === effectiveMediaType)
          : authorBooks;

        const visibleBooksBase = hasRootFolder ? authorBooksForMediaType : [];

        const filterText = (filterValue || '').toString().trim();
        const isFilterActive = Boolean(filterText);

        let visibleBooks = visibleBooksBase;

        if (isFilterActive) {
          const searchTerm = filterText.toLowerCase();
          const includes = (value) => typeof value === 'string' && value.toLowerCase().includes(searchTerm);
          const listIncludes = (list) => Array.isArray(list) && list.some((value) => includes(value));

          const editionMatches = (edition) => {
            if (!edition) {
              return false;
            }

            // Narrator search: use ALL editions (even when the book-level narrator is hidden).
            // Also include edition title/subtitle so users can search alternate-language variants.
            return includes(edition.narrator) || includes(edition.title) || includes(edition.subtitle);
          };

          visibleBooks = visibleBooks.filter((book) => {
            if (!book) {
              return false;
            }

            // Book-level fields
            if (includes(book.title) || includes(book.authorTitle) || includes(book.seriesTitle) || includes(book.narrator)) {
              return true;
            }

            // Aggregated edition narrators (server-provided) so search works even when narrator column is hidden.
            if (listIncludes(book.availableNarrators)) {
              return true;
            }

            // Series (legacy shape)
            if (Array.isArray(book.seriesLinks)) {
              const matchesSeries = book.seriesLinks.some((link) => includes(link?.series?.title));
              if (matchesSeries) {
                return true;
              }
            }

            // Editions (robust narrator search across all editions)
            if (Array.isArray(book.editions) && book.editions.some(editionMatches)) {
              return true;
            }

            return false;
          });
        } else if (hideUnmonitoredMissing && visibleBooks.length > 0) {
          // When showing "Monitored", hide only if unmonitored AND missing.
          visibleBooks = visibleBooks.filter((book) => {
            const isMonitored = effectiveMediaType === 'audiobook'
              ? book.audiobookMonitored
              : book.ebookMonitored;
            const hasFiles = book.statistics && book.statistics.bookFileCount > 0;
            return isMonitored || hasFiles;
          });
        }

        const authorBookFiles = Array.isArray(allBookFileItems)
          ? allBookFileItems.filter((file) => {
            if (!file) {
              return false;
            }

            if (file.authorId === author.id) {
              return true;
            }

            if (file.book && file.book.authorId === author.id) {
              return true;
            }

            if (file.author && file.author.id === author.id) {
              return true;
            }

            return false;
          })
          : [];

        const authorBookFilesForMediaType = effectiveMediaType
          ? authorBookFiles.filter((file) => file.mediaType === effectiveMediaType)
          : authorBookFiles;

        const visibleBookFiles = isFilterActive
          ? authorBookFilesForMediaType.filter((file) => {
            const searchTerm = filterText.toLowerCase();
            const pathMatch = file.path && file.path.toLowerCase().includes(searchTerm);
            const titleMatch = file.book && file.book.title && file.book.title.toLowerCase().includes(searchTerm);
            const authorMatch = file.book && file.book.authorTitle && file.book.authorTitle.toLowerCase().includes(searchTerm);

            return pathMatch || titleMatch || authorMatch;
          })
          : authorBookFilesForMediaType;

        const hasBooksForAuthor = visibleBooksBase.length > 0;
        const hasMonitoredBooksForAuthor = visibleBooksBase.some((book) => {
          return effectiveMediaType === 'audiobook' ? book.audiobookMonitored : book.ebookMonitored;
        });

        const hasBookFilesForAuthor = authorBookFilesForMediaType.length > 0;

        const visibleBookIds = new Set(visibleBooks.map((b) => b.id));
        const visibleSeriesCount = (seriesItems || []).filter((s) =>
          (s.links || []).some((link) => visibleBookIds.has(link.bookId))
        ).length;

        return {
          ...author,
          authorLoaded: true,
          alternateTitles,
          isAuthorRefreshing,
          allAuthorRefreshing,
          isRefreshing,
          isSearching,
          isRenamingFiles,
          isRenamingAuthor,
          isFetching,
          isPopulated,
          booksError,
          isSaving,
          saveError,
          isDeleting,
          deleteError,
          seriesError,
          bookFilesError,
          hasBooks: hasBooksForAuthor,
          hasMonitoredBooks: hasMonitoredBooksForAuthor,
          hasSeries,
          series: seriesItems,
          visibleSeriesCount,
          hasBookFiles: hasBookFilesForAuthor,
          previousAuthor,
          nextAuthor,
          isSmallScreen: dimensions.isSmallScreen,
          selectedMediaType,
          hasAudiobookRootFolder,
          hasEbookRootFolder,
          hasAudiobookRootFoldersAvailable,
          hasEbookRootFoldersAvailable,
          authorHasAudiobookConfig,
          authorHasEbookConfig,
          rootFoldersPopulated,
          qualityProfileId,
          hideUnmonitoredMissing: hideUnmonitoredMissing ?? false,
          filterValue: filterValue || '',
          statistics: {
            ...(author.statistics || {}),
            totalBookCount: visibleBooks.length,
            bookFileCount: visibleBookFiles.length
          }
        };
      } catch (error) {
        console.error('Error in AuthorDetailsConnector mapStateToProps:', error);
        return {};
      }
    }
  );
}

const mapDispatchToProps = {
  clearBooks,
  fetchBooks,
  fetchSeries,
  clearSeries,
  saveBookEditor,
  fetchBookFiles,
  clearBookFiles,
  toggleAuthorMonitored,
  updateAuthorMediaType,
  fetchAuthorMediaTypeSize,
  fetchQueueDetails,
  clearQueueDetails,
  clearReleases,
  cancelFetchReleases,
  executeCommand,
  fetchRootFolders,
  setSelectedMediaType
};

class AuthorDetailsConnector extends Component {
  constructor(props) {
    super(props);

    this._hasPopulated = false;
    this._userSelectedMediaType = false;
  }

  ensureConfiguredMediaTypeSelection = () => {
    const {
      authorLoaded,
      selectedMediaType,
      authorHasAudiobookConfig,
      authorHasEbookConfig,
      setSelectedMediaType
    } = this.props;

    if (!authorLoaded || this._userSelectedMediaType) {
      return false;
    }

    const normalizedSelected = (selectedMediaType || '').toString().toLowerCase() === 'ebook' ? 'ebook' : 'audiobook';

    if (normalizedSelected === 'audiobook' && !authorHasAudiobookConfig && authorHasEbookConfig) {
      setSelectedMediaType({ mediaType: 'ebook' });
      return true;
    }

    if (normalizedSelected === 'ebook' && !authorHasEbookConfig && authorHasAudiobookConfig) {
      setSelectedMediaType({ mediaType: 'audiobook' });
      return true;
    }

    return false;
  };

  //
  // Lifecycle

  componentDidMount() {
    registerPagePopulator(this.populate);
    
    // Fetch root folders first, let componentDidUpdate handle populate
    this.props.fetchRootFolders();

    this.ensureConfiguredMediaTypeSelection();
  }

  componentDidUpdate(prevProps) {
    const {
      id,
      isAuthorRefreshing,
      allAuthorRefreshing,
      isRenamingFiles,
      isRenamingAuthor,
      hideUnmonitoredMissing,
      selectedMediaType,
      rootFoldersPopulated
    } = this.props;

    // If the id has changed we need to clear the books
    // files and fetch from the server.
    if (prevProps.id !== id) {
      this._hasPopulated = false;
      this._userSelectedMediaType = false;
      this.unpopulate();
      // Let the selectedMediaType check handle populate
    }

    if (this.ensureConfiguredMediaTypeSelection()) {
      return;
    }

    // Populate when BOTH are ready and we haven't populated yet
    // The _hasPopulated flag prevents duplicate calls
    if (!this._hasPopulated && rootFoldersPopulated && selectedMediaType) {
      this.populate();
    }

    if (
      (prevProps.isAuthorRefreshing && !isAuthorRefreshing) ||
      (prevProps.allAuthorRefreshing && !allAuthorRefreshing) ||
      (prevProps.isRenamingFiles && !isRenamingFiles) ||
      (prevProps.isRenamingAuthor && !isRenamingAuthor)
    ) {
      this.populate();
    }

    // If the hideUnmonitoredMissing toggle has changed, refetch books with appropriate filter
    if (prevProps.hideUnmonitoredMissing !== hideUnmonitoredMissing) {
      this.populate();
    }

	    // If selectedMediaType changed after initial load, refetch with correct media type
	    // (The initial population is handled by the logic above)
	    if (prevProps.selectedMediaType && prevProps.selectedMediaType !== selectedMediaType && selectedMediaType) {
	      this.populate();
	    }
	  }

  componentWillUnmount() {
    unregisterPagePopulator(this.populate);
    // Preserve the author's books when navigating to the book details page so
    // previous/next book navigation can work immediately (Readarr-style).
    // Book details will still self-heal via an author-scoped fetch on deep links.
    this.unpopulate({ clearBooks: false });
  }

  //
  // Control

  populate = () => {
    const { id: authorId, selectedMediaType, hideUnmonitoredMissing } = this.props;

    console.log('[AuthorDetails] populate() called with:', { 
      authorId, 
      selectedMediaType, 
      hideUnmonitoredMissing,
      hasPopulated: this._hasPopulated 
    });

    // Don't populate if we don't have a valid author ID
    if (!authorId || authorId === 0) {
      console.log('[AuthorDetails] Skipping populate - no valid authorId');
      return;
    }

    // Don't populate until we have a valid selectedMediaType
    // This prevents fetching with the wrong media type on initial load
    if (!selectedMediaType) {
      console.log('[AuthorDetails] Skipping populate - no selectedMediaType yet');
      return;
    }

    // Prevent duplicate calls - check if we're already fetching
    if (this._isPopulating) {
      console.log('[AuthorDetails] Skipping populate - already in progress');
      return;
    }

    // Mark as populated only when we actually fetch with valid data
    this._hasPopulated = true;
    this._isPopulating = true;

    // Fetch books specifically for this author with the selected media type
    const booksFetchParams = { 
      authorId, 
      mediaType: selectedMediaType
    };
    
    // Only add monitored parameter when hideUnmonitoredMissing is true
    // When false (All toggle), don't send monitored parameter to get all books
    if (hideUnmonitoredMissing) {
      booksFetchParams.monitored = true;
    }
    
    this.props.fetchBooks(booksFetchParams);
    
    this.props.fetchSeries({ authorId, mediaType: selectedMediaType });
    this.props.fetchBookFiles({ authorId, mediaType: selectedMediaType });
    this.props.fetchQueueDetails({ authorId });
    
    // Fetch size for both media types
    this.props.fetchAuthorMediaTypeSize({ authorId, mediaType: 'audiobook' });
    this.props.fetchAuthorMediaTypeSize({ authorId, mediaType: 'ebook' });
    
    // Reset populating flag after a short delay to allow for subsequent calls if needed
    setTimeout(() => {
      this._isPopulating = false;
    }, 100);
  };

  unpopulate = ({ clearBooks: shouldClearBooks = true } = {}) => {
    this.props.cancelFetchReleases();
    if (shouldClearBooks) {
      this.props.clearBooks();
    }
    this.props.clearSeries();
    this.props.clearBookFiles();
    this.props.clearQueueDetails();
    this.props.clearReleases();
  };

  //
  // Listeners

  onMonitorTogglePress = (monitored) => {
    this.props.toggleAuthorMonitored({
      authorId: this.props.id,
      monitored
    });
  };

  onRefreshPress = () => {
    this.props.executeCommand({
      name: commandNames.REFRESH_AUTHOR,
      authorId: this.props.id
    });
  };

  onSearchPress = () => {
    this.props.executeCommand({
      name: commandNames.AUTHOR_SEARCH,
      authorId: this.props.id
    });
  };

  onSaveSelected = (payload) => {
    this.props.saveBookEditor(payload);
  };

  onMediaTypeChange = (mediaType) => {
    const { id } = this.props;

    this._userSelectedMediaType = true;
    
    // Update global selected media type
    this.props.setSelectedMediaType({ mediaType });
    
    // Update author's last selected media type
    this.props.updateAuthorMediaType({ authorId: id, mediaType });
    
    // REMOVED duplicate API calls - componentDidUpdate will detect the mediaType change
    // and call populate() which handles all the fetching with proper parameters
  };

  //
  // Render

  render() {
    return (
      <AuthorDetails
        {...this.props}
        onMonitorTogglePress={this.onMonitorTogglePress}
        onRefreshPress={this.onRefreshPress}
        onSearchPress={this.onSearchPress}
        onSaveSelected={this.onSaveSelected}
        onMediaTypeChange={this.onMediaTypeChange}
      />
    );
  }
}

AuthorDetailsConnector.propTypes = {
  id: PropTypes.number,
  authorLoaded: PropTypes.bool,
  isAuthorRefreshing: PropTypes.bool,
  allAuthorRefreshing: PropTypes.bool,
  isRefreshing: PropTypes.bool,
  isRenamingFiles: PropTypes.bool,
  isRenamingAuthor: PropTypes.bool,
  selectedMediaType: PropTypes.string,
  authorHasAudiobookConfig: PropTypes.bool,
  authorHasEbookConfig: PropTypes.bool,
  rootFoldersPopulated: PropTypes.bool,
  hideUnmonitoredMissing: PropTypes.bool.isRequired,
  clearBooks: PropTypes.func.isRequired,
  fetchBooks: PropTypes.func.isRequired,
  fetchSeries: PropTypes.func.isRequired,
  clearSeries: PropTypes.func.isRequired,
  saveBookEditor: PropTypes.func.isRequired,
  fetchBookFiles: PropTypes.func.isRequired,
  clearBookFiles: PropTypes.func.isRequired,
  toggleAuthorMonitored: PropTypes.func.isRequired,
  updateAuthorMediaType: PropTypes.func.isRequired,
  fetchAuthorMediaTypeSize: PropTypes.func.isRequired,
  fetchQueueDetails: PropTypes.func.isRequired,
  clearQueueDetails: PropTypes.func.isRequired,
  clearReleases: PropTypes.func.isRequired,
  cancelFetchReleases: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  setSelectedMediaType: PropTypes.func.isRequired
};

AuthorDetailsConnector.defaultProps = {
  id: 0,
  isAuthorRefreshing: false,
  allAuthorRefreshing: false,
  isRefreshing: false,
  isRenamingFiles: false,
  isRenamingAuthor: false,
  selectedMediaType: 'audiobook',
  hideUnmonitoredMissing: false
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorDetailsConnector);
