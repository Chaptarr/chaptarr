import { createMemoryHistory } from 'history';
import PropTypes from 'prop-types';
import React from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import QueueRow from 'Activity/Queue/QueueRow';
import BookDetailsConnector from 'Book/Details/BookDetailsConnector';
import NarratorSearchModalContentConnector from 'Book/Narrator/NarratorSearchModalContentConnector';
import BookFileEditorTableContent from 'BookFile/Editor/BookFileEditorTableContent';
import BookEditionSelectInputConnector from 'Components/Form/BookEditionSelectInputConnector';
import Modal from 'Components/Modal/Modal';
import InteractiveImportModal from 'InteractiveImport/InteractiveImportModal';
import AddNewBookModalContent from 'Search/Book/AddNewBookModalContent';
import GeneralSettings from 'Settings/General/GeneralSettings';
import EditQualityProfileModalContent from 'Settings/Profiles/Quality/EditQualityProfileModalContent';
import { set } from 'Store/Actions/baseActions';
import { addNarratorVariant, clearNarratorDiscovery } from 'Store/Actions/narratorActions';
import createAppStore from 'Store/createAppStore';
import themes from 'Styles/Themes';
import { fetchTranslations } from 'Utilities/String/translate';

import './queue-removal-fixture';
import 'Styles/globals.css';
import '../src/index.css';

window.renderAddBookValidationFixture = (error) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  ReactDOM.render(
    <Modal isOpen={true} onModalClose={() => {}}>
      <AddNewBookModalContent
        bookTitle="A Book"
        authorName="An Author"
        images={[]}
        isAdding={false}
        isQueued={false}
        isLoadingAuthor={true}
        rootFoldersPopulated={false}
        isSmallScreen={true}
        initialMediaType="audiobook"
        addError={error}
        onModalClose={() => {}}
        onAddBookPress={() => {}}
      />
    </Modal>, root);
};

window.verifyAllowedHostsRestart = () => {
  const settings = new GeneralSettings({ savedSettings: { allowedHosts: 'books.example' }, isSaving: false });
  settings.setState = (state) => {
    settings.state = { ...settings.state, ...state };
  };
  settings.componentDidUpdate({ savedSettings: { allowedHosts: 'old.example' }, isSaving: true });
  return settings.state.isRestartRequiredModalOpen;
};

window.renderNarratorDiscoveryFixture = () => {
  const store = createAppStore(createMemoryHistory());
  store.dispatch(set({ section: 'books', items: [{ id: 20, title: 'Recording', authorName: 'Author' }] }));
  window.narratorDiscoveryState = () => store.getState().narrator.discovery[20];
  window.injectInvalidNarratorFixture = () => store.dispatch(set({
    section: 'narrator', discovery: { 20: { ...store.getState().narrator.discovery[20], filteredNarrators: ['Invalid edition'] } }
  }));
  return new Promise((resolve) => {
    const unsubscribe = store.subscribe(() => {
      const discovery = store.getState().narrator.discovery[20];
      const { isSearching, error } = store.getState().narrator;
      if (isSearching || (!discovery && !error)) {
        return;
      }
      unsubscribe();
      resolve(discovery);
    });
    const root = document.getElementById('root');
    ReactDOM.unmountComponentAtNode(root);
    ReactDOM.render(
      <Provider store={store}>
        <Modal isOpen={true} onModalClose={() => {}}>
          <NarratorSearchModalContentConnector
            bookId={20}
            bookTitle="Recording"
            authorName="Author"
            onModalClose={() => {}}
          />
        </Modal>
      </Provider>, root);
  });
};

window.selectNarratorFixture = ({ bookFileCount, anyEditionOk, repeat = false, closeBeforeResponse = false }) => {
  const store = createAppStore(createMemoryHistory());
  const original = { id: 20, authorId: 3, anyEditionOk, statistics: { bookFileCount } };
  store.dispatch(set({
    section: 'books',
    items: [original]
  }));
  const narrator = { editionId: 30, name: 'Selected Reader', narratorNames: ['Selected Reader', 'Another Reader', 'Third Reader'], monitored: false };
  store.dispatch(set({
    section: 'narrator',
    discovery: {
      20: {
        filteredNarrators: [narrator, { ...narrator, editionId: 32 }],
        existingCopyNarrators: [],
        totalExisting: 0,
        totalAvailable: 2,
        totalFiltered: 2
      }
    }
  }));
  const select = () => new Promise((resolve) => store.dispatch(addNarratorVariant({
    bookId: 20, narrator, searchForNewBook: true, onSuccess: resolve
  })));
  const first = select();
  if (closeBeforeResponse) {
    store.dispatch(clearNarratorDiscovery({ bookId: 20 }));
  }
  return first.then(async(result) => {
    if (repeat) {
      await select();
    }
    return {
      result,
      original: store.getState().books.items.find((book) => book.id === 20),
      discovery: store.getState().narrator.discovery,
      messages: store.getState().app.messages.items
    };
  });
};

window.selectNarratorWithoutEditionFixture = () => {
  const store = createAppStore(createMemoryHistory());
  store.dispatch(set({ section: 'books', items: [{ id: 20, authorId: 3 }] }));
  store.dispatch(addNarratorVariant({ bookId: 20, narrator: { name: 'Selected Reader' }, searchForNewBook: true }));
  return store.getState().narrator;
};

window.renderManualImportFixture = (themeName, folderMode = false, awaitingImportConfirmation = false) => {
  Object.entries(themes[themeName]).forEach(([key, value]) => {
    document.documentElement.style.setProperty(`--${key}`, value);
  });

  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);

  const history = createMemoryHistory();
  const store = createAppStore(history);
  store.dispatch(set({
    section: 'app',
    dimensions: { width: window.innerWidth, height: window.innerHeight, isSmallScreen: window.innerWidth <= 768 }
  }));
  store.dispatch(set({
    section: 'settings.mediaManagement',
    isPopulated: true,
    item: {
      bookMatchingStrictness: 'balanced',
      usePathAsTagsFallback: true
    }
  }));

  window.manualImportState = () => store.getState().interactiveImport;
  window.manualImportCloseCount = 0;

  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        {
          folderMode ?
            <InteractiveImportModal
              isOpen={true}
              folder="/downloads"
              onModalClose={() => {
                window.manualImportCloseCount++;
              }}
            /> :
            <table>
              <tbody>
                <QueueRow
                  id={999}
                  downloadId="queue-download"
                  title="Queued book"
                  status="completed"
                  trackedDownloadStatus="warning"
                  trackedDownloadState="importBlocked"
                  awaitingImportConfirmation={awaitingImportConfirmation}
                  quality={{ quality: { id: 1, name: 'Any' }, revision: { version: 1 } }}
                  customFormatScore={0}
                  protocol="usenet"
                  showRelativeDates={false}
                  shortDateFormat="MMM D YYYY"
                  timeFormat="HH:mm"
                  columns={[{ name: 'actions', isVisible: true }]}
                  onSelectedChange={() => {}}
                  onGrabPress={() => {}}
                  onCancelConversionPress={() => {}}
                  onRetryImportPress={() => {}}
                  onRemoveQueueItemPress={() => {}}
                  onQueueRowModalOpenOrClose={(isOpen) => {
                    if (!isOpen) {
                      window.manualImportCloseCount++;
                    }
                  }}
                />
              </tbody>
            </table>
        }
      </Router>
    </Provider>, root);
};

function UpgradeProfileFixture({ profileType }) {
  const [item, setItem] = React.useState({ name: { value: 'Upgrade policy' }, profileType: { value: profileType }, upgradeAllowed: { value: true } });
  const noop = () => {};
  return (<Modal isOpen={true} onModalClose={noop}>
    <EditQualityProfileModalContent
      editGroups={false} advancedSettings={false}
      isFetching={false} isSaving={false}
      qualities={[]} convertToQualities={[]}
      item={item} isInUse={false}
      onInputChange={({ name, value }) => {
        window.profileChanges.push({ name, value });
        setItem((previous) => ({ ...previous, [name]: { value } }));
      }}
      onCutoffChange={noop} onConvertToQualityChange={noop}
      onReleasePriorityChange={noop}
      onEasyCustomFormatPresetChange={noop} onAdvancedSettingsPress={noop}
      onSavePress={noop} onContentHeightChange={noop}
      onModalClose={noop}
    />
  </Modal>);
}

UpgradeProfileFixture.propTypes = {
  profileType: PropTypes.string.isRequired
};

window.renderUpgradeProfileFixture = (profileType) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  window.profileChanges = [];
  const history = createMemoryHistory();
  const store = createAppStore(history);
  ReactDOM.render(<Provider store={store}><Router history={history}><UpgradeProfileFixture profileType={profileType} /></Router></Provider>, root);
};

window.renderFileRecordingFixture = (mediaType) => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const store = createAppStore(history);
  store.dispatch(set({
    section: 'settings.ui',
    item: {
      showRelativeDates: false,
      shortDateFormat: 'MMM D YYYY',
      longDateFormat: 'dddd, MMMM D YYYY',
      timeFormat: 'HH:mm'
    },
    isPopulated: true
  }));
  const noop = () => {};
  ReactDOM.render(
    <Provider store={store}>
      <Router history={history}>
        <BookFileEditorTableContent
          isFetching={false} isPopulated={true}
          isDeleting={false} isSaving={false}
          items={[{ id: 1, path: `/books/recording.${mediaType === 'audiobook' ? 'm4b' : 'epub'}`,
            mediaType, isGraphicAudio: true, size: 1024, dateAdded: '2026-01-01T00:00:00Z',
            quality: { quality: { id: 1, name: 'Any' }, revision: { version: 1 } }, qualityCutoffNotMet: false }]}
          qualities={[]} columns={[{ name: 'path', label: 'Path', isVisible: true }]}
          onSortPress={noop} onQualityChange={noop}
          onDeletePress={noop} dispatchDeleteBookFile={noop}
        />
      </Router>
    </Provider>, root);
};

window.verifyEditionFileReload = (beforePinned, afterPinned, changedEdition) => {
  const edition = { id: 1, monitored: true };
  const before = { id: 1, anyEditionOk: !beforePinned, editions: [edition] };
  const connector = new BookDetailsConnector.WrappedComponent(before);
  connector.props = { ...before, anyEditionOk: !afterPinned,
    editions: [{ ...edition, id: changedEdition ? 2 : 1 }] };
  let fetches = 0;
  let clears = 0;
  connector.populate = () => {
    fetches++;
  };
  connector.unpopulate = () => {
    clears++;
  };
  connector.componentDidUpdate(before);
  return { fetches, clears };
};

window.renderEditionPickerFixture = () => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const store = createAppStore(history);
  window.editionPickerChanges = [];
  ReactDOM.render(
    <Provider store={store}>
      <BookEditionSelectInputConnector
        name="editions"
        bookEditions={{ value: [
          { foreignEditionId: 'solo', title: 'Recording', readingFormatId: 2, narratorNames: ['Jim Dale'], monitored: true },
          { foreignEditionId: 'cast', title: 'Recording', readingFormatId: 2, narratorNames: ['Alice Reader', 'Bob Reader', 'Carol Reader'] },
          { foreignEditionId: 'text', title: 'Recording', readingFormatId: 3, narratorNames: ['Should not be shown'] }
        ] }}
        onChange={(change) => window.editionPickerChanges.push(change)}
      />
    </Provider>, root);
};

fetchTranslations().then(() => {
  window.manualImportFixtureReady = true;
});
