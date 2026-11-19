import { createMemoryHistory } from 'history';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import AuthorEditorFooter from 'Author/Editor/AuthorEditorFooter';
import { set } from 'Store/Actions/baseActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import BackupsConnector from 'System/Backup/BackupsConnector';
import SettingsRestoreModal from 'System/SettingsBackup/SettingsRestoreModal';
import { fetchTranslations } from 'Utilities/String/translate';

import 'Styles/globals.css';
import '../src/index.css';

const history = createMemoryHistory();
const store = createAppStore(history);
store.dispatch(set({ section: 'settings.ui', item: { shortDateFormat: 'MMM D YYYY', longDateFormat: 'dddd, MMMM D YYYY', timeFormat: 'HH:mm', showRelativeDates: false } }));
window.fixtureState = () => store.getState();
window.setFixtureTheme = (name) => Object.entries(themes[name]).forEach(([key, value]) => {
  document.documentElement.style.setProperty(`--${key}`, value);
});
window.setFixtureTheme('dark');
window.showSettingsRestoreError = () => {
  const element = document.createElement('div');
  document.body.appendChild(element);
  let modal = null;
  const close = () => {
    ReactDOM.unmountComponentAtNode(element);
    element.remove();
  };
  const setModal = (value) => {
    modal = value;
  };
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <SettingsRestoreModal
          ref={setModal}
          isOpen={true}
          onModalClose={close}
        />
      </Router>
    </Provider>, element
  );
  modal.setState({ manualFilePath: '/config/test.chaptarr-settings-backup.json', passphrase: 'synthetic password', categories: { remotePathMappings: true } }, modal.onRestorePress);
};
window.showTagsFooter = (selectedMediaType) => {
  store.dispatch(set({ section: 'authors', items: [{ id: 1, authorName: 'Frank Herbert', tags: [1, 2], audiobookTags: [1], ebookTags: [2] }], itemMap: { 1: 0 } }));
  store.dispatch(set({ section: 'tags', items: [{ id: 1, label: 'audio-tag' }, { id: 2, label: 'ebook-tag' }] }));
  window.tagChanges = [];
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <AuthorEditorFooter
          authorIds={[1]}
          selectedCount={1}
          selectedMediaType={selectedMediaType}
          isSaving={false}
          isDeleting={false}
          isOrganizingAuthor={false}
          isRetaggingAuthor={false}
          showMetadataProfile={false}
          onSaveSelected={(changes) => window.tagChanges.push(changes)}
          onOrganizeAuthorPress={() => {}}
          onRetagAuthorPress={() => {}}
        />
      </Router>
    </Provider>, document.getElementById('root')
  );
};
fetchTranslations().then(() => {
  ReactDOM.render(<Provider store={store}><Router history={history}><BackupsConnector /></Router></Provider>, document.getElementById('root'));
  window.fixtureReady = true;
});
