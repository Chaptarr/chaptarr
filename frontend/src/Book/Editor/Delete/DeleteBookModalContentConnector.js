import _ from 'lodash';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { bulkDeleteBook } from 'Store/Actions/bookIndexActions';
import DeleteBookModalContent from './DeleteBookModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { bookIds }) => bookIds,
    (state) => state.books.items,
    (state) => state.bookIndex,
    (bookIds, allBooks, { isDeleting, deleteError }) => {
      const selectedBook = _.intersectionWith(allBooks, bookIds, (s, id) => {
        return s.id === id;
      });

      const sortedBook = _.orderBy(selectedBook, 'title');

      const book = _.map(sortedBook, (s) => {
        return {
          title: s.title,
          path: s.path
        };
      });

      return {
        selectedCount: bookIds.length,
        isDeleting,
        deleteError,
        book,
        bookIds
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onDeleteSelectedPress(deleteFiles, addImportListExclusion) {
      dispatch(bulkDeleteBook({
        bookIds: props.bookIds,
        deleteFiles,
        addImportListExclusion
      })).done(() => props.onModalClose());
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(DeleteBookModalContent);
