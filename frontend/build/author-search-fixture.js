import PropTypes from 'prop-types';
import React, { useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider } from 'react-redux';
import { Tab, TabList, TabPanel, Tabs } from 'react-tabs';
import { applyMiddleware, createStore } from 'redux';
import thunk from 'redux-thunk';
import ApplyTheme from 'App/ApplyTheme';
import AuthorDetailsConnector from 'Author/Details/AuthorDetailsConnector';
import AuthorIndexItemConnector from 'Author/Index/AuthorIndexItemConnector';
import SignalRConnector from 'Components/SignalRConnector';
import FilterWarning from 'InteractiveSearch/FilterWarning';
import InteractiveSearchRow from 'InteractiveSearch/InteractiveSearchRow';
import InteractiveSearchTable from 'InteractiveSearch/InteractiveSearchTable';
import { cancelAuthorReleases, defaultState as releaseState, grabRelease, reducers as releaseReducer, setAuthorSearchResults, setBypassFilters } from 'Store/Actions/releaseActions';
import { getAuthorSearchBookCount } from 'Utilities/Author/getAuthorStatisticsForMediaType';
import { fetchTranslations } from 'Utilities/String/translate';

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
    app: { dimensions: { width: innerWidth, height: innerHeight, isSmallScreen: false } },
    releases: releaseState,
    customFilters: { items: [] },
    settings: { ui: { item: { theme: 'light', longDateFormat: 'MMMM D, YYYY', shortDateFormat: 'MMM D, YYYY', timeFormat: 'h:mm A' } } }
  };
  const store = preserveInstance && fixtureStore ? fixtureStore : createStore((state = initialState, action) => ({
    ...state,
    releases: releaseReducer(state.releases, action)
  }), applyMiddleware(thunk));
  fixtureStore = store;
  window.readReleaseResults = () => ({ items: store.getState().releases.items, hiddenItems: store.getState().releases.hiddenItems });
  window.readAuthorSearchState = () => store.getState().releases.authorSearch;
  window.cancelAuthorSearchRequest = (searchId) => store.dispatch(cancelAuthorReleases({ searchId }));
  window.sendAuthorSearchSignalR = (body) => {
    const handler = new SignalR({
      authorSearch: store.getState().releases.authorSearch,
      dispatchAuthorSearchResults: (payload) => store.dispatch(setAuthorSearchResults(payload))
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
