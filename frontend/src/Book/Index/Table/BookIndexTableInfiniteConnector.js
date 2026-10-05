import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { setBookSort } from 'Store/Actions/bookIndexActions';
import withInfiniteBooks from '../withInfiniteBooks';
import BookIndexTable from './BookIndexTable';

// Columns the server can't sort a paged list by.
const unsortableColumnNames = new Set(['status', 'ratings']);

function createMapStateToProps() {
  return createSelector(
    (state) => state.app.dimensions,
    (state) => state.bookIndex.tableOptions,
    (state) => state.bookIndex.columns,
    (dimensions, tableOptions, columns) => {
      const pagedColumns = columns.map((column) => {
        if (unsortableColumnNames.has(column.name)) {
          return { ...column, isSortable: false };
        }

        return column;
      });

      return {
        isSmallScreen: dimensions.isSmallScreen,
        showBanners: tableOptions.showBanners,
        columns: pagedColumns
      };
    }
  );
}

function createMapDispatchToProps(dispatch) {
  return {
    onSortPress(sortKey) {
      dispatch(setBookSort({ sortKey }));
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(withInfiniteBooks(BookIndexTable));
