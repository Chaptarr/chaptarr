import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { toggleBooksMonitored } from 'Store/Actions/bookActions';
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
    (author, bookMap, selectedMediaType) => {
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
        books: sortedBooks
      };
    }
  );
}

const mapDispatchToProps = {
  toggleBooksMonitored
};

class BookshelfRowConnector extends Component {

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
  toggleBooksMonitored: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfRowConnector);
