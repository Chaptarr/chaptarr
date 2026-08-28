import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import createAuthorSelector from 'Store/Selectors/createAuthorSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import createCommandsSelector from 'Store/Selectors/createCommandsSelector';
import { isCommandExecuting } from 'Utilities/Command';
import BookSearchCell from './BookSearchCell';

function createMapStateToProps() {
  return createSelector(
    (state, { bookId }) => bookId,
    createAuthorSelector(),
    createBookSelector(),
    createCommandsSelector(),
    (bookId, author, book, commands) => {
      const isSearching = commands.some((command) => {
        const bookSearch = command.name === commandNames.BOOK_SEARCH;

        if (!bookSearch) {
          return false;
        }

        return (
          isCommandExecuting(command) &&
          command.body.bookIds.indexOf(bookId) > -1
        );
      });

      const isAvailable = book ? Date.parse(book.releaseDate) < new Date() : false;

      return {
        isSearching,
        bookFiles: book ? book.bookFiles : [],
        isAvailable
      };
    }
  );
}

function createMapDispatchToProps(dispatch, props) {
  return {
    onSearchPress(name, path) {
      dispatch(executeCommand({
        name: commandNames.BOOK_SEARCH,
        bookIds: [props.bookId]
      }));
    }
  };
}

export default connect(createMapStateToProps, createMapDispatchToProps)(BookSearchCell);
