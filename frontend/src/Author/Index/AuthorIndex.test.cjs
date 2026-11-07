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
  const filePath = path.join(__dirname, 'AuthorIndex.js');
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
    },
    Fragment: 'React.Fragment'
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
    ['Utilities/Author/getAuthorMediaTypeRootFolderStatus', mockDefault(() => ({ hasRootFolder: true }))],
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
    ['./AuthorIndex.css', mockDefault(styles)]
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

function makeAuthorIndex(onRefreshAuthorPress, onRssSyncPress = () => null) {
  const AuthorIndex = loadComponent();
  return new AuthorIndex({
    isFetching: false,
    isPopulated: true,
    error: null,
    totalItems: 1,
    items: [{ id: 42 }],
    columns: [],
    selectedFilterKey: 'all',
    filters: [],
    customFilters: [],
    selectedMediaType: 'ebook',
    sortKey: 'cleanTitle',
    sortDirection: 'ascending',
    view: 'table',
    isRefreshingAuthor: false,
    isRssSyncExecuting: false,
    isOrganizingAuthor: false,
    isRetaggingAuthor: false,
    isSaving: false,
    saveError: null,
    saveWarning: null,
    isDeleting: false,
    deleteError: null,
    onSortSelect: () => null,
    onFilterSelect: () => null,
    onMediaTypeChange: () => null,
    onViewSelect: () => null,
    onRefreshAuthorPress,
    onRssSyncPress,
    onScroll: () => null,
    onSaveSelected: () => null
  });
}

function flushPromises() {
  return new Promise((resolve) => setImmediate(resolve));
}

test('bulk author update reports queue failures and retries the same authors and format', async() => {
  const refreshCalls = [];
  const authorIndex = makeAuthorIndex((authorIds, mediaType) => {
    refreshCalls.push([authorIds, mediaType]);
    return refreshCalls.length === 1 ? Promise.reject({ responseJSON: { message: 'author update unavailable' } }) : Promise.resolve({ id: 1 });
  });
  authorIndex.state.isEditorActive = true;
  authorIndex.state.selectedState = { 42: true };

  authorIndex.onRefreshAuthorPress();
  await flushPromises();

  assert.equal(authorIndex.state.refreshError, 'author update unavailable');
  assert.deepEqual(authorIndex.state.failedRefreshAuthorIds, [42]);
  assert.equal(authorIndex.state.failedRefreshMediaType, 'ebook');

  authorIndex.props = { ...authorIndex.props, selectedMediaType: 'audiobook' };
  const retryButton = findElements(authorIndex.render(), (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  await flushPromises();

  assert.deepEqual(refreshCalls, [[[42], 'ebook'], [[42], 'ebook']]);
  assert.equal(authorIndex.state.refreshError, null);
  assert.equal(authorIndex.state.isStartingRefresh, false);
});

test('author RSS Sync reports queue failures and offers a retry', async() => {
  let syncCount = 0;
  const authorIndex = makeAuthorIndex(() => null, () => {
    syncCount += 1;
    return syncCount === 1 ? Promise.reject({ responseJSON: { message: 'RSS Sync unavailable' } }) : Promise.resolve({ id: 4 });
  });

  authorIndex.onRssSyncPress();
  await flushPromises();

  assert.equal(authorIndex.state.rssSyncError, 'RSS Sync unavailable');
  const retryButton = findElements(authorIndex.render(), (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  await flushPromises();

  assert.equal(syncCount, 2);
  assert.equal(authorIndex.state.rssSyncError, null);
  assert.equal(authorIndex.state.isStartingRssSync, false);
});

test('author update and RSS queue responses do not update state after leaving the page', async() => {
  const actions = [
    { method: 'onRefreshAuthorPress', prop: 'refresh' },
    { method: 'onRssSyncPress', prop: 'rss' }
  ];

  for (const action of actions) {
    for (const result of ['resolve', 'reject']) {
      let completeRequest;
      let requestCount = 0;
      const request = new Promise((resolve, reject) => {
        completeRequest = result === 'resolve' ? resolve : reject;
      });
      const callbacks = {
        refresh: () => {
          requestCount++;

          return request;
        },
        rss: () => {
          requestCount++;

          return request;
        }
      };
      const authorIndex = makeAuthorIndex(callbacks.refresh, callbacks.rss);
      let updatesAfterUnmount = 0;
      const setState = authorIndex.setState.bind(authorIndex);
      authorIndex.setState = (update) => {
        if (authorIndex._isUnmounted) {
          updatesAfterUnmount++;
        }

        setState(update);
      };

      authorIndex[action.method]();
      await flushPromises();
      assert.equal(requestCount, 1);

      authorIndex.componentWillUnmount();
      completeRequest(result === 'resolve' ? { id: 1 } : new Error('offline'));
      await flushPromises();

      assert.equal(updatesAfterUnmount, 0, `${action.method} ${result}`);
    }
  }
});

test('author queue controls remain active after a remount cycle', async() => {
  const authorIndex = makeAuthorIndex(() => Promise.resolve({ id: 1 }));

  authorIndex.componentWillUnmount();
  authorIndex.componentDidMount();
  authorIndex.onRefreshAuthorPress();
  await flushPromises();

  assert.equal(authorIndex._isUnmounted, false);
  assert.equal(authorIndex.state.isStartingRefresh, false);
});
