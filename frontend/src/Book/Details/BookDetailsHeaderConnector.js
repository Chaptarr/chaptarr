import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { toggleBooksMonitored } from 'Store/Actions/bookActions';
import createBookSelector from 'Store/Selectors/createBookSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createQueueItemSelector from 'Store/Selectors/createQueueItemSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import BookDetailsHeader from './BookDetailsHeader';

const selectBookDetails = createSelector(
  (state) => state.editions,
  (editions) => {
    const monitored = editions.items.find((e) => e.monitored === true);
    return {
      overview: monitored?.overview,
      images: monitored?.images,
      ratings: monitored?.ratings
    };
  }
);

function createMapStateToProps() {
  const selectQueueItem = createQueueItemSelector();

  return createSelector(
    createBookSelector(),
    selectBookDetails,
    selectQueueItem,
    createUISettingsSelector(),
    createDimensionsSelector(),
    (book, bookDetails, queueItem, uiSettings, dimensions) => {
      const images = (bookDetails.images && bookDetails.images.length) ? bookDetails.images : (book.images || []);

      // Use book's own properties if available, fallback to edition details
      return {
        ...book,
        overview: bookDetails.overview || book.overview || '', // Prioritize edition's overview over book's overview
        images, // Prioritize edition's images over book's images (but don't let empty arrays mask book images)
        queueItem,
        ratings: bookDetails.ratings || book.ratings || {}, // Prioritize edition's ratings over book's ratings
        shortDateFormat: uiSettings.shortDateFormat,
        isSmallScreen: dimensions.isSmallScreen
      };
    }
  );
}

const mapDispatchToProps = {
  toggleBooksMonitored
};

class BookDetailsHeaderConnector extends Component {

  //
  // Listeners

  onMonitorTogglePress = (monitored) => {
    this.props.toggleBooksMonitored({
      bookIds: [this.props.bookId],
      monitored
    });
  };

  //
  // Render

  render() {
    return (
      <BookDetailsHeader
        {...this.props}
        onMonitorTogglePress={this.onMonitorTogglePress}
      />
    );
  }
}

BookDetailsHeaderConnector.propTypes = {
  bookId: PropTypes.number,
  toggleBooksMonitored: PropTypes.func.isRequired,
  author: PropTypes.object
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookDetailsHeaderConnector);
