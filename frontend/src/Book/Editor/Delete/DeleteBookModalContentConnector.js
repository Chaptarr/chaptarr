import intersectionWith from 'lodash/intersectionWith';
import map from 'lodash/map';
import orderBy from 'lodash/orderBy';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import { bulkDeleteBook } from 'Store/Actions/bookIndexActions';
import DeleteBookModalContent from './DeleteBookModalContent';

function createMapStateToProps() {
  return createSelector(
    (state, { bookIds }) => bookIds,
    (state) => state.books.items,
    (state) => state.bookFiles.items,
    (bookIds, allBooks, allBookFiles) => {
      const selectedBook = intersectionWith(allBooks, bookIds, (s, id) => {
        return s.id === id;
      });

      const sortedBook = orderBy(selectedBook, 'title');

      const selectedFiles = intersectionWith(allBookFiles, bookIds, (s, id) => {
        return s.bookId === id;
      });

      const files = orderBy(selectedFiles, ['bookId', 'path']);

      const book = map(sortedBook, (s) => {
        return {
          title: s.title,
          path: s.path
        };
      });

      return {
        selectedCount: bookIds.length,
        book,
        files
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
      }));

      props.onModalClose();
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(DeleteBookModalContent);
