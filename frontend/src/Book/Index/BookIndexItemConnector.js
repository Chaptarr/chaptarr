/* eslint max-params: 0 */
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import createBookAuthorSelector from 'Store/Selectors/createBookAuthorSelector';
import createBookQualityProfileSelector from 'Store/Selectors/createBookQualityProfileSelector';
import createBookSelector from 'Store/Selectors/createBookSelector';
import createExecutingCommandsSelector from 'Store/Selectors/createExecutingCommandsSelector';

function selectShowSearchAction() {
  return createSelector(
    (state) => state.bookIndex,
    (bookIndex) => {
      const view = bookIndex.view;

      switch (view) {
        case 'posters':
          return bookIndex.posterOptions.showSearchAction;
        case 'banners':
          return bookIndex.bannerOptions.showSearchAction;
        case 'overview':
          return bookIndex.overviewOptions.showSearchAction;
        default:
          return bookIndex.tableOptions.showSearchAction;
      }
    }
  );
}

function createMapStateToProps() {
  // Create per-instance selectors once to maintain memoization
  const selectBook = createBookSelector();
  const selectAuthor = createBookAuthorSelector();
  const selectQualityProfile = createBookQualityProfileSelector();
  const selectShowSearch = selectShowSearchAction();
  const selectExecutingCommands = createExecutingCommandsSelector();
  
  // Stable empty result to avoid prop shape changes
  const EMPTY_RESULT = {};

  const extractAuthorName = (authorTitle, title) => {
    if (!authorTitle) {
      return '';
    }

    if (!title) {
      return authorTitle;
    }

    const suffix = ` ${title}`;
    return authorTitle.endsWith(suffix) ? authorTitle.slice(0, -suffix.length) : authorTitle;
  };

  return (state, ownProps) => {
    // Get book from props or state (compute only once)
    const bookId = ownProps.bookId ?? ownProps.id;
    const book = ownProps.book || (bookId != null ? selectBook(state, { bookId }) : undefined);
    
    if (!book) {
      return EMPTY_RESULT;
    }
    
    // Get dependent data using cached selectors
    const authorFromStore = selectAuthor(state, { authorId: book.authorId });
    const author = book.author ?? authorFromStore ?? {
      id: book.authorId,
      authorName: extractAuthorName(book.authorTitle, book.title),
      path: '',
      titleSlug: undefined
    };
    const qualityProfile = selectQualityProfile(state, { bookId: book.id });
    const showSearchAction = selectShowSearch(state, ownProps);
    const executingCommands = selectExecutingCommands(state);
    
    // Safe command checking with proper guards
    const commands = Array.isArray(executingCommands) ? executingCommands : [];
    
    const isRefreshingBook = commands.some((command) => {
      return (
        (command.name === commandNames.REFRESH_AUTHOR &&
          command.body?.authorId === book.authorId) ||
        (command.name === commandNames.REFRESH_BOOK &&
          command.body?.bookId === book.id)
      );
    });
    
    const isSearchingBook = commands.some((command) => {
      if (command.name === commandNames.AUTHOR_SEARCH) {
        return command.body?.authorId === book.authorId;
      }
      if (command.name === commandNames.BOOK_SEARCH) {
        const ids = command.body?.bookIds;
        return Array.isArray(ids) && ids.includes(book.id);
      }
      return false;
    });
    
    // Component expects flattened book fields via spread
    return {
      ...book,
      author,
      qualityProfile,
      showSearchAction,
      isRefreshingBook,
      isSearchingBook
    };
  };
}

const mapDispatchToProps = {
  dispatchExecuteCommand: executeCommand
};

class BookIndexItemConnector extends Component {

  //
  // Listeners

  onRefreshBookPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.REFRESH_BOOK,
      bookId: this.props.id
    });
  };

  onSearchPress = () => {
    this.props.dispatchExecuteCommand({
      name: commandNames.BOOK_SEARCH,
      bookIds: [this.props.id]
    });
  };

  //
  // Render

  render() {
    const {
      id,
      component: ItemComponent,
      ...otherProps
    } = this.props;

    if (!id) {
      return null;
    }

    return (
        <ItemComponent
          {...otherProps}
          id={id}
          onRefreshBookPress={this.onRefreshBookPress}
          onSearchPress={this.onSearchPress}
        />
      );
  }
}

BookIndexItemConnector.propTypes = {
  id: PropTypes.number,
  component: PropTypes.elementType.isRequired,
  dispatchExecuteCommand: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(BookIndexItemConnector);
