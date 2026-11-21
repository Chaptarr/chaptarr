/* eslint max-params: 0 */
import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { toggleBooksMonitored } from 'Store/Actions/bookActions';
import { executeCommand } from 'Store/Actions/commandActions';
import { setSeriesSort, setSeriesTableOption } from 'Store/Actions/seriesActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createCommandsSelector from 'Store/Selectors/createCommandsSelector';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import getAuthorMediaTypeRootFolderStatus from 'Utilities/Author/getAuthorMediaTypeRootFolderStatus';
import createBookSearchFilter from 'Utilities/Book/createBookSearchFilter';
import isLibraryBook from 'Utilities/Book/isLibraryBook';
import AuthorDetailsSeries from './AuthorDetailsSeries';

function createMapStateToProps() {
  return createSelector(
    (state, { seriesId }) => seriesId,
    (state) => state.books,
    createAuthorSelector(),
    (state) => state.series,
    createCommandsSelector(),
    createDimensionsSelector(),
    createUISettingsSelector(),
    (state) => state.app.hideUnmonitoredMissing,
    (state) => state.authorDetails.filterValue,
    (state, props) => props.selectedMediaType,
    (seriesId, books, author, series, commands, dimensions, uiSettings, hideUnmonitoredMissing, filterValue, selectedMediaType) => {

      const currentSeries = _.find(series.items, { id: seriesId });
      const rootFolderStatus = getAuthorMediaTypeRootFolderStatus(author, selectedMediaType);
      const effectiveMediaType = rootFolderStatus.mediaType;
      const hasRootFolder = rootFolderStatus.hasRootFolder;

      if (!currentSeries) {
        return {
          id: seriesId,
          label: 'Unknown Series',
          items: [],
          positionMap: {},
          columns: series.columns,
          sortKey: series.sortKey,
          sortDirection: series.sortDirection,
          isSmallScreen: dimensions.isSmallScreen,
          uiSettings,
          hideSeries: true,
          hasRootFolder
        };
      }

      const bookIds = (currentSeries.links || []).map((x) => x.bookId);
      const positionMap = (currentSeries.links || []).reduce((acc, curr) => {
        acc[curr.bookId] = curr.position;
        return acc;
      }, {});
      const positionOrderMap = (currentSeries.links || []).reduce((acc, curr) => {
        acc[curr.bookId] = curr.positionOrder;
        return acc;
      }, {});

      let booksInSeries = _.filter(books.items, (book) => bookIds.includes(book.id));

      // If the author has no root folder configured for this media type, hide series content on this tab.
      if (!hasRootFolder) {
        return {
          id: currentSeries.id,
          label: currentSeries.title,
          items: [],
          positionMap,
          columns: series.columns,
          sortKey: series.sortKey,
          sortDirection: series.sortDirection,
          isSmallScreen: dimensions.isSmallScreen,
          uiSettings,
          hideSeries: true,
          hasRootFolder
        };
      }
      
      // Apply search filter first if there's a value
      if (typeof filterValue === 'string' && filterValue.trim()) {
        booksInSeries = booksInSeries.filter(createBookSearchFilter(filterValue, currentSeries.title));
      }

      // Apply hide unmonitored/missing filter ONLY if not searching
      // When searching, show all results regardless of monitored/missing status
      if (hideUnmonitoredMissing && !filterValue && booksInSeries.length > 0) {
        booksInSeries = booksInSeries.filter((book) => isLibraryBook(book));
      }

      // Never show empty series groups in the UI. A series with no visible books is either:
      // - orphaned metadata (0 linked books after profile/language filtering), or
      // - filtered out by the user's search/monitored toggles
      const hideSeries = booksInSeries.length === 0;

      let sortDir = 'asc';

      if (series.sortDirection === 'descending') {
        sortDir = 'desc';
      }

      let sortedBooks = [];
      if (series.sortKey === 'position') {
        sortedBooks = _.orderBy(booksInSeries,
          [(book) => positionOrderMap[book.id], (book) => positionMap[book.id] || ''],
          [sortDir, sortDir]);
      } else {
        sortedBooks = _.orderBy(booksInSeries, series.sortKey, sortDir);
      }

      return {
        id: currentSeries.id,
        label: currentSeries.title,
        items: sortedBooks,
        positionMap,
        columns: series.columns,
        sortKey: series.sortKey,
        sortDirection: series.sortDirection,
        isSmallScreen: dimensions.isSmallScreen,
        uiSettings,
        hideSeries,
        hasRootFolder
      };
    }
  );
}

const mapDispatchToProps = {
  toggleBooksMonitored,
  setSeriesTableOption,
  dispatchSetSeriesSort: setSeriesSort,
  executeCommand
};

class AuthorDetailsSeriesConnector extends Component {

  //
  // Listeners

  onTableOptionChange = (payload) => {
    this.props.setSeriesTableOption(payload);
  };

  onSortPress = (sortKey) => {
    this.props.dispatchSetSeriesSort({ sortKey });
  };

  onMonitorBookPress = (bookIds, monitored) => {
    this.props.toggleBooksMonitored({
      bookIds,
      monitored,
      mediaType: this.props.selectedMediaType
    });
  };

  //
  // Render

  render() {
    if (this.props.hideSeries) {
      return null;
    }

    return (
      <AuthorDetailsSeries
        {...this.props}
        onSortPress={this.onSortPress}
        onTableOptionChange={this.onTableOptionChange}
        onMonitorBookPress={this.onMonitorBookPress}
        selectedMediaType={this.props.selectedMediaType}
      />
    );
  }
}

AuthorDetailsSeriesConnector.propTypes = {
  authorId: PropTypes.number.isRequired,
  toggleBooksMonitored: PropTypes.func.isRequired,
  setSeriesTableOption: PropTypes.func.isRequired,
  dispatchSetSeriesSort: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  selectedMediaType: PropTypes.string.isRequired,
  hideSeries: PropTypes.bool,
  hasRootFolder: PropTypes.bool
};

export default connect(createMapStateToProps, mapDispatchToProps)(AuthorDetailsSeriesConnector);
