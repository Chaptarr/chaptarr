/* eslint max-params: 0 */
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import withScrollPosition from 'Components/withScrollPosition';
import { setSelectedMediaType } from 'Store/Actions/appActions';
import { clearBooks, fetchBooks } from 'Store/Actions/bookActions';
import { saveBookEditor, setBookFilter, setBookSort, setBookTableOption, setBookView } from 'Store/Actions/bookIndexActions';
import * as bookInfiniteScrollActions from 'Store/Actions/bookInfiniteScrollActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { selectQueryBooks } from 'Store/Reducers/bookInfiniteScrollReducer';
import scrollPositions from 'Store/scrollPositions';
import createBookClientSideCollectionItemsSelector from 'Store/Selectors/createBookClientSideCollectionItemsSelector';
import createCommandExecutingSelector from 'Store/Selectors/createCommandExecutingSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import BookIndex from './BookIndex';
import getBookIndexQuery from './getBookIndexQuery';

function createMapStateToProps() {
  return createSelector(
    createBookClientSideCollectionItemsSelector('bookIndex'),
    createCommandExecutingSelector(commandNames.BULK_REFRESH_AUTHOR),
    createCommandExecutingSelector(commandNames.BULK_REFRESH_BOOK),
    createCommandExecutingSelector(commandNames.RSS_SYNC),
    createCommandExecutingSelector(commandNames.CUTOFF_UNMET_BOOK_SEARCH),
    createCommandExecutingSelector(commandNames.MISSING_BOOK_SEARCH),
    createDimensionsSelector(),
    (state) => state.app.selectedMediaType,
    (state) => state.bookInfiniteScroll,
    (
      book,
      isRefreshingAuthorCommand,
      isRefreshingBookCommand,
      isRssSyncExecuting,
      isCutoffBooksSearch,
      isMissingBooksSearch,
      dimensionsState,
      selectedMediaType,
      bookInfiniteScroll
    ) => {
      const isRefreshingBook = isRefreshingBookCommand || isRefreshingAuthorCommand;
      const mediaType = selectedMediaType || 'audiobook';
      const {
        queryKey: bookQueryKey,
        queryParams: bookQueryParams,
        useClientSideBooks
      } = getBookIndexQuery(book, mediaType);
      const bookBuckets = bookInfiniteScroll?.queries?.[bookQueryKey]?.buckets;
      const bookTotalCount = bookInfiniteScroll?.queries?.[bookQueryKey]?.totalCount;
      // Server-paged views don't fetch the main `books` collection. Provide the currently
      // loaded paged items to BookIndex so editor selection (Select All / bulk actions)
      // works consistently across views.
      const selectionItems = useClientSideBooks ?
        book.items :
        selectQueryBooks({ bookInfiniteScroll }, bookQueryKey).map((b) => ({ id: b.id }));

      return {
        ...book,
        items: selectionItems,
        isRefreshingBook,
        isRssSyncExecuting,
        isSearching: isCutoffBooksSearch || isMissingBooksSearch,
        isSmallScreen: dimensionsState.isSmallScreen,
        selectedMediaType: mediaType,
        bookQueryKey,
        bookQueryParams,
        bookBuckets,
        bookTotalCount,
        useClientSideBooks
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onTableOptionChange(payload) {
      dispatch(setBookTableOption(payload));
    },

    onSortSelect(sortKey) {
      dispatch(setBookSort({ sortKey }));
    },

    onFilterSelect(selectedFilterKey) {
      dispatch(setBookFilter({ selectedFilterKey }));
    },

    dispatchSetBookView(view) {
      dispatch(setBookView({ view }));
    },

    dispatchSaveBookEditor(payload) {
      dispatch(saveBookEditor(payload));
    },

    onRefreshBookPress(items) {
      dispatch(executeCommand({
        name: commandNames.BULK_REFRESH_BOOK,
        bookIds: items
      }));
    },

    onRssSyncPress() {
      dispatch(executeCommand({
        name: commandNames.RSS_SYNC
      }));
    },

    onSearchPress(items) {
      dispatch(executeCommand({
        name: commandNames.BOOK_SEARCH,
        bookIds: items
      }));
    },

    dispatchFetchBooks(params = {}) {
      dispatch(fetchBooks(params));
    },

    onFetchBookIds(queryKey, queryParams) {
      return dispatch(bookInfiniteScrollActions.fetchBookIds({ queryKey, queryParams }));
    },

    onMediaTypeChange(mediaType) {
      // Update global selected media type
      dispatch(setSelectedMediaType({ mediaType }));

      // Clear existing books to prevent transient mixing
      dispatch(clearBooks());
    }
  };
}

class BookIndexConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const { selectedMediaType } = this.props;

    if (this.needsClientSideBooks()) {
      this.props.dispatchFetchBooks({
        mediaType: selectedMediaType || 'audiobook',
        include: 'author'
      });
    }
  }

  componentDidUpdate(prevProps) {
    const { selectedMediaType } = this.props;
    const needsClientSideBooks = this.needsClientSideBooks();
    const previouslyNeededClientSideBooks = this.needsClientSideBooks(prevProps);

    if (needsClientSideBooks &&
        (!previouslyNeededClientSideBooks || prevProps.selectedMediaType !== selectedMediaType)) {
      this.props.dispatchFetchBooks({ mediaType: selectedMediaType, include: 'author' });
    }
  }

  needsClientSideBooks(props = this.props) {
    // Only custom filters still need the whole list; built-in filters page on the server.
    return props.useClientSideBooks;
  }

  //
  // Listeners

  onViewSelect = (view) => {
    this.props.dispatchSetBookView(view);
  };

  onSaveSelected = (payload) => {
    this.props.dispatchSaveBookEditor(payload);
  };

  onScroll = ({ scrollTop }) => {
    scrollPositions.bookIndex = scrollTop;
  };

  //
  // Render

  render() {
    return (
      <BookIndex
        {...this.props}
        onViewSelect={this.onViewSelect}
        onScroll={this.onScroll}
        onSaveSelected={this.onSaveSelected}
      />
    );
  }
}

BookIndexConnector.propTypes = {
  isSmallScreen: PropTypes.bool.isRequired,
  view: PropTypes.string.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  useClientSideBooks: PropTypes.bool.isRequired,
  bookTotalCount: PropTypes.number,
  dispatchSetBookView: PropTypes.func.isRequired,
  dispatchSaveBookEditor: PropTypes.func.isRequired,
  dispatchFetchBooks: PropTypes.func.isRequired,
  onFetchBookIds: PropTypes.func.isRequired,
  onMediaTypeChange: PropTypes.func.isRequired
};

export default withScrollPosition(
  connect(createMapStateToProps, createMapDispatchToProps)(BookIndexConnector),
  'bookIndex'
);
