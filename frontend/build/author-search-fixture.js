import { ConnectedRouter } from 'connected-react-router';
import { createMemoryHistory } from 'history';
import PropTypes from 'prop-types';
import React, { useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Router } from 'react-router-dom';
import { Tab, TabList, TabPanel, Tabs } from 'react-tabs';
import { applyMiddleware, createStore } from 'redux';
import { enableBatching } from 'redux-batched-actions';
import thunk from 'redux-thunk';
import ApplyTheme from 'App/ApplyTheme';
import AuthorDetailsConnector from 'Author/Details/AuthorDetailsConnector';
import AuthorDetailsSeriesConnector from 'Author/Details/AuthorDetailsSeriesConnector';
import EditAuthorModal from 'Author/Edit/EditAuthorModal';
import EditAuthorModalConnector from 'Author/Edit/EditAuthorModalConnector';
import AuthorEditorFooter from 'Author/Editor/AuthorEditorFooter';
import AuthorIndexItemConnector from 'Author/Index/AuthorIndexItemConnector';
import MessagesConnector from 'Components/Page/Sidebar/Messages/MessagesConnector';
import SignalRConnector from 'Components/SignalRConnector';
import FilterWarning from 'InteractiveSearch/FilterWarning';
import InteractiveSearchRow from 'InteractiveSearch/InteractiveSearchRow';
import InteractiveSearchTable from 'InteractiveSearch/InteractiveSearchTable';
import AddNewAuthorModal from 'Search/Author/AddNewAuthorModal';
import AddNewAuthorSearchResultConnector from 'Search/Author/AddNewAuthorSearchResultConnector';
import AddNewBookSearchResultConnector from 'Search/Book/AddNewBookSearchResultConnector';
import AddNewSeriesModal from 'Search/Series/AddNewSeriesModal';
import { defaultState as appState, reducers as appReducer, showMessage } from 'Store/Actions/appActions';
import { toggleAuthorMonitored } from 'Store/Actions/authorActions';
import { set } from 'Store/Actions/baseActions';
import { cancelCommand, defaultState as commandState, fetchCommands, finishCommand, reducers as commandReducer, removeCommand, showCommandProgress, updateCommand } from 'Store/Actions/commandActions';
import { defaultState as releaseState, grabRelease, receiveAuthorSearchResults, reducers as releaseReducer, setBypassFilters } from 'Store/Actions/releaseActions';
import { addBook } from 'Store/Actions/searchActions';
import createAppStore from 'Store/createAppStore';
import QueuedTaskRow from 'System/Tasks/Queued/QueuedTaskRow';
import { getAuthorSearchBookCount } from 'Utilities/Author/getAuthorStatisticsForMediaType';
import getNewBook from 'Utilities/Book/getNewBook';
import { fetchTranslations } from 'Utilities/String/translate';

import '../src/polyfills';
import 'Styles/globals.css';
import '../src/index.css';

const Detail = AuthorDetailsConnector.WrappedComponent;
const Index = AuthorIndexItemConnector.WrappedComponent;
const SignalR = SignalRConnector.WrappedComponent;
const noop = () => {};
let fixtureStore = null;

function SearchAction({ onSearchPress }) {
  return <button onClick={onSearchPress}>{'Search monitored'}</button>;
}

SearchAction.propTypes = { onSearchPress: PropTypes.func.isRequired };

function GroupedSearchTabs({ authorId, mediaType, searchCount }) {
  const [selectedTabIndex, setSelectedTabIndex] = useState(0);
  const [isBookSearchMounted, setIsBookSearchMounted] = useState(false);

  return (
    <div>
      <Tabs
        selectedIndex={selectedTabIndex}
        onSelect={(index) => setSelectedTabIndex(index)}
      >
        <TabList>
          <Tab>{'Books'}</Tab>
          <Tab>{'Series'}</Tab>
          <Tab>{'History'}</Tab>
          <Tab>{'Search tab'}</Tab>
          <Tab>{'Files'}</Tab>
        </TabList>
        <TabPanel><div>{'Books panel'}</div></TabPanel>
        <TabPanel><div>{'Series panel'}</div></TabPanel>
        <TabPanel><div>{'History panel'}</div></TabPanel>
        <TabPanel
          data-testid="author-search-panel"
          forceRender={true}
          hidden={selectedTabIndex !== 3}
        >
          <InteractiveSearchTable
            key={`${authorId}:${mediaType}`}
            type="author"
            authorId={authorId}
            selectedMediaType={mediaType}
            searchCount={searchCount}
          />
        </TabPanel>
        <TabPanel><div>{'Files panel'}</div></TabPanel>
      </Tabs>
      {isBookSearchMounted ?
        <div role="dialog" aria-label="Book search modal">
          <InteractiveSearchTable
            type="book"
            bookId={900}
            selectedMediaType={mediaType}
          />
          <button onClick={() => setIsBookSearchMounted(false)}>{'Close book modal'}</button>
        </div> :
        <button onClick={() => setIsBookSearchMounted(true)}>{'Open book modal'}</button>}
    </div>
  );
}

GroupedSearchTabs.propTypes = {
  authorId: PropTypes.number.isRequired,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  searchCount: PropTypes.number.isRequired
};

function LegacyGrabFixture({ onGrabPress }) {
  return (
    <table>
      <tbody>
        <InteractiveSearchRow
          rank={1}
          guid="legacy-unmapped"
          protocol="torrent"
          age={0}
          ageHours={0}
          ageMinutes={0}
          publishDate="2026-09-19T00:00:00Z"
          title="Legacy Unmapped Release"
          infoUrl="https://example.test/legacy-unmapped"
          indexer="Mock Indexer"
          indexerId={17}
          size={1024}
          quality={{ quality: { id: 1, name: 'Unknown' }, revision: { real: 0, version: 1, isRepack: false } }}
          customFormats={[]}
          customFormatScore={0}
          indexerFlags={0}
          authorId={null}
          bookId={null}
          rejections={['Error']}
          downloadAllowed={false}
          isGrabbing={false}
          isGrabbed={false}
          searchPayload={{ bookId: 900, initialMediaType: 'audiobook', bypassFilters: false }}
          longDateFormat="MMMM D, YYYY"
          timeFormat="h:mm A"
          onGrabPress={onGrabPress}
        />
      </tbody>
    </table>
  );
}

LegacyGrabFixture.propTypes = { onGrabPress: PropTypes.func.isRequired };

window.renderAuthorSearchFixture = async(view, audio, ebook, overrides = {}, preserveInstance = false) => {
  await fetchTranslations();
  if (!preserveInstance) {
    ReactDOM.unmountComponentAtNode(document.getElementById('root'));
    fixtureStore = null;
  }
  window.searchCommands = [];
  const initialState = {
    app: { ...appState, dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: false } },
    releases: releaseState,
    commands: commandState,
    authors: { items: [], itemMap: {} },
    customFilters: { items: [] },
    settings: { ui: { item: { theme: 'light', longDateFormat: 'MMMM D, YYYY', shortDateFormat: 'MMM D, YYYY', timeFormat: 'h:mm A' } } }
  };
  const store = preserveInstance && fixtureStore ? fixtureStore : createStore(enableBatching((state = initialState, action) => ({
    ...state,
    app: appReducer(state.app, action),
    releases: releaseReducer(state.releases, action),
    commands: commandReducer(state.commands, action)
  })), applyMiddleware(thunk));
  fixtureStore = store;
  window.readReleaseResults = () => store.getState().releases;
  window.readAuthorSearchState = () => store.getState().releases.authorSearch;
  window.readSearchCommands = () => store.getState().commands;
  const commandSignalR = new SignalR({
    dispatchFetchCommands: () => store.dispatch(fetchCommands()),
    dispatchFinishCommand: (payload) => store.dispatch(finishCommand(payload)),
    dispatchUpdateCommand: (payload) => store.dispatch(updateCommand(payload)),
    dispatchShowCommandProgress: (payload) => store.dispatch(showCommandProgress(payload)),
    dispatchFetchAuthor: noop,
    dispatchSetAppValue: noop
  });
  window.reconnectSearchFixture = () => {
    commandSignalR.onReconnecting();
    commandSignalR.onReconnected();
  };
  window.cancelSearchCommand = (id) => {
    store.dispatch(cancelCommand({ id }));
  };
  window.sendSearchCommand = (resource) => commandSignalR.handleCommand({ action: 'updated', resource });
  window.sendBookProgress = (body) => commandSignalR.handleBulkauthorbookprogress(body);
  window.readSidebarMessages = () => store.getState().app.messages.items;
  window.showSidebarMessage = (payload) => store.dispatch(showMessage(payload));
  window.removeSearchCommand = (id) => store.dispatch(removeCommand({ id }));
  window.unmountAuthorSearchFixture = () => ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  window.sendAuthorSearchSignalR = (body) => {
    const handler = new SignalR({
      authorSearch: store.getState().releases.authorSearch,
      dispatchAuthorSearchResults: (payload) => store.dispatch(receiveAuthorSearchResults(payload))
    });
    handler.handleMessage({ name: 'AuthorSearchResults', body });
  };
  window.toggleBypassFixture = (bypassFilters) => store.dispatch(setBypassFilters({ bypassFilters }));
  const props = {
    id: 10,
    selectedMediaType: 'audiobook',
    audiobookMonitored: true,
    ebookMonitored: true,
    audiobookQualityProfileId: 1,
    ebookQualityProfileId: 2,
    audiobookStatistics: { totalBookCount: audio, bookCount: 0 },
    ebookStatistics: { totalBookCount: ebook, bookCount: 0 },
    component: SearchAction,
    dispatchExecuteCommand: (command) => window.searchCommands.push(command),
    executeCommand: (command) => window.searchCommands.push(command),
    fetchRootFolders: noop,
    cancelFetchReleases: noop,
    clearBooks: noop,
    clearSeries: noop,
    clearBookFiles: noop,
    clearQueueDetails: noop,
    clearReleases: noop,
    hideUnmonitoredMissing: false,
    ...overrides
  };
  if (view === 'task-row') {
    ReactDOM.render(<Provider store={store}><table><tbody>
      <QueuedTaskRow id={88000} commandName="RssSync"
        trigger="scheduled"
        queued="2026-01-01T00:00:00Z" status={overrides.status}
        body={{ name: 'RssSync', supportsCancellation: overrides.supportsCancellation }}
      />
    </tbody></table></Provider>, document.getElementById('root'));
    return;
  }
  if (view === 'command-messages') {
    const history = createMemoryHistory();
    window.readMessageLocation = () => history.location;
    ReactDOM.render(
      <Provider store={store}>
        <Router history={history}><MessagesConnector /></Router>
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (view === 'filter-warning') {
    ReactDOM.render(
      <FilterWarning
        filterSummary={{ hasSoftFilters: true, displayedCount: 0, softFilteredCount: 2, summaryText: 'Showing 0 of 2 results (2 filtered)' }}
        bypassFilters={true}
        onToggleBypass={noop}
      />, document.getElementById('root')
    );
    return;
  }
  if (view === 'book-tab') {
    ReactDOM.render(
      <Provider store={store}>
        <InteractiveSearchTable
          type="book"
          bookId={10}
          selectedMediaType="audiobook"
        />
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (view === 'grouped-tabs') {
    ReactDOM.render(
      <Provider store={store}>
        <ApplyTheme>
          <GroupedSearchTabs
            authorId={props.id}
            mediaType={props.selectedMediaType}
            searchCount={props.selectedMediaType ? getAuthorSearchBookCount(props, props.selectedMediaType) : 0}
          />
        </ApplyTheme>
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (view === 'legacy-grab') {
    ReactDOM.render(
      <Provider store={store}>
        <LegacyGrabFixture onGrabPress={(payload) => store.dispatch(grabRelease(payload))} />
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (view === 'tab') {
    ReactDOM.render(
      <Provider store={store}>
        <InteractiveSearchTable
          key={`${props.id}:${props.selectedMediaType}`}
          type="author"
          authorId={props.id}
          selectedMediaType={props.selectedMediaType}
          searchCount={props.selectedMediaType ? getAuthorSearchBookCount(props, props.selectedMediaType) : 0}
        />
      </Provider>, document.getElementById('root')
    );
    return;
  }
  const Component = view === 'detail' ? Detail : Index;
  ReactDOM.render(<Provider store={store}><Component {...props} /></Provider>, document.getElementById('root'));
};

// Seed the existing search card and connected modal; submit through the actual store thunk.
window.renderAddAuthorFixture = async(mediaType, family = 'author', capturedResult = null, options = {}) => {
  await fetchTranslations();
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  localStorage.clear();
  const history = createMemoryHistory();
  const store = createAppStore(history);
  const qualityProfiles = [
    { id: 11, name: 'Audio Default', profileType: 1 }, { id: 12, name: 'None', profileType: 1 },
    { id: 13, name: 'Ebook Default', profileType: 2 }, { id: 14, name: 'None', profileType: 2 }
  ];
  const metadataProfiles = [
    { id: 21, name: 'Audio Metadata Default', profileType: 1 }, { id: 22, name: 'Audio Metadata Override', profileType: 1 },
    { id: 23, name: 'Ebook Metadata Default', profileType: 2 }, { id: 24, name: 'Ebook Metadata Override', profileType: 2 },
    { id: 25, name: 'None', profileType: 1 }
  ];
  const roots = [
    { id: 1, name: 'Audio', path: '/fixture/audio', folderType: 1, isEffectiveDefaultAudiobook: true,
      audiobookQualityProfileId: 11, audiobookMetadataProfileId: 21, audiobookMonitored: false,
      audiobookMonitorExistingMode: 'all', audiobookMonitorNewItems: 'new', audiobookTags: [41] },
    { id: 2, name: 'Ebook', path: '/fixture/ebook', folderType: 2, isEffectiveDefaultEbook: true,
      ebookQualityProfileId: 13, ebookMetadataProfileId: 23, ebookMonitored: true,
      ebookMonitorExistingMode: 'missing', ebookMonitorNewItems: 'none', ebookTags: [42] }
  ];
  roots.push({ ...roots[0], id: 3, name: 'Audio alternate', path: '/fixture/audio-alt', isEffectiveDefaultAudiobook: false });
  roots.push({ ...roots[1], id: 4, name: 'Ebook alternate', path: '/fixture/ebook-alt', isEffectiveDefaultEbook: false });
  if (family === 'series') {
    Object.assign(roots[2], { audiobookQualityProfileId: 12, audiobookMetadataProfileId: 22,
      audiobookMonitored: true, audiobookMonitorExistingMode: 'missing', audiobookMonitorNewItems: 'none', audiobookTags: [] });
    Object.assign(roots[3], { ebookQualityProfileId: 14, ebookMetadataProfileId: 24,
      ebookMonitored: false, ebookMonitorExistingMode: 'none', ebookMonitorNewItems: 'all', ebookTags: [43] });
  }
  roots.forEach((folder, index) => Object.assign(folder, options.roots?.[index]));
  if (options.existingAuthor) {
    store.dispatch(set({ section: 'authors', items: [options.existingAuthor], itemMap: { [options.existingAuthor.id]: 0 } }));
  }
  store.dispatch(set({ section: 'app', ...(options.selectedMediaType ? { selectedMediaType: options.selectedMediaType } : {}), dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: innerWidth <= 768 } }));
  store.dispatch(set({ section: 'settings.ui', item: { theme: options.theme || 'light', addNewDefaultMediaType: options.defaultMediaType || mediaType } }));
  store.dispatch(set({ section: 'settings.rootFolders', isPopulated: options.rootsPopulated !== false, items: roots }));
  store.dispatch(set({ section: 'settings.qualityProfiles', isPopulated: true, items: qualityProfiles }));
  store.dispatch(set({ section: 'settings.metadataProfiles', isPopulated: true, items: metadataProfiles }));
  store.dispatch(set({ section: 'tags', isPopulated: true, items: [{ id: 41, label: 'Root Audio' }, { id: 42, label: 'Root Ebook' }, { id: 43, label: 'Chosen Tag' }] }));
  store.dispatch(set({ section: 'system.status', item: { isWindows: false } }));
  if (options.authorDefaults) {
    store.dispatch(set({ section: 'search', authorDefaults: { ...store.getState().search.authorDefaults, ...options.authorDefaults } }));
  }
  window.openBulkMoveFixture = (shared) => {
    const author = { id: 10, authorName: 'Walter Isaacson', audiobookFolder: '/fixture/audio/Walter Isaacson',
      ebookFolder: shared ? '/fixture/audio/Walter Isaacson' : '/fixture/ebook/Walter Isaacson' };
    store.dispatch(set({ section: 'authors', items: [author], itemMap: { 10: 0 } }));
    ReactDOM.unmountComponentAtNode(document.getElementById('root'));
    ReactDOM.render(<Provider store={store}><Router history={history}>
      <AuthorEditorFooter authorIds={[10]} selectedCount={1}
        selectedMediaType="audiobook"
        isSaving={false} isDeleting={false}
        isOrganizingAuthor={false} isRetaggingAuthor={false}
        showMetadataProfile={false} onSaveSelected={() => {}}
        onOrganizeAuthorPress={() => {}}
        onRetagAuthorPress={() => {}}
      />
    </Router></Provider>, document.getElementById('root'));
  };
  window.openNewAuthorEntryFixture = () => {
    ReactDOM.unmountComponentAtNode(document.getElementById('root'));
    ReactDOM.render(
      <Provider store={store}><ApplyTheme>
        <AddNewAuthorModal
          isOpen={true}
          foreignAuthorId="gr:fixture-new-writer"
          authorName="New Fixture Writer"
          folder="New Fixture Writer"
          images={[]}
          onModalClose={() => ReactDOM.unmountComponentAtNode(document.getElementById('root'))}
        />
      </ApplyTheme></Provider>, document.getElementById('root')
    );
  };
  window.openEditAuthorEntryFixture = (author) => {
    ReactDOM.unmountComponentAtNode(document.getElementById('root'));
    store.dispatch(set({ section: 'authors', items: [author], itemMap: { [author.id]: 0 } }));
    class EditEntry extends React.Component {
      state = { isOpen: true };

      render() {
        return (
          <Provider store={store}><ApplyTheme>
            <EditAuthorModalConnector isOpen={this.state.isOpen} authorId={author.id}
              onDeleteAuthorPress={() => this.setState({ isOpen: false })}
              onModalClose={() => ReactDOM.unmountComponentAtNode(document.getElementById('root'))}
            />
          </ApplyTheme></Provider>
        );
      }
    }
    ReactDOM.render(<EditEntry />, document.getElementById('root'));
  };
  window.addAuthorFixture = { qualityProfiles, metadataProfiles, roots };
  window.readAddAuthorFixtureState = () => store.getState().search;
  window.readAuthorSyncState = () => store.getState().authors;
  window.readAuthorEntryState = () => ({ authors: store.getState().authors, books: store.getState().books, search: store.getState().search, selectedMediaType: store.getState().app.selectedMediaType });
  window.updateAuthorEntryDefault = (value) => store.dispatch(set({ section: 'settings.ui', item: { ...store.getState().settings.ui.item, addNewDefaultMediaType: value } }));
  window.toggleAuthorSyncGate = (side, monitored) => store.dispatch(toggleAuthorMonitored({ authorId: options.existingAuthor.id, mediaType: side, monitored }));
  window.updateAuthorSyncRoots = (changes) => {
    const items = store.getState().settings.rootFolders.items.map((root, index) => ({ ...root, ...changes[index] }));
    window.addAuthorFixture.roots = items;
    store.dispatch(set({ section: 'settings.rootFolders', items }));
  };
  if (family === 'author-entry') {
    ReactDOM.render(
      <Provider store={store}>
        <ConnectedRouter history={history}>
          <ApplyTheme>
            <AuthorDetailsConnector id={options.existingAuthor.id} />
          </ApplyTheme>
        </ConnectedRouter>
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (family === 'sync' || family === 'edit-sync') {
    const Modal = family === 'sync' ? AddNewAuthorModal : EditAuthorModal;
    ReactDOM.render(
      <Provider store={store}>
        <ApplyTheme>
          <Modal
            isOpen={true}
            authorId={options.existingAuthor?.id}
            foreignAuthorId="gr:fixture-selected-author"
            authorName="Selected Search Writer"
            folder="Selected Search Writer"
            images={[]}
            onModalClose={() => {
              window.authorSyncCloseCount = (window.authorSyncCloseCount || 0) + 1;
            }}
          />
        </ApplyTheme>
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (family === 'captured-author' || family === 'captured-book') {
    const Result = family === 'captured-author' ? AddNewAuthorSearchResultConnector : AddNewBookSearchResultConnector;
    ReactDOM.render(
      <Provider store={store}>
        <ApplyTheme>
          <Result {...capturedResult} />
        </ApplyTheme>
      </Provider>, document.getElementById('root')
    );
    return;
  }
  if (family === 'series') {
    window.reopenAddSeriesFixture = () => ReactDOM.render(
      <Provider store={store}>
        <ApplyTheme>
          <AddNewSeriesModal
            isOpen={true}
            foreignSeriesId="hc:8300"
            title="Selected Search Series"
            workCount={1}
            primaryWorkCount={1}
            onModalClose={() => ReactDOM.unmountComponentAtNode(document.getElementById('root'))}
          />
        </ApplyTheme>
      </Provider>, document.getElementById('root')
    );
    window.reopenAddSeriesFixture();
    return;
  }

  ReactDOM.render(
    <Provider store={store}>
      <ApplyTheme>
        <AddNewAuthorSearchResultConnector
          foreignAuthorId="gr:fixture-selected-author"
          titleSlug="selected-search-writer"
          authorName="Selected Search Writer"
          status="continuing"
          overview="Synthetic selected provider-search result"
          ratings={{ votes: 0, value: 0 }}
          folder="Selected Search Writer"
          images={[]}
          links={[]}
        />
      </ApplyTheme>
    </Provider>, document.getElementById('root')
  );
};

window.renderAddSeriesFixture = (mediaType) => window.renderAddAuthorFixture(mediaType, 'series');

window.getBookModalMetadataPayload = (mediaType, profileId, legacyFallback = false, throughAction = false) => {
  const book = {
    id: 88,
    localBookId: 88,
    foreignBookId: 'hc:8100',
    title: 'Selected Search Book',
    author: { foreignAuthorId: 'hc:8000', audiobookMetadataProfileId: 93, ebookMetadataProfileId: 94 },
    editions: [{ foreignEditionId: 'hc:edition:8200-audiobook', title: 'Selected Search Book', monitored: true, manualAdd: false }]
  };
  const payload = {
    foreignAuthorId: 'hc:8000',
    audiobookRootFolderPath: '/fixture/audio', ebookRootFolderPath: '/fixture/ebook',
    audiobookQualityProfileId: 11, ebookQualityProfileId: 13,
    audiobookMetadataProfileId: legacyFallback ? undefined : profileId,
    ebookMetadataProfileId: legacyFallback ? undefined : profileId,
    metadataProfileId: 73, monitor: 'specificBook',
    audiobookMonitored: false, ebookMonitored: false,
    audiobookMonitorNewItems: 'none', ebookMonitorNewItems: 'none',
    audiobookTags: [], ebookTags: [], searchForNewBook: true
  };
  if (throughAction) {
    const store = createAppStore(createMemoryHistory());
    store.dispatch(addBook({ ...payload, book, foreignBookId: book.foreignBookId, mediaType }));
    return;
  }
  return getNewBook(book, payload, mediaType);
};

window.renderSeriesMembershipFixture = async(mediaType, theme) => {
  await fetchTranslations();
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  const history = createMemoryHistory();
  const store = createAppStore(history);
  const positions = [
    { position: '2.2', positionOrder: 2.2 },
    { position: '序章', positionOrder: 7.922816251426434e28 },
    { position: '10', positionOrder: 10 },
    { position: '2.02', positionOrder: 2.02 },
    { position: '1', positionOrder: 1 },
    { position: '0.25', positionOrder: 0.25 }
  ];
  const books = ['audiobook', 'ebook'].flatMap((side, index) => {
    const start = 501 + index * 100;
    return [
      { id: start, title: 'Shared membership title' },
      ...positions.map(({ position }, offset) => ({ id: start + offset + 1, title: `Position ${position}` }))
    ].map((book) => ({ ...book, authorId: 77, mediaType: side, monitored: false,
      audiobookMonitored: false, ebookMonitored: false, isSaving: false, editions: [],
      ratings: { value: 0, votes: 0 }, images: [], titleSlug: `book-${book.id}` }));
  });
  const series = ['audiobook', 'ebook'].flatMap((side, index) => {
    const start = 501 + index * 100;
    return [
      { id: start, title: 'First membership group', links: [
        ...positions.map((position, offset) => ({ ...position, bookId: start + offset + 1 })),
        { bookId: start, position: '1-3', positionOrder: 1 }
      ] },
      { id: start + 1, title: 'Second membership group', links: [{ bookId: start, position: '6', positionOrder: 6 }] }
    ].map((group) => ({ ...group, mediaType: side }));
  });
  store.dispatch(set({ section: 'app', hideUnmonitoredMissing: false, selectedMediaType: mediaType,
    dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: innerWidth <= 768 } }));
  store.dispatch(set({ section: 'settings.ui', item: { ...store.getState().settings.ui.item, theme } }));
  store.dispatch(set({ section: 'authors', items: [{ id: 77, authorName: 'Series writer',
    audiobookRootFolderPath: '/audio', ebookRootFolderPath: '/ebooks' }], itemMap: { 77: 0 } }));
  store.dispatch(set({ section: 'authorDetails', filterValue: '' }));
  store.dispatch(set({ section: 'books', items: books, itemMap: Object.fromEntries(books.map((book, index) => [book.id, index])) }));
  store.dispatch(set({ section: 'series', items: series, sortKey: 'position', sortDirection: 'ascending',
    columns: store.getState().series.columns.filter(({ name }) => ['monitored', 'title', 'position'].includes(name)) }));
  window.readSeriesMembershipBooks = () => store.getState().books.items;
  ReactDOM.render(<Provider store={store}><Router history={history}><ApplyTheme>
    {series.filter((group) => group.mediaType === mediaType).map((group, index) => (
      <div key={group.id} data-series-fixture={index === 0 ? 'first' : 'second'}>
        <AuthorDetailsSeriesConnector seriesId={group.id} authorId={77}
          selectedMediaType={mediaType}
          isExpanded={true} onExpandPress={noop}
        />
      </div>
    ))}
  </ApplyTheme></Router></Provider>, root);
};
