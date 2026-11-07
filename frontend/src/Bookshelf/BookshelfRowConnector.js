import orderBy from 'lodash/orderBy';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { toggleBooksMonitored } from 'Store/Actions/bookActions';
import { fetchBookshelfBooks, getBookshelfBookKey } from 'Store/Actions/bookshelfActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import BookshelfRow from './BookshelfRow';

// Use a const to share the reselect cache between instances
const getBookMap = createSelector(
  (state) => state.books.items,
  (books) => {
    return books.reduce((acc, curr) => {
      (acc[curr.authorId] = acc[curr.authorId] || []).push(curr);
      return acc;
    }, {});
  }
);

function createMapStateToProps() {
  return createSelector(
    createAuthorSelector(),
    getBookMap,
    (state, props) => props.selectedMediaType,
    (state, props) => state.bookshelf.loadedBookKeys[getBookshelfBookKey(props.selectedMediaType, props.authorId)] === true,
    (state, props) => state.bookshelf.failedBookKeys[getBookshelfBookKey(props.selectedMediaType, props.authorId)] === true,
    (author, bookMap, selectedMediaType, isBooksLoaded, hasBooksError) => {
      const booksInAuthor = bookMap.hasOwnProperty(author.id) ? bookMap[author.id] : [];
      const sortedBooks = orderBy(
        booksInAuthor.filter((book) => selectedMediaType === 'both' || book.mediaType === selectedMediaType),
        'releaseDate',
        'desc'
      );

      return {
        ...author,
        authorId: author.id,
        authorName: author.authorName,
        status: author.status,
        books: sortedBooks,
        isBooksLoaded,
        hasBooksError
      };
    }
  );
}

const mapDispatchToProps = {
  toggleBooksMonitored,
  fetchBookshelfBooks
};

class BookshelfRowConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.fetchBooksIfNeeded();

    // CellMeasurer may have measured the loading state before this row mounted.
    if (this.props.isBooksLoaded) {
      this.props.onBooksLoaded?.();
    }
  }

  componentDidUpdate(prevProps) {
    this.fetchBooksIfNeeded();

    // Re-measure after the spinner is replaced with the loaded book tiles.
    if (!prevProps.isBooksLoaded && this.props.isBooksLoaded) {
      this.props.onBooksLoaded?.();
    }
  }

  fetchBooksIfNeeded() {
    const {
      authorId,
      selectedMediaType,
      isBooksLoaded,
      hasBooksError
    } = this.props;

    // Repeat calls are ignored until the author's books arrive.
    if (!isBooksLoaded && !hasBooksError) {
      this.props.fetchBookshelfBooks({
        authorId,
        mediaType: selectedMediaType
      });
    }
  }

  onRetryBooksPress = () => {
    this.props.fetchBookshelfBooks({
      authorId: this.props.authorId,
      mediaType: this.props.selectedMediaType
    });
  };

  //
  // Listeners

  onBookMonitoredPress = (bookId, monitored) => {
    const bookIds = [bookId];
    this.props.toggleBooksMonitored({
      bookIds,
      monitored
    });
  };

  //
  // Render

  render() {
    return (
      <BookshelfRow
        onRetryBooksPress={this.onRetryBooksPress}
        {...this.props}
        onBookMonitoredPress={this.onBookMonitoredPress}
      />
    );
  }
}

BookshelfRowConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  isBooksLoaded: PropTypes.bool.isRequired,
  hasBooksError: PropTypes.bool.isRequired,
  onBooksLoaded: PropTypes.func,
  toggleBooksMonitored: PropTypes.func.isRequired,
  fetchBookshelfBooks: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfRowConnector);
