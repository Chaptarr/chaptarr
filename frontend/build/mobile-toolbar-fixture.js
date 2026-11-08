import { createMemoryHistory } from 'history';
import React, { useState } from 'react';
import ReactDOM from 'react-dom';
import { Provider, useDispatch, useSelector } from 'react-redux';
import { MemoryRouter } from 'react-router-dom';
import { createStore } from 'redux';
import headerStyles from 'Author/Details/AuthorDetails.css';
import bookTableStyles from 'Author/Details/AuthorDetailsBookTable.css';
import AuthorDetailsHeader from 'Author/Details/AuthorDetailsHeader';
import { AuthorInitialScanStatus } from 'Author/Details/AuthorInitialScanStatus';
import BookRow from 'Author/Details/BookRow';
import MediaTypeTripleToggle from 'Author/Index/MediaTypeTripleToggle';
import BookIndex from 'Book/Index/BookIndex';
import bookIndexStyles from 'Book/Index/BookIndex.css';
import bookFooterStyles from 'Book/Index/BookIndexFooter.css';
import BookIndexFooterConnector from 'Book/Index/BookIndexFooterConnector';
import getBookIndexQuery from 'Book/Index/getBookIndexQuery';
import BookIndexFilterMenu from 'Book/Index/Menus/BookIndexFilterMenu';
import BookIndexProgressBar from 'Book/Index/ProgressBar/BookIndexProgressBar';
import CalendarPage from 'Calendar/CalendarPage';
import { createMissingBookIdsSelector } from 'Calendar/CalendarPageConnector';
import NarratorCell from 'Components/Book/NarratorCell';
import CheckInput from 'Components/Form/CheckInput';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import IconButton from 'Components/Link/IconButton';
import Menu from 'Components/Menu/Menu';
import MenuContent from 'Components/Menu/MenuContent';
import MenuItem from 'Components/Menu/MenuItem';
import ToolbarMenuButton from 'Components/Menu/ToolbarMenuButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import SwipeHeader from 'Components/Swipe/SwipeHeader';
import Table from 'Components/Table/Table';
import TableOptionsModalWrapper from 'Components/Table/TableOptions/TableOptionsModalWrapper';
import { icons } from 'Helpers/Props';
import { setSelectedMediaType } from 'Store/Actions/appActions';
import { filterPredicates as bookFilterPredicates, filters as bookFilters, sortPredicates as bookSortPredicates } from 'Store/Actions/bookActions';
import { setBookFilter } from 'Store/Actions/bookIndexActions';
import { buildQueryParams } from 'Store/Actions/bookInfiniteScrollActions';
import createAppStore from 'Store/createAppStore';
import createBookClientSideCollectionItemsSelector from 'Store/Selectors/createBookClientSideCollectionItemsSelector';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import { isBookSearchEligible, SEARCH_CONFIRMATION_THRESHOLD, shouldConfirmSearch } from 'Utilities/Author/getAuthorStatisticsForMediaType';
import getNarratorNamesForDisplay from 'Utilities/Book/getNarratorNamesForDisplay';
import isLibraryBook from 'Utilities/Book/isLibraryBook';
import { fetchTranslations } from 'Utilities/String/translate';
import CutoffUnmet from 'Wanted/CutoffUnmet/CutoffUnmet';
import Missing from 'Wanted/Missing/Missing';

import 'Styles/scaffolding.css';

const h = React.createElement;
const emptyIndexState = () => ({
  filterBuilderProps: [],
  filters: bookFilters,
  filterPredicates: bookFilterPredicates
});
const viewportState = () => ({
  settings: { qualityProfiles: { items: [] } },
  app: { dimensions: { width: innerWidth, isSmallScreen: innerWidth <= 768 } },
  books: { items: [] },
  bookIndex: emptyIndexState(),
  customFilters: { items: [] }
});
const store = createStore(viewportState);
let persistedLibraryStore = null;
window.addEventListener('resize', () => store.dispatch({ type: 'resize' }));
const columns = [{ name: 'title', label: 'Title', isVisible: true }];

const bookProgressCases = [
  { key: 'available', monitored: false, bookFileCount: 1, releaseDate: '2000-01-01', count: '1 / 1' },
  { key: 'missing', monitored: true, bookFileCount: 0, releaseDate: '2000-01-01', count: '0 / 1' },
  { key: 'future', monitored: true, bookFileCount: 0, releaseDate: '2099-01-01', count: '0 / 1' },
  { key: 'unknown', monitored: true, bookFileCount: 0, releaseDate: null, count: '0 / 1' },
  { key: 'unmonitored', monitored: false, bookFileCount: 0, releaseDate: '2000-01-01', count: '0 / 1' }
];

const bookRowDateCases = [
  { key: 'unknown', releaseDate: null, label: 'Not Available' },
  { key: 'past', releaseDate: '2000-01-01', label: 'Missing' },
  { key: 'future', releaseDate: '2099-01-01', label: 'Not Available' },
  { key: 'file', releaseDate: null, label: 'MP3', monitored: false, bookFiles: [{
    quality: { quality: { name: 'MP3' }, revision: { real: 0, version: 1, isRepack: false } },
    size: 1024,
    qualityCutoffNotMet: false
  }] },
  { key: 'absent' },
  { key: 'unmonitored', releaseDate: null, monitored: false },
  { key: 'grabbed', releaseDate: null, monitored: false, grabbed: true },
  { key: 'queue', releaseDate: null, monitored: false, grabbed: true, queueItem: {
    title: 'Queued book', size: 100, sizeleft: 50, status: 'downloading'
  } }
];

function getLibraryRows() {
  return [
    {
      id: 1,
      title: 'Monitored row',
      mediaType: 'audiobook',
      authorId: 1,
      audiobookMonitored: true,
      ebookMonitored: false,
      monitored: true,
      status: 'continuing',
      statistics: { bookFileCount: 0, sizeOnDisk: 0 }
    },
    {
      id: 2,
      title: 'File-only row',
      mediaType: 'audiobook',
      authorId: 2,
      audiobookMonitored: false,
      ebookMonitored: false,
      monitored: false,
      status: 'ended',
      statistics: { bookFileCount: 1, sizeOnDisk: 250 }
    },
    {
      id: 3,
      title: 'Outside library',
      mediaType: 'audiobook',
      authorId: 3,
      audiobookMonitored: false,
      ebookMonitored: false,
      monitored: false,
      status: 'ended',
      statistics: { bookFileCount: 0, sizeOnDisk: 0 }
    },
    {
      id: 4,
      title: 'Other monitor flag row',
      mediaType: 'audiobook',
      authorId: 4,
      audiobookMonitored: false,
      ebookMonitored: true,
      monitored: true,
      status: 'continuing',
      statistics: { bookFileCount: 0, sizeOnDisk: 0 }
    },
    {
      id: 5,
      title: 'Monitored ebook row',
      mediaType: 'ebook',
      authorId: 5,
      audiobookMonitored: false,
      ebookMonitored: true,
      monitored: true,
      status: 'continuing',
      statistics: { bookFileCount: 0, sizeOnDisk: 0 }
    },
    {
      id: 6,
      title: 'Other audiobook flag row',
      mediaType: 'ebook',
      authorId: 6,
      audiobookMonitored: true,
      ebookMonitored: false,
      monitored: false,
      status: 'continuing',
      statistics: { bookFileCount: 0, sizeOnDisk: 0 }
    }
  ];
}

function makeLibraryState(view = 'table', mediaType = 'audiobook') {
  const rows = getLibraryRows();
  const bookIndex = {
    ...emptyIndexState(),
    view,
    selectedFilterKey: 'library',
    sortKey: 'id',
    sortDirection: 'ascending',
    sortPredicates: {},
    secondarySortKey: null,
    secondarySortDirection: null
  };
  const state = {
    app: { selectedMediaType: mediaType },
    books: { items: rows },
    authors: { items: [], itemMap: {} },
    bookIndex,
    customFilters: { items: [] },
    bookInfiniteScroll: { queries: {} }
  };

  if (view === 'posters') {
    bookIndex.sortKey = 'title';
    const { queryKey } = getBookIndexQuery(bookIndex, mediaType);
    state.books.items = [];
    state.bookInfiniteScroll.queries[queryKey] = {
      footerStatistics: {
        totalBooks: 2,
        monitoredBooks: 1,
        fileCount: 1,
        totalFileSize: 250,
        authorCount: 2
      },
      totalCount: 2,
      buckets: { status: 'succeeded' },
      pages: {}
    };
  }

  return state;
}

function PersistedLibraryMenu() {
  const dispatch = useDispatch();
  const selectedFilterKey = useSelector((state) => state.bookIndex.selectedFilterKey);
  const selectedMediaType = useSelector((state) => state.app.selectedMediaType);

  return h('div', null,
    h('div', { id: 'persisted-filter-key' }, selectedFilterKey),
    h('div', { id: 'persisted-media-type' }, selectedMediaType),
    h(BookIndexFilterMenu, {
      selectedFilterKey,
      filters: bookFilters,
      customFilters: [],
      isDisabled: false,
      onFilterSelect: (key) => dispatch(setBookFilter({ selectedFilterKey: key }))
    }),
    h('button', {
      type: 'button',
      onClick: () => dispatch(setSelectedMediaType({
        mediaType: selectedMediaType === 'audiobook' ? 'ebook' : 'audiobook'
      }))
    }, 'Switch media type')
  );
}

function Fixture() {
  const [format, setFormat] = useState('all');
  const [selectedFilterKey, setSelectedFilterKey] = useState('all');
  const action = (label) => h(PageToolbarButton, { key: label, label, iconName: icons.SEARCH, onPress: () => {} });
  const menu = (label) => h(Menu, { key: label, alignMenu: 'right' },
    h(ToolbarMenuButton, { iconName: icons.FILTER, text: label }),
    h(MenuContent, null, h(MenuItem, { onPress: () => {} }, `${label} choice`)));
  const filterMenu = h(BookIndexFilterMenu, {
    selectedFilterKey,
    filters: bookFilters,
    customFilters: [],
    isDisabled: false,
    onFilterSelect: (key) => {
      window.libraryFilterSelection = key;
      setSelectedFilterKey(key);
    }
  });

  // Match the index's non-collapsing right section, not just its total button count.
  return h(PageToolbar, { controls: h(MediaTypeTripleToggle, { selectedMediaType: format, onMediaTypeChange: setFormat }) },
    h(PageToolbarSection, null, action('Update All'), action('RSS Sync'), h(PageToolbarSeparator), action('Author Editor')),
    h(PageToolbarSection, { alignContent: 'right', collapseButtons: false },
      h(TableOptionsModalWrapper, { columns, onTableOptionChange: () => {} }, action('Options')),
      h(PageToolbarSeparator), menu('View'), menu('Sort'), filterMenu));
}

window.getLibraryFilterAssertions = () => {
  const rows = getLibraryRows();
  const state = makeLibraryState('table');
  const footerItems = createClientSideCollectionSelector('books', 'bookIndex')(state).items;
  const bookIndexItems = createBookClientSideCollectionItemsSelector('bookIndex')(state).items;
  const audiobookQuery = getBookIndexQuery({
    sortKey: 'title',
    sortDirection: 'ascending',
    selectedFilterKey: 'library'
  }, 'audiobook');
  const ebookQuery = getBookIndexQuery({
    sortKey: 'title',
    sortDirection: 'ascending',
    selectedFilterKey: 'library'
  }, 'ebook');

  return {
    menuKeys: bookFilters.slice(0, 2).map((filter) => filter.key),
    footerIds: footerItems.map((item) => item.id),
    bookIndexIds: bookIndexItems.map((item) => item.id),
    audiobookQuery: {
      key: audiobookQuery.queryKey,
      clientSide: audiobookQuery.useClientSidePosters,
      params: buildQueryParams(audiobookQuery.queryParams)
    },
    ebookQuery: {
      key: ebookQuery.queryKey,
      clientSide: ebookQuery.useClientSidePosters,
      params: buildQueryParams(ebookQuery.queryParams)
    },
    libraryIds: {
      unscoped: rows.filter((row) => isLibraryBook(row)).map((row) => row.id),
      audiobook: rows.filter((row) => row.mediaType === 'audiobook' && isLibraryBook(row)).map((row) => row.id),
      ebook: rows.filter((row) => row.mediaType === 'ebook' && isLibraryBook(row)).map((row) => row.id)
    },
    rowCount: rows.length
  };
};

window.renderToolbarFixture = async() => {
  await fetchTranslations();
  window.libraryFilterSelection = null;
  ReactDOM.render(h(Provider, { store }, h(Fixture)), document.getElementById('root'));
};

window.renderLibraryFooterFixture = (mode) => {
  const state = makeLibraryState(mode === 'infinite' ? 'posters' : 'table', mode === 'infinite' ? 'ebook' : 'audiobook');
  const footerStore = createStore(() => state);
  ReactDOM.render(h(Provider, { store: footerStore },
    h('div', { id: 'library-footer-content', className: bookIndexStyles.postersInnerContentBody },
      h('div', { style: { height: 500 } }),
      h(BookIndexFooterConnector, { isSticky: true })
    )), document.getElementById('root'));
};

window.getBookFooterLayout = () => {
  const footer = document.querySelector(`.${bookFooterStyles.footer}`);
  const legend = footer.querySelector(`.${bookFooterStyles.legend}`);
  const statistics = footer.querySelector(`.${bookFooterStyles.statistics}`);
  const footerRect = footer.getBoundingClientRect();
  const legendRect = legend.getBoundingClientRect();
  const statisticsRect = statistics.getBoundingClientRect();
  const content = document.getElementById('library-footer-content');
  const colors = {};

  for (const [name, variable] of Object.entries({
    available: 'successColor',
    missing: 'dangerColor',
    future: 'primaryColor',
    unmonitored: 'warningColor'
  })) {
    const probe = document.createElement('div');
    probe.style.backgroundColor = `var(--${variable})`;
    document.body.appendChild(probe);
    colors[name] = getComputedStyle(probe).backgroundColor;
    probe.remove();
  }

  return {
    position: getComputedStyle(footer).position,
    isSticky: footer.classList.contains(bookFooterStyles.sticky),
    footer: { left: footerRect.left, right: footerRect.right, top: footerRect.top, bottom: footerRect.bottom, height: footerRect.height },
    pinnedHeight: parseFloat(document.documentElement.style.getPropertyValue('--bookIndexFooterPinnedHeight')),
    contentPaddingBottom: parseFloat(getComputedStyle(content).paddingBottom),
    documentWidth: document.documentElement.scrollWidth,
    viewportWidth: innerWidth,
    viewportHeight: innerHeight,
    legendWithinFooter: legendRect.left >= footerRect.left && legendRect.right <= footerRect.right && legendRect.top >= footerRect.top && legendRect.bottom <= footerRect.bottom,
    statisticsWithinFooter: statisticsRect.left >= footerRect.left && statisticsRect.right <= footerRect.right && statisticsRect.top >= footerRect.top && statisticsRect.bottom <= footerRect.bottom,
    legendDoesNotOverlapStatistics: legendRect.right <= statisticsRect.left || statisticsRect.right <= legendRect.left || legendRect.bottom <= statisticsRect.top || statisticsRect.bottom <= legendRect.top,
    legendItems: Array.from(legend.children).map((item) => ({
      text: item.lastElementChild.textContent.trim(),
      color: getComputedStyle(item.firstElementChild).backgroundColor,
      withinLegend: (() => {
        const itemRect = item.getBoundingClientRect();
        return itemRect.left >= legendRect.left && itemRect.right <= legendRect.right && itemRect.top >= legendRect.top && itemRect.bottom <= legendRect.bottom;
      })()
    })),
    colors
  };
};

window.isBookFooterPinnedHeightCurrent = () => {
  const footer = document.querySelector(`.${bookFooterStyles.footer}`);
  return document.documentElement.style.getPropertyValue('--bookIndexFooterPinnedHeight') === `${Math.ceil(footer.getBoundingClientRect().height)}px`;
};

window.renderBookProgressFixture = async() => {
  await fetchTranslations();
  ReactDOM.render(h('div', null, bookProgressCases.map((item) => h('div', {
    key: item.key,
    'data-book-status': item.key
  }, h(BookIndexProgressBar, {
    monitored: item.monitored,
    bookFileCount: item.bookFileCount,
    releaseDate: item.releaseDate,
    posterWidth: 180,
    detailedProgressBar: true
  })))), document.getElementById('root'));
};

window.getBookProgressAssertions = () => bookProgressCases.map((item) => {
  const row = document.querySelector(`[data-book-status=${item.key}]`);
  const progress = row.querySelector('[role=meter]');
  const container = progress.parentElement;

  return {
    key: item.key,
    count: container.getAttribute('title'),
    color: getComputedStyle(progress).backgroundColor,
    ariaValue: progress.getAttribute('aria-valuenow')
  };
});

window.renderBookRowDateFixture = () => {
  const statusColumns = [{ name: 'status', isVisible: true }];
  ReactDOM.render(h('div', null, bookRowDateCases.map((item) => h('table', {
    key: item.key,
    'data-book-row-status': item.key
  }, h('tbody', null, h(BookRow, {
    id: 1,
    authorId: 1,
    title: `Book ${item.key}`,
    authorName: 'Example Author',
    releaseDate: item.releaseDate,
    selectedMediaType: 'audiobook',
    audiobookMonitored: item.monitored !== false,
    ebookMonitored: false,
    ratings: { value: 0, votes: 0 },
    bookFiles: item.bookFiles || [],
    grabbed: item.grabbed,
    queueItem: item.queueItem,
    columns: statusColumns,
    onMonitorBookPress: () => {}
  }))))), document.getElementById('root'));
};

window.getBookRowDateAssertions = () => bookRowDateCases.map((item) => {
  const row = document.querySelector(`[data-book-row-status=${item.key}]`);
  const cell = row.querySelector('td');
  const label = cell.querySelector('[title]');

  return {
    key: item.key,
    text: cell.textContent.trim(),
    title: label ? label.getAttribute('title') : null,
    progress: cell.querySelector('[role=meter]')?.getAttribute('aria-valuenow')
  };
});

window.getBookStatusSortAssertions = () => {
  const makeItem = (releaseDate, monitored, bookFileCount) => ({
    releaseDate,
    mediaType: 'audiobook',
    audiobookMonitored: monitored,
    ebookMonitored: false,
    statistics: { bookFileCount }
  });
  const status = (item) => bookSortPredicates.status(item, 'ascending', 'audiobook');

  return {
    unknownMonitored: status(makeItem(null, true, 0)),
    pastMonitored: status(makeItem('2000-01-01', true, 0)),
    futureMonitored: status(makeItem('2099-01-01', true, 0)),
    futureFileUnmonitored: status(makeItem('2099-01-01', false, 1))
  };
};

window.renderPersistedLibraryMenu = async(reset) => {
  await fetchTranslations();
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);

  if (reset) {
    localStorage.removeItem('chaptarr');
    localStorage.removeItem('selectedMediaType');
  }

  persistedLibraryStore = createAppStore(createMemoryHistory());
  ReactDOM.render(h(Provider, { store: persistedLibraryStore }, h(PersistedLibraryMenu)), root);
};

window.remountPersistedLibraryMenu = () => {
  const root = document.getElementById('root');
  ReactDOM.unmountComponentAtNode(root);
  ReactDOM.render(h(Provider, { store: persistedLibraryStore }, h(PersistedLibraryMenu)), root);
};

window.getPersistedLibraryQuery = () => {
  const state = persistedLibraryStore.getState();
  const query = getBookIndexQuery(state.bookIndex, state.app.selectedMediaType);
  return { key: query.queryKey, params: buildQueryParams(query.queryParams) };
};

window.renderHeaderFixture = async() => {
  await fetchTranslations();
  const header = (width) => h(AuthorDetailsHeader, {
    id: 1, authorName: 'A Long Example Author Name That Must Leave Room for Navigation', width, ratings: { value: 0 },
    statistics: {}, monitored: true, status: 'continuing', overview: '',
    links: [], images: [], alternateTitles: ['An alternate provider spelling'], tags: [], isSaving: false,
    isSmallScreen: innerWidth <= 768, onMonitorTogglePress: () => {}
  });
  ReactDOM.render(h(Provider, { store }, h(SwipeHeader, {
    className: headerStyles.header, currentComponent: header,
    nextComponent: header, prevComponent: header, nextLink: '/', prevLink: '/',
    isSmallScreen: innerWidth <= 768, onGoTo: () => {}
  }, h('div', { className: headerStyles.authorNavigationButtons },
    h(IconButton, { className: headerStyles.authorNavigationButton, name: icons.ARROW_LEFT, size: 30, title: 'Previous author', onPress: () => {} }),
    h(IconButton, { className: headerStyles.authorUpButton, name: icons.ARROW_UP, size: 30, title: 'Author list', onPress: () => {
      window.authorNavigationPressed = true;
    } }),
    h(IconButton, { className: headerStyles.authorNavigationButton, name: icons.ARROW_RIGHT, size: 30, title: 'Next author', onPress: () => {} })
  ))), document.getElementById('root'));
};

// Exercise the production cells, table scroller and shared dialog controls.
window.renderBookColumnFixture = () => {
  const tableColumns = ['title', 'narrator', 'position', 'rating'].map((name) => ({
    name, label: name, isVisible: true, className: bookTableStyles[name]
  }));
  ReactDOM.render(h(MemoryRouter, null, h(Table, { columns: tableColumns, className: bookTableStyles.table },
    h('tbody', null, h(BookRow, {
      id: 1, authorId: 1, authorName: 'Example Author', title: 'Dune Messiah',
      narrator: 'A very long narrator name '.repeat(25), hasPinnedEdition: true,
      manualAdd: true, anyEditionOk: false, selectedMediaType: 'audiobook',
      ratings: { value: 4.2, votes: 100 }, bookFiles: [], position: '2',
      columns: tableColumns, onMonitorBookPress: () => {}
    })))), document.getElementById('root'));
};

function DialogToggleFixture() {
  const [mediaType, setMediaType] = React.useState('audiobook');
  const [checked, setChecked] = React.useState(false);
  return h(Modal, { isOpen: true, onModalClose: () => {} },
    h(ModalContent, { onModalClose: () => {} }, h(ModalBody, null,
      h(MediaTypeToggle, { selectedMediaType: mediaType, includeBoth: true, onMediaTypeChange: setMediaType }),
      h(CheckInput, { name: 'fixtureMonitor', value: checked, onChange: ({ value }) => setChecked(value), helpText: 'Monitor this book' })
    )));
}

window.renderDialogToggleFixture = () => {
  ReactDOM.render(h(DialogToggleFixture), document.getElementById('root'));
};

window.renderCastLabelFixture = () => {
  const cases = [
    [{ name: 'A Full Cast', isCastLabel: true }, { name: 'Inés del Castillo', isCastLabel: false }],
    [{ name: 'Varios narradores', isCastLabel: true }],
    [{ name: 'Full Cast', isCastLabel: false }], // The server's flag, not an English browser rule, decides.
    [{ name: 'Diverse', isCastLabel: true }, { name: 'Dietmar Wunder', isCastLabel: false }, { name: 'Martin May', isCastLabel: false }]
  ];
  window.narratorDiscoveryNames = cases.map((credits) => getNarratorNamesForDisplay(credits));
  ReactDOM.render(h('div', null, cases.map((narrators, index) => h('div', { key: index, 'data-cast-case': index },
    h(NarratorCell, { bookId: 1, authorName: 'Test Author', bookTitle: 'Test Book', narrators, hasFiles: true })))),
  document.getElementById('root'));
};

window.renderInitialScanFixture = async(state = 'waiting') => {
  await fetchTranslations();
  window.initialScanCommand = null;
  ReactDOM.render(h(AuthorInitialScanStatus, {
    authorId: 42, paths: state === 'complete' ? [] : ['/ebooks/Frank Herbert'],
    isScanning: state === 'scanning', executeCommand: (command) => {
      window.initialScanCommand = command;
    }
  }), document.getElementById('root'));
};

// Render the toolbar returned by each actual page; do not duplicate its control layout.
window.renderPageToolbarFixture = async(pageName) => {
  await fetchTranslations();
  window.toolbarCalls = [];
  const record = (name) => (...args) => window.toolbarCalls.push({ name, args });
  const pageProps = {
    items: [{ id: 1 }], totalItems: 1, totalRecords: 1,
    columns, filters: bookFilters, customFilters: [],
    view: 'table', selectedMediaType: 'audiobook', selectedFilterKey: 'all',
    sortKey: 'title', sortDirection: 'ascending',
    onFilterSelect: record('filter'), onSortSelect: record('sort'), onViewSelect: record('view'),
    onRssSyncPress: record('rss'), onSearchSelectedPress: record('search'),
    onMediaTypeChange: record('media'), setMissingMediaType: record('media'),
    onTableOptionChange: record('options')
  };
  const component = pageName === 'books' ? new BookIndex(pageProps) : new Missing(pageProps);
  const toolbar = React.Children.toArray(component.render().props.children).find((child) => child.type === PageToolbar);
  ReactDOM.render(h(Provider, { store }, toolbar), document.getElementById('root'));
};

// Exercise each page's real action and modal, isolated from unrelated table rendering.
window.renderSearchWarningFixture = async(pageName, count, selected = true, mediaType = 'audiobook', monitoredView = true) => {
  await fetchTranslations();
  window.searchWarningCalls = [];
  window.searchWarningRequests = [];
  const record = (ids, monitoredOnly) => {
    window.searchWarningCalls.push(ids);
    window.searchWarningRequests.push({ ids, monitoredOnly });
  };
  const ids = Array.from({ length: count }, (_, index) => index + 1);
  const author = { id: 1, audiobookMonitored: true, ebookMonitored: true, audiobookQualityProfileId: 1, ebookQualityProfileId: 1 };
  const books = Array.from({ length: 20 }, (_, index) => ({
    id: index + 1, authorId: index >= count && index % 2 === 0 ? 2 : 1, mediaType,
    audiobookMonitored: index < count, ebookMonitored: index < count,
    releaseDate: '2020-06-01', statistics: { percentOfBooks: 0 }
  }));
  const paused = { ...author, id: 2, audiobookMonitored: false, ebookMonitored: false };
  for (const book of books) {
    if (book.authorId === 2) {
      book[`${mediaType}Monitored`] = true;
    }
    book.author = book.authorId === 2 ? paused : author;
    book.isSearchEligible = isBookSearchEligible(book);
  }
  const missingBookIds = createMissingBookIdsSelector()({
    calendar: { start: '2020-01-01', end: '2021-01-01', items: books }, queue: { details: { items: [] } },
    authors: { items: [author, paused], itemMap: { 1: 0, 2: 1 } }
  });
  const props = {
    items: books,
    totalItems: 20, totalRecords: monitoredView ? count : 1, columns, customFilters: [],
    filters: [{ key: 'test', filters: [{ key: 'monitored', value: monitoredView }] }],
    view: 'table', selectedMediaType: mediaType, selectedFilterKey: 'test',
    sortKey: 'title', sortDirection: 'ascending',
    isPopulated: true, isAuthorPopulated: true, hasAuthor: false,
    missingBookIds, calendarItems: [],
    onSearchPress: record, onSearchSelectedPress: record, onSearchMissingPress: record,
    onSearchAllMissingPress: () => record(ids),
    onSearchAllCutoffUnmetPress: () => record(ids),
    onSearchCountError: (error) => {
      window.searchCountError = error.status;
    }
  };
  const Page = { books: BookIndex, missing: Missing, cutoff: CutoffUnmet, calendar: CalendarPage }[pageName];
  const component = new Page(props);
  component.state.selectedState = Object.fromEntries(ids.map((id) => [id, selected]));
  component.state.isEditorActive = selected;
  const action = {
    books: component.onSearchPress,
    calendar: component.onSearchMissingPress,
    missing: selected ? component.onSearchSelectedPress : component.onSearchAllMissingPress,
    cutoff: selected ? component.onSearchSelectedPress : component.onSearchAllCutoffUnmetPress
  }[pageName];
  function dialogs(element) {
    if (!React.isValidElement(element)) {
      return [];
    }
    if (element.type === ConfirmModal) {
      return [element];
    }
    return React.Children.toArray(element.props.children).flatMap(dialogs);
  }
  function render() {
    ReactDOM.render(h(Provider, { store }, h(React.Fragment, null,
      h('button', { onClick: action }, 'Run search action'),
      ...dialogs(component.render()).map((element, index) => React.cloneElement(element, { key: index }))
    )), document.getElementById('root'));
  }
  component.setState = (update) => {
    component.state = { ...component.state, ...(typeof update === 'function' ? update(component.state, props) : update) };
    render();
  };
  ReactDOM.unmountComponentAtNode(document.getElementById('root'));
  render();
};

window.searchConfirmationRules = () => ({
  threshold: SEARCH_CONFIRMATION_THRESHOLD,
  five: shouldConfirmSearch(5), six: shouldConfirmSearch(6),
  unsupportedMedia: isBookSearchEligible({ mediaType: 'other', otherMonitored: true }, { otherMonitored: true, otherQualityProfileId: 1 })
});

window.getLibraryFilterLabel = () => bookFilters.find((filter) => filter.key === 'library').label();
