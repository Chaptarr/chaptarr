import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { applyMiddleware, createStore } from 'redux';
import thunk from 'redux-thunk';
import SingleDeleteAuthorModal from 'Author/Delete/DeleteAuthorModal';
import DeleteAuthorModal from 'Author/Editor/Delete/DeleteAuthorModal';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

window.renderDeleteFixture = async(single = false) => {
  await fetchTranslations();
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  const state = {
    app: { dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: innerWidth <= 768 } },
    authors: { itemMap: { 1: 0, 2: 1 }, items: [
      { id: 1, authorName: 'Example Author', sortName: 'example', path: '/books/Example Author', statistics: { bookFileCount: 0, sizeOnDisk: 0 } },
      { id: 2, authorName: 'Auteur Exemple', sortName: 'exemple', path: '/ebooks/Auteur Exemple' }
    ] }
  };
  const store = createStore(() => state, applyMiddleware(thunk));
  const DeleteModal = single ? SingleDeleteAuthorModal : DeleteAuthorModal;
  ReactDOM.render(
    <Provider store={store}>
      <DeleteModal
        isOpen={true}
        {...(single ? { authorId: 1 } : { authorIds: [1, 2] })}
        onModalClose={() => ReactDOM.unmountComponentAtNode(document.getElementById('root'))}
      />
    </Provider>, document.getElementById('root'));
};
