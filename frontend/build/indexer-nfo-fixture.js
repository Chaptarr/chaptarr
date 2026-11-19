import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import EditIndexerModalConnector from 'Settings/Indexers/Indexers/EditIndexerModalConnector';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

window.renderIndexer = (indexer, theme = 'dark') => {
  Object.entries(themes[theme]).forEach(([key, value]) => document.documentElement.style.setProperty(`--${key}`, value));
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  const history = createMemoryHistory();
  const store = createAppStore(history);
  store.dispatch(set({ section: 'app', dimensions: { width: window.innerWidth, height: window.innerHeight } }));
  store.dispatch(set({ section: 'tags', isPopulated: true, items: [] }));
  store.dispatch(set({ section: 'settings.indexers', isPopulated: true, items: [indexer], pendingChanges: {} }));
  window.indexerState = () => store.getState().settings.indexers;
  window.categoryState = () => store.getState().providerOptions.newznabCategories;
  window.closeCount = 0;
  const render = (open) => ReactDOM.render(
    <Provider store={store}><Router history={history}>
      <EditIndexerModalConnector id={indexer.id} isOpen={open}
        onModalClose={() => {
          window.closeCount++;
          render(false);
        }}
      />
    </Router></Provider>, document.getElementById('root')
  );
  window.reopenIndexer = () => render(true);
  render(true);
};
fetchTranslations().then(() => {
  window.fixtureReady = true;
});
