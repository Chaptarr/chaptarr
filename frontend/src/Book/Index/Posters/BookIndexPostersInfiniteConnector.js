import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createDimensionsSelector from 'Store/Selectors/createDimensionsSelector';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import withInfiniteBooks from '../withInfiniteBooks';
import BookIndexPostersInfinite from './BookIndexPostersInfinite';

function createMapStateToProps() {
  return createSelector(
    (state) => state.bookIndex,
    createUISettingsSelector(),
    createDimensionsSelector(),
    (bookIndex, uiSettings, dimensions) => {
      return {
        posterOptions: bookIndex.posterOptions,
        sortKey: bookIndex.sortKey,
        showRelativeDates: uiSettings.showRelativeDates,
        shortDateFormat: uiSettings.shortDateFormat,
        timeFormat: uiSettings.timeFormat,
        isSmallScreen: dimensions.isSmallScreen
      };
    }
  );
}

export default connect(createMapStateToProps)(withInfiniteBooks(BookIndexPostersInfinite));
