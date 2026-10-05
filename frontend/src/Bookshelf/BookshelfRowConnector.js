import _ from 'lodash';
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
    (author, bookMap, selectedMediaType, isBooksLoaded) => {
      const booksInAuthor = bookMap.hasOwnProperty(author.id) ? bookMap[author.id] : [];
      const sortedBooks = _.orderBy(
        booksInAuthor.filter((book) => book.mediaType === selectedMediaType),
        'releaseDate',
        'desc'
      );

      return {
        ...author,
        authorId: author.id,
        authorName: author.authorName,
        status: author.status,
        books: sortedBooks,
        isBooksLoaded
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
  }

  componentDidUpdate() {
    this.fetchBooksIfNeeded();
  }

  fetchBooksIfNeeded() {
    const {
      authorId,
      selectedMediaType,
      isBooksLoaded
    } = this.props;

    // Repeat calls are ignored until the author's books arrive.
    if (!isBooksLoaded) {
      this.props.fetchBookshelfBooks({
        authorId,
        mediaType: selectedMediaType
      });
    }
  }

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
        {...this.props}
        onBookMonitoredPress={this.onBookMonitoredPress}
      />
    );
  }
}

BookshelfRowConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  isBooksLoaded: PropTypes.bool.isRequired,
  toggleBooksMonitored: PropTypes.func.isRequired,
  fetchBookshelfBooks: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfRowConnector);
