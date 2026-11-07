const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const propType = () => propType;
propType.isRequired = propType;
propType.arrayOf = () => propType;
propType.oneOf = () => propType;
propType.oneOfType = () => propType;

const PropTypes = new Proxy({}, {
  get() {
    return propType;
  }
});

const styles = new Proxy({}, {
  get(_target, property) {
    return property.toString();
  }
});

function Component(props) {
  this.props = props;
  this.state = {};
}

Component.prototype.setState = function(update) {
  const nextState = typeof update === 'function' ? update(this.state, this.props) : update;
  this.state = { ...this.state, ...nextState };
};

function findElements(element, predicate, matches = []) {
  if (Array.isArray(element)) {
    element.forEach((child) => findElements(child, predicate, matches));
    return matches;
  }

  if (!element || typeof element !== 'object') {
    return matches;
  }

  if (predicate(element)) {
    matches.push(element);
  }

  Object.values(element.props || {}).forEach((value) => findElements(value, predicate, matches));
  return matches;
}

function loadComponent() {
  const filePath = path.join(__dirname, 'BookIndex.js');
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [
      [
        '@babel/preset-env',
        {
          targets: { browsers: ['last 2 versions'] },
          loose: true,
          modules: 'commonjs'
        }
      ],
      '@babel/preset-react'
    ]
  });

  const React = {
    Component,
    createElement(type, props, ...children) {
      return {
        type,
        props: {
          ...props,
          children: children.length === 1 ? children[0] : children
        }
      };
    }
  };
  const icons = new Proxy({}, {
    get(_target, property) {
      return property.toString();
    }
  });
  const kinds = new Proxy({}, {
    get(_target, property) {
      return property.toString();
    }
  });
  const mockDefault = (value) => ({ __esModule: true, default: value });
  const mocks = new Map([
    ['lodash/reduce', mockDefault((collection, iteratee, initialValue) => collection.reduce(iteratee, initialValue))],
    ['prop-types', mockDefault(PropTypes)],
    ['react', { __esModule: true, default: React, Component }],
    ['Helpers/Props', { align: { RIGHT: 'right' }, icons, kinds, sortDirections: { all: [] } }],
    ['Utilities/Object/getErrorMessage', mockDefault((error, fallback) => error?.responseJSON?.message || fallback)],
    ['Utilities/Object/hasDifferentItemsOrOrder', mockDefault(() => false)],
    ['Utilities/String/translate', mockDefault((key) => key)],
    ['Utilities/Table/getSelectedIds', mockDefault((state) => Object.keys(state).filter((id) => state[id]).map(Number))],
    ['Utilities/Table/selectAll', mockDefault((state, selected) => ({
      allSelected: selected,
      allUnselected: !selected,
      lastToggled: null,
      selectedState: Object.fromEntries(Object.keys(state).map((id) => [id, selected]))
    }))],
    ['Utilities/Table/toggleSelected', mockDefault((state) => state)],
    ['./BookIndex.css', mockDefault(styles)]
  ]);
  const sandbox = {
    exports: {},
    require(moduleName) {
      if (mocks.has(moduleName)) {
        return mocks.get(moduleName);
      }

      return mockDefault(moduleName);
    }
  };

  vm.runInNewContext(code, sandbox, { filename: filePath });
  return sandbox.exports.default;
}

function makeProps(overrides = {}) {
  return {
    isFetching: false,
    isPopulated: true,
    error: null,
    totalItems: 3,
    items: [{ id: 1 }, { id: 2 }, { id: 3 }],
    columns: [],
    selectedFilterKey: 'all',
    filters: [],
    customFilters: [],
    sortKey: 'cleanTitle',
    sortDirection: 'ascending',
    view: 'table',
    isRefreshingBook: false,
    isSearching: false,
    isRssSyncExecuting: false,
    isSmallScreen: false,
    isSaving: false,
    saveError: null,
    isDeleting: false,
    deleteError: null,
    onScroll: () => null,
    onSortSelect: () => null,
    onFilterSelect: () => null,
    onViewSelect: () => null,
    onRefreshBookPress: () => null,
    onRssSyncPress: () => null,
    onSearchPress: () => null,
    onSaveSelected: () => null,
    selectedMediaType: 'both',
    onMediaTypeChange: () => null,
    useClientSideBooks: false,
    bookQueryKey: 'cleanTitle_ascending_all_both',
    bookQueryParams: { filters: { mediaType: 'all' } },
    bookBuckets: null,
    bookTotalCount: 3,
    onFetchBookIds: () => Promise.resolve({ ids: [1, 2, 3] }),
    ...overrides
  };
}

function makeBookIndex(props = {}) {
  const BookIndex = loadComponent();
  return new BookIndex(makeProps(props));
}

function findConfirmModal(bookIndex) {
  return findElements(bookIndex.render(), (element) => element.type === 'Components/Modal/ConfirmModal')[0];
}

function flushPromises() {
  return new Promise((resolve) => setImmediate(resolve));
}

test('paged mass search keeps confirmation open until the full ID list is ready', async() => {
  let resolveIds = () => null;
  const searchedIds = [];
  const fetchPromise = new Promise((resolve) => {
    resolveIds = resolve;
  });
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => fetchPromise,
    onSearchPress: (ids) => searchedIds.push(ids)
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, true);
  assert.equal(bookIndex.state.isResolvingSearchBookIds, true);
  assert.equal(findConfirmModal(bookIndex).props.isSpinning, true);
  assert.deepEqual(searchedIds, []);

  resolveIds({ ids: [10, 11, 12] });
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, false);
  assert.equal(bookIndex.state.isResolvingSearchBookIds, false);
  assert.deepEqual(searchedIds, [[10, 11, 12]]);
});

test('bulk book edits keep a single selected format scoped when the library view shows both formats', () => {
  const savedPayloads = [];
  const bookIndex = makeBookIndex({
    items: [
      { id: 30, mediaType: 'audiobook' },
      { id: 31, mediaType: 'ebook' }
    ],
    selectedMediaType: 'both',
    onSaveSelected: (payload) => savedPayloads.push(payload)
  });
  bookIndex.state.selectedState = { 30: true };

  bookIndex.onSaveSelected({ monitored: true });

  assert.equal(savedPayloads.length, 1);
  assert.equal(savedPayloads[0].bookIds.length, 1);
  assert.equal(savedPayloads[0].bookIds[0], 30);
  assert.equal(savedPayloads[0].monitored, true);
  assert.equal(savedPayloads[0].mediaType, 'audiobook');
});

test('paged mass search failure displays provider error and retries before starting search', async() => {
  let fetchCount = 0;
  const searchedIds = [];
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => {
      fetchCount += 1;
      return fetchCount === 1 ? Promise.reject({ responseJSON: { message: 'book list unavailable' } }) : Promise.resolve({ ids: [9] });
    },
    onSearchPress: (ids) => searchedIds.push(ids)
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, true);
  assert.equal(bookIndex.state.isResolvingSearchBookIds, false);
  assert.equal(bookIndex.state.searchBookIdsError, 'book list unavailable');

  const modal = findConfirmModal(bookIndex);
  assert.equal(modal.props.confirmLabel, 'Retry');
  const errorAlert = findElements(modal.props.message, (element) => element.type === 'Components/Alert')[0];
  assert.equal(errorAlert.props.role, 'alert');
  assert.equal(errorAlert.props.children[0].props.children, 'book list unavailable');

  modal.props.onConfirm();
  await flushPromises();

  assert.equal(fetchCount, 2);
  assert.equal(bookIndex.state.isConfirmSearchModalOpen, false);
  assert.deepEqual(searchedIds, [[9]]);
});

test('mass-search queue failure stays in the confirmation and retries without fetching IDs again', async() => {
  let fetchCount = 0;
  const searchedIds = [];
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => {
      fetchCount += 1;
      return Promise.resolve({ ids: [14, 15] });
    },
    onSearchPress: (ids) => {
      searchedIds.push(ids);
      return searchedIds.length === 1 ? Promise.reject({ responseJSON: { message: 'search queue unavailable' } }) : Promise.resolve({ id: 7 });
    }
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, true);
  assert.equal(bookIndex.state.searchBookIdsError, 'search queue unavailable');
  assert.equal(bookIndex.state.isSubmittingBookSearch, false);
  assert.equal(findConfirmModal(bookIndex).props.confirmLabel, 'Retry');

  findConfirmModal(bookIndex).props.onConfirm();
  await flushPromises();

  assert.equal(fetchCount, 1);
  assert.deepEqual(searchedIds, [[14, 15], [14, 15]]);
  assert.equal(bookIndex.state.isConfirmSearchModalOpen, false);
  assert.equal(bookIndex.state.searchBookIdsError, null);
});

test('the mass-search dialog cannot close while the command is being queued', async() => {
  let resolveSearch = () => null;
  const bookIndex = makeBookIndex({
    onSearchPress: () => new Promise((resolve) => {
      resolveSearch = resolve;
    })
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();
  await flushPromises();

  assert.equal(bookIndex.state.isSubmittingBookSearch, true);
  assert.equal(findConfirmModal(bookIndex).props.hideCancelButton, true);
  bookIndex.onConfirmSearchModalClose();
  assert.equal(bookIndex.state.isConfirmSearchModalOpen, true);

  resolveSearch({ id: 8 });
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, false);
  assert.equal(bookIndex.state.isSubmittingBookSearch, false);
});

test('an empty paged mass-search ID result stays in the confirmation with a retry message', async() => {
  const searchedIds = [];
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => Promise.resolve({ ids: [] }),
    onSearchPress: (ids) => searchedIds.push(ids)
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, true);
  assert.equal(bookIndex.state.searchBookIdsError, 'NoBooksMatchedForMassSearch');
  assert.equal(findConfirmModal(bookIndex).props.confirmLabel, 'Retry');
  assert.deepEqual(searchedIds, []);
});

test('cancelled mass search ignores a late ID response', async() => {
  let resolveIds = () => null;
  const searchedIds = [];
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => new Promise((resolve) => {
      resolveIds = resolve;
    }),
    onSearchPress: (ids) => searchedIds.push(ids)
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();
  await flushPromises();
  bookIndex.onConfirmSearchModalClose();
  resolveIds({ ids: [7, 8] });
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, false);
  assert.equal(bookIndex.state.searchBookIdsError, null);
  assert.deepEqual(searchedIds, []);
});

test('select all displays a retry after a paged ID lookup fails and selects IDs on retry', async() => {
  let fetchCount = 0;
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => {
      fetchCount += 1;
      return fetchCount === 1 ? Promise.reject({ responseJSON: { message: 'selection unavailable' } }) : Promise.resolve({ ids: [20, 21] });
    }
  });
  bookIndex.state.isEditorActive = true;

  bookIndex.onSelectAllPress();
  await flushPromises();

  assert.equal(bookIndex.state.isSelectingAll, false);
  assert.equal(bookIndex.state.selectAllError, 'selection unavailable');

  const retryButton = findElements(bookIndex.render(), (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  await flushPromises();

  assert.equal(fetchCount, 2);
  assert.equal(bookIndex.state.selectAllError, null);
  assert.equal(bookIndex.state.allSelected, true);
  assert.deepEqual(bookIndex.getSelectedIds(), [20, 21]);
});

test('bulk book update reports queue failures and retries the same selected books', async() => {
  let refreshCount = 0;
  const refreshCalls = [];
  const bookIndex = makeBookIndex({
    onRefreshBookPress: (ids) => {
      refreshCount += 1;
      refreshCalls.push(ids);
      return refreshCount === 1 ? Promise.reject({ responseJSON: { message: 'update queue unavailable' } }) : Promise.resolve({ id: 1 });
    }
  });
  bookIndex.state.isEditorActive = true;
  bookIndex.state.selectedState = { 31: true };

  bookIndex.onRefreshBookPress();
  const updateButton = findElements(bookIndex.render(), (element) => element.type === 'Components/Page/Toolbar/PageToolbarButton')
    .find((element) => element.props.label === 'UpdateSelected');
  assert.equal(updateButton.props.isSpinning, true);
  assert.equal(updateButton.props.isDisabled, true);

  await flushPromises();

  assert.equal(bookIndex.state.refreshError, 'update queue unavailable');
  const retryButton = findElements(bookIndex.render(), (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  await flushPromises();

  assert.deepEqual(refreshCalls, [[31], [31]]);
  assert.equal(bookIndex.state.refreshError, null);
  assert.equal(bookIndex.state.isStartingRefresh, false);
});

test('book RSS Sync reports queue failures and offers a retry', async() => {
  let syncCount = 0;
  const bookIndex = makeBookIndex({
    onRssSyncPress: () => {
      syncCount += 1;
      return syncCount === 1 ? Promise.reject({ responseJSON: { message: 'RSS Sync unavailable' } }) : Promise.resolve({ id: 4 });
    }
  });

  bookIndex.onRssSyncPress();
  const syncButton = findElements(bookIndex.render(), (element) => element.type === 'Components/Page/Toolbar/PageToolbarButton')
    .find((element) => element.props.label === 'RSSSync');
  assert.equal(syncButton.props.isSpinning, true);
  assert.equal(syncButton.props.isDisabled, true);
  await flushPromises();

  assert.equal(bookIndex.state.rssSyncError, 'RSS Sync unavailable');
  const retryButton = findElements(bookIndex.render(), (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  await flushPromises();

  assert.equal(syncCount, 2);
  assert.equal(bookIndex.state.rssSyncError, null);
  assert.equal(bookIndex.state.isStartingRssSync, false);
});

test('a list change invalidates Select All and explains that the new list needs selection', async() => {
  let resolveIds = () => null;
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => new Promise((resolve) => {
      resolveIds = resolve;
    })
  });
  bookIndex.state.isEditorActive = true;

  bookIndex.onSelectAllPress();
  await flushPromises();

  const previousProps = bookIndex.props;
  bookIndex.props = {
    ...previousProps,
    selectedFilterKey: 'missing',
    bookQueryKey: 'cleanTitle_ascending_missing_both',
    bookTotalCount: 1
  };
  bookIndex.componentDidUpdate(previousProps);
  resolveIds({ ids: [1, 2, 3] });
  await flushPromises();

  assert.equal(bookIndex.state.isSelectingAll, false);
  assert.equal(bookIndex.state.allSelected, false);
  assert.equal(bookIndex.state.selectAllError, 'BookListChangedDuringSelectAll');
  assert.equal(bookIndex.state.selectAllErrorKind, 'WARNING');
  const notice = findElements(bookIndex.render(), (element) => element.type === 'Components/Alert')
    .find((element) => element.props.role === 'status');
  assert.equal(notice.props.children, 'BookListChangedDuringSelectAll');
});

test('a list change cancels mass search and offers a fresh confirmation', async() => {
  let resolveIds = () => null;
  const searchedIds = [];
  const bookIndex = makeBookIndex({
    onFetchBookIds: () => new Promise((resolve) => {
      resolveIds = resolve;
    }),
    onSearchPress: (ids) => searchedIds.push(ids)
  });

  bookIndex.onSearchPress();
  bookIndex.onSearchConfirmed();
  await flushPromises();

  const previousProps = bookIndex.props;
  bookIndex.props = {
    ...previousProps,
    selectedFilterKey: 'missing',
    bookQueryKey: 'cleanTitle_ascending_missing_both',
    bookTotalCount: 1
  };
  bookIndex.componentDidUpdate(previousProps);
  resolveIds({ ids: [2, 3] });
  await flushPromises();

  assert.equal(bookIndex.state.isConfirmSearchModalOpen, false);
  assert.equal(bookIndex.state.isResolvingSearchBookIds, false);
  assert.equal(bookIndex.state.searchBookIdsError, null);
  assert.equal(bookIndex.state.searchScopeChangeNotice, 'BookListChangedDuringMassSearch');
  assert.deepEqual(searchedIds, []);

  const reviewButton = findElements(bookIndex.render(), (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'ReviewSearch');
  assert.ok(reviewButton);
  reviewButton.props.onPress();
  assert.equal(bookIndex.state.isConfirmSearchModalOpen, true);
  assert.equal(bookIndex.state.searchScopeChangeNotice, null);
  assert.equal(findConfirmModal(bookIndex).props.message.props.children[0].props.children, 'MassBookSearchWarning');
});

test('book update and RSS queue responses do not update state after leaving the page', async() => {
  const actions = [
    { method: 'onRefreshBookPress', prop: 'onRefreshBookPress' },
    { method: 'onRssSyncPress', prop: 'onRssSyncPress' }
  ];

  for (const action of actions) {
    for (const result of ['resolve', 'reject']) {
      let completeRequest;
      let requestCount = 0;
      const request = new Promise((resolve, reject) => {
        completeRequest = result === 'resolve' ? resolve : reject;
      });
      const bookIndex = makeBookIndex({
        [action.prop]: () => {
          requestCount++;

          return request;
        }
      });
      let updatesAfterUnmount = 0;
      const setState = bookIndex.setState.bind(bookIndex);
      bookIndex.setState = (update) => {
        if (bookIndex._isUnmounted) {
          updatesAfterUnmount++;
        }

        setState(update);
      };

      bookIndex[action.method]();
      await flushPromises();
      assert.equal(requestCount, 1);

      bookIndex.componentWillUnmount();
      completeRequest(result === 'resolve' ? { id: 1 } : new Error('offline'));
      await flushPromises();

      assert.equal(updatesAfterUnmount, 0, `${action.method} ${result}`);
    }
  }
});

test('book queue controls remain active after a remount cycle', async() => {
  const bookIndex = makeBookIndex({
    onRefreshBookPress: () => Promise.resolve({ id: 1 })
  });

  bookIndex.componentWillUnmount();
  bookIndex.componentDidMount();
  bookIndex.onRefreshBookPress();
  await flushPromises();

  assert.equal(bookIndex._isUnmounted, false);
  assert.equal(bookIndex.state.isStartingRefresh, false);
});

test('narrow Books pages keep statistics in document flow across resizing and editing', () => {
  const bookIndex = makeBookIndex();
  bookIndex.state.scroller = {};
  const footer = () => findElements(bookIndex.render(), (element) => element.type === './BookIndexFooterConnector')[0];

  assert.equal(footer().props.isSticky, true);
  bookIndex.props = { ...bookIndex.props, isSmallScreen: true };
  assert.equal(footer().props.isSticky, false);
  bookIndex.props = { ...bookIndex.props, isSmallScreen: false };
  assert.equal(footer().props.isSticky, true);
  bookIndex.state.isEditorActive = true;
  assert.equal(footer().props.isSticky, false);
});
