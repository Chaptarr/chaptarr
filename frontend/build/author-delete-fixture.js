import React from 'react';
import ReactDOM from 'react-dom';
import { connect, Provider } from 'react-redux';
import { applyMiddleware, createStore } from 'redux';
import { enableBatching } from 'redux-batched-actions';
import thunk from 'redux-thunk';
import SingleDeleteAuthorModal from 'Author/Delete/DeleteAuthorModal';
import DeleteAuthorModal from 'Author/Editor/Delete/DeleteAuthorModal';
import SingleDeleteBookModal from 'Book/Delete/DeleteBookModal';
import DeleteBookModal from 'Book/Editor/Delete/DeleteBookModal';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

const BookPage = connect((state) => ({ hasBook: state.books.items.some((book) => book.id === 10) }))(
  ({ hasBook, children }) => (hasBook ? children : null)
);

window.renderDeleteFixture = async(single = false, selectedMediaType = 'all', entity = 'author', staleError = false) => {
  await fetchTranslations();
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  const books = entity === 'book' ? [
    { id: 10, title: 'Dune', authorId: 1, mediaType: 'audiobook', statistics: { bookFileCount: 2, sizeOnDisk: 2048 } },
    { id: 11, title: 'Dune Messiah', authorId: 1, mediaType: 'audiobook', statistics: { bookFileCount: 1, sizeOnDisk: 1024 } }
  ] : [];
  const deleteError = staleError ? { responseJSON: { message: 'Previous deletion failed' } } : null;
  window.deleteFixtureCloses = 0;
  window.deleteFixtureNavigation = [];
  const state = {
    authorIndex: {},
    bookIndex: { isDeleting: false, deleteError },
    bookFiles: { items: [{ id: 1, bookId: 10, path: '/audiobooks/Frank Herbert/Dune/Part 01.mp3' }] },
    books: { items: books, itemMap: Object.fromEntries(books.map((book, index) => [book.id, index])), isDeleting: false, deleteError },
    app: { dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: innerWidth <= 768 } },
    authors: { itemMap: { 1: 0, 2: 1 }, items: [
      { id: 1, authorName: 'Frank Herbert', sortName: 'herbert frank', path: '/library/Dune/Frank Herbert', audiobookFolder: '/audiobooks/Dune/Frank Herbert', ebookFolder: '/ebooks/Dune/Frank Herbert', statistics: { bookFileCount: 0, sizeOnDisk: 0 } },
      { id: 2, authorName: 'Brian Herbert', sortName: 'herbert brian', path: '/library/Dune/Brian Herbert', audiobookFolder: '/library/Dune/Brian Herbert', ebookFolder: '/library/Dune/Brian Herbert' }
    ] }
  };
  const reducer = (current = state, action) => {
    if (action.type === 'base/set') {
      const { section, ...values } = action.payload;
      return { ...current, [section]: { ...current[section], ...values } };
    }
    if (action.type === 'base/update') {
      const { section, data } = action.payload;
      return { ...current, [section]: { ...current[section], items: data, itemMap: Object.fromEntries(data.map((item, index) => [item.id, index])) } };
    }
    if (action.type === 'base/removeItem') {
      const { section, id } = action.payload;
      const items = current[section].items.filter((item) => item.id !== id);
      return { ...current, [section]: { ...current[section], items, itemMap: Object.fromEntries(items.map((item, index) => [item.id, index])) } };
    }
    if (action.type === '@@router/CALL_HISTORY_METHOD') {
      window.deleteFixtureNavigation.push(action.payload.args[0]);
    }
    return current;
  };
  const store = createStore(enableBatching(reducer), applyMiddleware(thunk));
  window.getDeleteFixtureState = () => store.getState();
  let DeleteModal = single ? SingleDeleteAuthorModal : DeleteAuthorModal;
  let selection = single ? { authorId: 1 } : { authorIds: [1, 2] };
  if (entity === 'book') {
    DeleteModal = single ? SingleDeleteBookModal : DeleteBookModal;
    selection = single ? { bookId: 10, authorId: 1 } : { bookIds: [10, 11] };
  }
  const Page = entity === 'book' && single ? BookPage : React.Fragment;
  ReactDOM.render(
    <Provider store={store}>
      <Page>
        <DeleteModal
          isOpen={true}
          selectedMediaType={selectedMediaType}
          {...selection}
          onModalClose={() => {
            window.deleteFixtureCloses++;
            ReactDOM.unmountComponentAtNode(document.getElementById('root'));
          }}
        />
      </Page>
    </Provider>, document.getElementById('root'));
};
