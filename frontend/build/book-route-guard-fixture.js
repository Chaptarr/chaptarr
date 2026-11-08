import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Route, Router } from 'react-router-dom';
import BookDetailsPageConnector from 'Book/Details/BookDetailsPageConnector';
import { set } from 'Store/Actions/baseActions';
import { fetchBooks } from 'Store/Actions/bookActions';
import createAppStore from 'Store/createAppStore';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/scaffolding.css';

const root = document.getElementById('root');
window.prepareBookRouteFixture = fetchTranslations;
window.mountBookRouteFixture = ({ route, books = [], authors = [], initialFetch }) => {
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory({ initialEntries: [route] });
  const store = createAppStore(history);
  for (const [section, items] of [['books', books], ['authors', authors]]) {
    store.dispatch(set({ section, items, itemMap: Object.fromEntries(items.map((item, index) => [item.id, index])), isFetching: false, isPopulated: true, error: null }));
  }
  window.bookRouteFixture = {
    navigate: (path) => history.push(path),
    fetch: (query) => store.dispatch(fetchBooks(query)),
    unmount: () => ReactDOM.unmountComponentAtNode(root),
    state: () => ({ books: store.getState().books, authors: store.getState().authors, path: history.location.pathname })
  };
  if (initialFetch) {
    store.dispatch(fetchBooks(initialFetch));
  }
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <Route path={['/book/:bookId', '/:mediaScope(ebook|audiobook)/book/:bookId']} component={BookDetailsPageConnector} />
      </Router>
    </Provider>, root);
};
