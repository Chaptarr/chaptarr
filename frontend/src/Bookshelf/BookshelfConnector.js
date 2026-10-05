import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { setSelectedMediaType } from 'Store/Actions/appActions';
import { clearBookshelf, fetchBookshelfAuthors, saveBookshelf, setBookshelfFilter, setBookshelfSort } from 'Store/Actions/bookshelfActions';
import createAuthorClientSideCollectionItemsSelector from 'Store/Selectors/createAuthorClientSideCollectionItemsSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import Bookshelf from './Bookshelf';

function createMapStateToProps() {
  return createSelector(
    (state) => state.bookshelf,
    (state) => state.app.selectedMediaType || 'audiobook',
    createAuthorClientSideCollectionItemsSelector('bookshelf'),
    createDimensionsSelector(),
    (bookshelf, selectedMediaType, author, dimensionsState) => {
      const { authorBookCounts } = bookshelf;
      const isPopulated = bookshelf.isPopulated && author.isPopulated;
      const isFetching = author.isFetching || bookshelf.isFetching;
      const items = author.items.filter((item) => authorBookCounts.hasOwnProperty(item.id));

      let bookCount = 0;
      items.forEach((item) => {
        bookCount += authorBookCounts[item.id];
      });

      return {
        ...author,
        items,
        totalItems: Object.keys(authorBookCounts).length,
        isPopulated,
        isFetching,
        error: author.error || bookshelf.error,
        bookCount,
        selectedMediaType,
        isSmallScreen: dimensionsState.isSmallScreen
      };
    }
  );
}

const mapDispatchToProps = {
  setBookshelfSort,
  setBookshelfFilter,
  setSelectedMediaType,
  clearBookshelf,
  fetchBookshelfAuthors,
  saveBookshelf
};

class BookshelfConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    // Other pages can replace the books store, so rows reload their books on every visit.
    this.props.clearBookshelf();
    this.fetchAuthorBookCounts();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.selectedMediaType !== this.props.selectedMediaType) {
      this.fetchAuthorBookCounts();
    }
  }

  componentWillUnmount() {
    this.props.clearBookshelf();
  }

  fetchAuthorBookCounts() {
    this.props.fetchBookshelfAuthors({
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
  clearBookshelf: PropTypes.func.isRequired,
  fetchBookshelfAuthors: PropTypes.func.isRequired,
  saveBookshelf: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookshelfConnector);
