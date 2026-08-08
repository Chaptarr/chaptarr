import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as bookInfiniteScrollActions from 'Store/Actions/bookInfiniteScrollActions';
import {
  selectPageStatus,
  selectQueryBooks } from 'Store/Reducers/bookInfiniteScrollReducer';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import getBookIndexQuery from '../getBookIndexQuery';
import { BookIndexPostersInfinite } from './BookIndexPostersInfinite';

function createMapStateToProps() {
  return createSelector(
    (state) => state.bookIndex,
    (state) => state.bookInfiniteScroll,
    (state) => state.app.selectedMediaType,
    createUISettingsSelector(),
    createDimensionsSelector(),
    (bookIndex, bookInfiniteScroll, selectedMediaType, uiSettings, dimensions) => {
      const { queryKey, queryParams } = getBookIndexQuery(bookIndex, selectedMediaType);

      const query = bookInfiniteScroll.queries[queryKey] || {};
      const pageSize = bookInfiniteScroll.pageSize;
      const pages = query.pages || {};
      const entities = bookInfiniteScroll.entities || {};

      return {
        // From bookIndex
        posterOptions: bookIndex.posterOptions,
        sortKey: bookIndex.sortKey,

        // From infinite scroll
        queryKey,
        queryParams,
        items: selectQueryBooks({ bookInfiniteScroll }, queryKey),
        totalCount: query.totalCount,
        buckets: query.buckets,
        pageSize: bookInfiniteScroll.pageSize,
        getBookAtIndex: (index) => {
          const pageIndex = Math.floor(index / pageSize);
          const pageOffset = index % pageSize;
          const page = pages[pageIndex];
          const id = page?.ids?.[pageOffset];
          return id ? entities[id] : undefined;
        },
        getPageStatus: (qKey, pageIndex) => selectPageStatus({ bookInfiniteScroll }, qKey, pageIndex),

        // Error states for minimal error handling
        bucketsError: query.buckets?.error,
        hasAnyPageErrors: query.pages && Object.values(query.pages).some((page) => page.error),

        // UI settings
        showRelativeDates: uiSettings.showRelativeDates,
        shortDateFormat: uiSettings.shortDateFormat,
        timeFormat: uiSettings.timeFormat,
        isSmallScreen: dimensions.isSmallScreen
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchBooksPage: bookInfiniteScrollActions.fetchBooksPage,
  dispatchFetchBuckets: bookInfiniteScrollActions.fetchBookBuckets,
  dispatchSetActiveQuery: bookInfiniteScrollActions.setActiveQuery,
  dispatchInvalidateQuery: bookInfiniteScrollActions.invalidateQuery,
  dispatchJumpToLetter: bookInfiniteScrollActions.jumpToLetter,
  dispatchAbortAllRequests: bookInfiniteScrollActions.abortAllRequests
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookIndexPostersInfinite);
