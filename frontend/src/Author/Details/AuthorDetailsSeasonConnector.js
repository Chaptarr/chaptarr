/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { setAuthorDetailsId, setAuthorDetailsSort } from 'Store/Actions/authorDetailsActions';
import { setBooksTableOption, sortPredicates as globalBookSortPredicates, toggleBooksMonitored } from 'Store/Actions/bookActions';
import { executeCommand } from 'Store/Actions/commandActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import createBookSearchFilter from 'Utilities/Book/createBookSearchFilter';
import isLibraryBook from 'Utilities/Book/isLibraryBook';
import AuthorDetailsSeason from './AuthorDetailsSeason';

function createMapStateToProps() {
  return createSelector(
    createClientSideCollectionSelector('books', 'authorDetails'),
    createAuthorSelector(),
    createDimensionsSelector(),
    createUISettingsSelector(),
    (state) => state.app.hideUnmonitoredMissing,
    (state) => state.authorDetails.filterValue,
    (state, props) => props.selectedMediaType, // Get selectedMediaType from props
    (books, author, dimensions, uiSettings, hideUnmonitoredMissing, filterValue, selectedMediaType) => {

      // Use the selectedMediaType passed from parent, fallback to author's preference or audiobook
      const requestedMediaType = selectedMediaType || author.lastSelectedMediaType || 'audiobook';
      const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, requestedMediaType);
      const effectiveMediaType = rootFolderStatus.mediaType;
      const hasRootFolder = rootFolderStatus.hasRootFolder;
      const isAuthorDataLoading = rootFolderStatus.isDataLoading;
      
      // Books are now filtered by media type from the backend
      // Show only books that match the selected media type
      let booksInGroup = hasRootFolder ? books.items : [];

      // Defensive: SignalR and media-type switches can temporarily leave the global books store
      // containing items for other media types. Ensure the visible list matches the selected type.
      if (booksInGroup.length > 0 && effectiveMediaType) {
        const mediaType = effectiveMediaType.toLowerCase();
        booksInGroup = booksInGroup.filter((book) => (book?.mediaType || '').toString().toLowerCase() === mediaType);
      }
      
      // Apply search filter first if there's a value
      if (filterValue && typeof filterValue === 'string' && filterValue.trim()) {
        booksInGroup = booksInGroup.filter(createBookSearchFilter(filterValue));
      }

      // Apply hide unmonitored/missing filter ONLY if not searching
      // When searching, show all results regardless of monitored/missing status
      if (hideUnmonitoredMissing && (!filterValue || typeof filterValue !== 'string' || !filterValue.trim()) && booksInGroup.length > 0) {
        booksInGroup = booksInGroup.filter((book) => isLibraryBook(book));
      }

      // Apply sorting to the filtered books using the sort predicates
      const predicateFn = (books.sortPredicates && books.sortPredicates[books.sortKey])
        || (globalBookSortPredicates && globalBookSortPredicates[books.sortKey]);

      // Improve stability for Status sorting: use a deterministic tie-breaker (title)
      let sortedBooks;
      if (books.sortKey === 'status') {
        const statusClause = predicateFn ?
          (item) => predicateFn(item, books.sortDirection, effectiveMediaType) :
          (item) => '';
        const titleClause = (item) => ((globalBookSortPredicates && globalBookSortPredicates.title) ?
          globalBookSortPredicates.title(item) :
          (item.title || '').toLowerCase());

        const primaryOrder = books.sortDirection === 'ascending' ? 'asc' : 'desc';
        // Always sort title ascending for readability as a tie-breaker
        sortedBooks = _.orderBy(booksInGroup, [statusClause, titleClause], [primaryOrder, 'asc']);
      } else {
        const sortClause = predicateFn
          ? (item) => predicateFn(item, books.sortDirection, effectiveMediaType)
          : (item) => (item && item[books.sortKey] !== undefined ? item[books.sortKey] : '');
        const primaryOrder = books.sortDirection === 'ascending' ? 'asc' : 'desc';
        sortedBooks = _.orderBy(booksInGroup, [sortClause], [primaryOrder]);
      }

      // Select appropriate columns based on media type
      let columns;
      if (effectiveMediaType === 'audiobook' && books.audiobookColumns) {
        columns = books.audiobookColumns;
      } else if (effectiveMediaType === 'ebook' && books.ebookColumns) {
        columns = books.ebookColumns;
      } else {
        // Fallback to legacy columns
        columns = books.columns;
      }

      return {
        items: sortedBooks,
        columns,
        sortKey: books.sortKey,
        sortDirection: books.sortDirection,
        isSmallScreen: dimensions.isSmallScreen,
        uiSettings,
        selectedMediaType: effectiveMediaType, // Pass the effective media type to child
        hasRootFolder,
        isAuthorDataLoading
      };
    }
  );
}

const mapDispatchToProps = {
  setAuthorDetailsId,
  setAuthorDetailsSort,
  toggleBooksMonitored,
  setBooksTableOption,
  executeCommand
};

class AuthorDetailsSeasonConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.setAuthorDetailsId({ authorId: this.props.authorId });
  }

  componentDidUpdate(prevProps) {
    // Ensure the authorId-scoped filter is updated when navigating between authors
    if (prevProps.authorId !== this.props.authorId && this.props.authorId) {
      this.props.setAuthorDetailsId({ authorId: this.props.authorId });
    }
  }

  //
  // Listeners

  onTableOptionChange = (payload) => {
    const { selectedMediaType } = this.props;
    this.props.setBooksTableOption({
      ...payload,
      mediaType: selectedMediaType
    });
  };

  onSortPress = (sortKey) => {
    this.props.setAuthorDetailsSort({ sortKey });
  };

  onMonitorBookPress = (bookIds, monitored) => {
    const { selectedMediaType } = this.props;
    this.props.toggleBooksMonitored({
      bookIds,
      monitored,
      mediaType: selectedMediaType
    });
  };

  //
  // Render

  render() {
    return (
      <AuthorDetailsSeason
        {...this.props}
        onSortPress={this.onSortPress}
        onTableOptionChange={this.onTableOptionChange}
        onMonitorBookPress={this.onMonitorBookPress}
      />
    );
  }
}

AuthorDetailsSeasonConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  selectedMediaType: PropTypes.string.isRequired,
  toggleBooksMonitored: PropTypes.func.isRequired,
  setBooksTableOption: PropTypes.func.isRequired,
  setAuthorDetailsId: PropTypes.func.isRequired,
  setAuthorDetailsSort: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorDetailsSeasonConnector);
