import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { applyMiddleware, createStore } from 'redux';
import { enableBatching } from 'redux-batched-actions';
import thunk from 'redux-thunk';
import SingleDeleteAuthorModal from 'Author/Delete/DeleteAuthorModal';
import DeleteAuthorModal from 'Author/Editor/Delete/DeleteAuthorModal';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

window.renderDeleteFixture = async(single = false, selectedMediaType = 'all') => {
  await fetchTranslations();
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  const state = {
    authorIndex: {},
    books: { items: [], itemMap: {} },
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
    return current;
  };
  const store = createStore(enableBatching(reducer), applyMiddleware(thunk));
  window.getDeleteFixtureState = () => store.getState();
  const DeleteModal = single ? SingleDeleteAuthorModal : DeleteAuthorModal;
  ReactDOM.render(
    <Provider store={store}>
      <DeleteModal
        isOpen={true}
        selectedMediaType={selectedMediaType}
        {...(single ? { authorId: 1 } : { authorIds: [1, 2] })}
        onModalClose={() => ReactDOM.unmountComponentAtNode(document.getElementById('root'))}
      />
    </Provider>, document.getElementById('root'));
};
