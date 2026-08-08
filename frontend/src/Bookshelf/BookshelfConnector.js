import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { fetchBooks } from 'Store/Actions/bookActions';
import { saveBookshelf, setBookshelfFilter, setBookshelfSort } from 'Store/Actions/bookshelfActions';
import createAuthorClientSideCollectionItemsSelector from 'Store/Selectors/createAuthorClientSideCollectionItemsSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import Bookshelf from './Bookshelf';

function createBookFetchStateSelector() {
  return createSelector(
    (state) => state.books.items.length,
    (state) => state.books.isFetching,
    (state) => state.books.isPopulated,
    (length, isFetching, isPopulated) => {
      const bookCount = (!isFetching && isPopulated) ? length : 0;
      return {
        bookCount,
        isFetching,
        isPopulated
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
      return {
        ...author,
        isPopulated,
        isFetching,
        isBookFetching: books.isFetching,
        isBookPopulated: books.isPopulated,
        bookCount: books.bookCount,
        isSmallScreen: dimensionsState.isSmallScreen
      };
    }
  );
}

const mapDispatchToProps = {
  setBookshelfSort,
  setBookshelfFilter,
  fetchBooks,
  saveBookshelf
};

class BookshelfConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      isBookFetching,
      isBookPopulated
    } = this.props;

    if (!isBookFetching && !isBookPopulated) {
      this.props.fetchBooks({ include: 'author' });
    }
  }

  //
  // Listeners

  onSortPress = (sortKey) => {
    this.props.setBookshelfSort({ sortKey });
  };

  onFilterSelect = (selectedFilterKey) => {
    this.props.setBookshelfFilter({ selectedFilterKey });
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
        onUpdateSelectedPress={this.onUpdateSelectedPress}
      />
    );
  }
}

BookshelfConnector.propTypes = {
  isBookFetching: PropTypes.bool.isRequired,
  isBookPopulated: PropTypes.bool.isRequired,
  setBookshelfSort: PropTypes.func.isRequired,
  setBookshelfFilter: PropTypes.func.isRequired,
  fetchBooks: PropTypes.func.isRequired,
  saveBookshelf: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfConnector);
