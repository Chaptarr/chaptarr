import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import BookIndexTableOptions from './BookIndexTableOptions';

function createMapStateToProps() {
  return createSelector(
    (state) => state.bookIndex.tableOptions,
    (tableOptions) => {
      return tableOptions;
    }
  );
}

export default connect(createMapStateToProps)(BookIndexTableOptions);
