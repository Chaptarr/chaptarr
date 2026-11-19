import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import EditImportListModal from 'Settings/ImportLists/ImportLists/EditImportListModal';
import ManageImportListsModal from 'Settings/ImportLists/ImportLists/Manage/ManageImportListsModal';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

window.setListTheme = (theme) => {
  Object.entries(themes[theme]).forEach(([key, value]) => {
    document.documentElement.style.setProperty(`--${key}`, value);
  });
};
window.setListTheme('dark');

window.renderList = (list) => {
  list = JSON.parse(JSON.stringify(list));
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const store = createAppStore(history);
  store.dispatch(set({ section: 'app', dimensions: { width: window.innerWidth, height: window.innerHeight } }));
  store.dispatch(set({ section: 'settings.metadataProfiles', isPopulated: true, items: [{ id: 1, name: 'Standard' }] }));
  store.dispatch(set({ section: 'settings.qualityProfiles', isPopulated: true, items: [{ id: 1, name: 'Any', mediaType: 0 }] }));
  store.dispatch(set({ section: 'tags', isPopulated: true, items: [{ id: 1, label: 'audio-tag' }, { id: 2, label: 'other-tag' }, { id: 3, label: 'ebook-tag' }] }));
  store.dispatch(set({
    section: 'settings.importLists', isPopulated: true, isSchemaPopulated: true,
    items: list.id ? [list] : [], schema: [list], selectedSchema: list, pendingChanges: {}
  }));
  window.listState = () => store.getState().settings.importLists;
  window.listRootState = () => store.getState().settings.rootFolders;
  window.listCloseCount = 0;
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <EditImportListModal id={list.id} isOpen={true}
          onModalClose={() => {
            window.listCloseCount++;
          }}
        />
      </Router>
    </Provider>, root
  );
};

window.renderManageLists = (lists, catalogs) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const store = createAppStore(history);
  store.dispatch(set({ section: 'app', dimensions: { width: window.innerWidth, height: window.innerHeight } }));
  store.dispatch(set({ section: 'settings.qualityProfiles', isPopulated: true, items: catalogs.quality }));
  store.dispatch(set({ section: 'settings.metadataProfiles', isPopulated: true, items: catalogs.metadata }));
  store.dispatch(set({ section: 'tags', isPopulated: true, items: [{ id: 1, label: 'audio-tag' }] }));
  store.dispatch(set({ section: 'settings.importLists', isPopulated: true, items: lists }));
  window.manageListState = () => store.getState().settings.importLists;
  window.manageListCloseCount = 0;
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <ManageImportListsModal isOpen={true}
          onModalClose={() => {
            window.manageListCloseCount++;
          }}
        />
      </Router>
    </Provider>, root
  );
};

fetchTranslations().then(() => {
  window.fixtureReady = true;
});
