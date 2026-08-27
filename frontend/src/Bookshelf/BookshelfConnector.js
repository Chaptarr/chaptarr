import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { setSelectedMediaType } from 'Store/Actions/appActions';
import { clearBooks, fetchBooks } from 'Store/Actions/bookActions';
import { saveBookshelf, setBookshelfFilter, setBookshelfSort } from 'Store/Actions/bookshelfActions';
import createAuthorClientSideCollectionItemsSelector from 'Store/Selectors/createAuthorClientSideCollectionItemsSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import Bookshelf from './Bookshelf';

function createBookFetchStateSelector() {
  return createSelector(
    (state) => state.books.items,
    (state) => state.books.isFetching,
    (state) => state.books.isPopulated,
    (state) => state.app.selectedMediaType || 'audiobook',
    (items, isFetching, isPopulated, selectedMediaType) => {
      const scopedItems = items.filter((book) => book.mediaType === selectedMediaType);
      return {
        isFetching,
        isPopulated,
        items: scopedItems,
        selectedMediaType
      };
    }
  );
}

function createMapStateToProps() {
  return createSelector(
    createBookFetchStateSelector(),
    createAuthorClientSideCollectionItemsSelector('bookshelf'),
    createDimensionsSelector(),
    (books, author, dimensionsState) => {
      const isPopulated = books.isPopulated && author.isPopulated;
      const isFetching = author.isFetching || books.isFetching;
      const authorIds = new Set(books.items.map((book) => book.authorId));
      const items = author.items.filter((item) => authorIds.has(item.id));
      const visibleAuthorIds = new Set(items.map((item) => item.id));
      const bookCount = books.items.filter((book) => visibleAuthorIds.has(book.authorId)).length;

      return {
        ...author,
        items,
        totalItems: authorIds.size,
        isPopulated,
        isFetching,
        bookCount,
        selectedMediaType: books.selectedMediaType,
        isSmallScreen: dimensionsState.isSmallScreen
      };
    }
  );
}

const mapDispatchToProps = {
  setBookshelfSort,
  setBookshelfFilter,
  setSelectedMediaType,
  clearBooks,
  fetchBooks,
  saveBookshelf
};

class BookshelfConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.fetchBooksForSelectedMediaType();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.selectedMediaType !== this.props.selectedMediaType) {
      this.fetchBooksForSelectedMediaType();
    }
  }

  componentWillUnmount() {
    if (this.abortBooksFetch) {
      this.abortBooksFetch();
      this.abortBooksFetch = null;
    }
  }

  fetchBooksForSelectedMediaType() {
    this.abortBooksFetch = this.props.fetchBooks({
      mediaType: this.props.selectedMediaType
    });
  }

  //
  // Listeners

  onSortPress = (sortKey) => {
    this.props.setBookshelfSort({ sortKey });
  };

  onFilterSelect = (selectedFilterKey) => {
    this.props.setBookshelfFilter({ selectedFilterKey });
  };

  onMediaTypeChange = (mediaType) => {
    this.props.setSelectedMediaType({ mediaType });
    this.props.clearBooks();
  };

  onUpdateSelectedPress = (payload) => {
    this.props.saveBookshelf(payload);
  };

  //
  // Render

  render() {
    return (
      <Bookshelf
        {...this.props}
        onSortPress={this.onSortPress}
        onFilterSelect={this.onFilterSelect}
        onMediaTypeChange={this.onMediaTypeChange}
        onUpdateSelectedPress={this.onUpdateSelectedPress}
      />
    );
  }
}

BookshelfConnector.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  setBookshelfSort: PropTypes.func.isRequired,
  setBookshelfFilter: PropTypes.func.isRequired,
  setSelectedMediaType: PropTypes.func.isRequired,
  clearBooks: PropTypes.func.isRequired,
  fetchBooks: PropTypes.func.isRequired,
  saveBookshelf: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfConnector);
