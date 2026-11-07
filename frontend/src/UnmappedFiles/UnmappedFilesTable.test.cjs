const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const propType = () => propType;
propType.isRequired = propType;

const PropTypes = new Proxy({}, {
  get() {
    return propType;
  }
});

function Component(props, context) {
  this.props = props;
  this.context = context;
  this.state = {};
}

Component.prototype.setState = function(update) {
  const nextState = typeof update === 'function' ? update(this.state, this.props) : update;
  this.state = { ...this.state, ...nextState };
};

const React = {
  Component,
  Fragment: 'Fragment',
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

function loadComponent() {
  const filePath = path.join(__dirname, 'UnmappedFilesTable.js');
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

  const kinds = { DANGER: 'danger', INFO: 'info', PRIMARY: 'primary' };
  const moduleMocks = new Map([
    ['react', { __esModule: true, default: React, Component, Fragment: 'Fragment' }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Helpers/Props', {
      align: { RIGHT: 'right' },
      icons: new Proxy({}, { get: (_target, key) => key }),
      kinds,
      sizes: { LARGE: 'large' },
      sortDirections: { all: [] }
    }],
    ['Utilities/MediaFile/getMediaTypeFromExtension', { __esModule: true, default: () => 'ebook' }],
    ['Utilities/Object/hasDifferentItemsOrOrder', { __esModule: true, default: () => false }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['Utilities/Table/getToggledRange', { __esModule: true, default: () => [] }]
  ]);

  const sandbox = {
    exports: {},
    require(moduleName) {
      return moduleMocks.get(moduleName) || { __esModule: true, default: moduleName };
    }
  };

  vm.runInNewContext(code, sandbox, { filename: filePath });
  return sandbox.exports.default;
}

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

  findElements(element.props?.children, predicate, matches);
  return matches;
}

function getText(element) {
  if (Array.isArray(element)) {
    return element.map(getText).join(' ');
  }

  if (typeof element === 'string' || typeof element === 'number') {
    return String(element);
  }

  return element?.props ? getText(element.props.children) : '';
}

function renderTable(overrides = {}) {
  const UnmappedFilesTable = loadComponent();
  const noop = () => null;
  const table = new UnmappedFilesTable({
    items: [],
    columns: [],
    isFetching: false,
    isPopulated: false,
    isDeleting: false,
    isSendingLogs: false,
    isRefreshingFiles: false,
    isRetryMatching: false,
    fetchUnmappedFiles: noop,
    onTableOptionChange: noop,
    onSortPress: noop,
    deleteUnmappedFile: noop,
    deleteUnmappedFiles: noop,
    onRefreshUnmappedFilesPress: noop,
    onRetryUnmappedMatchPress: noop,
    previewMatchingLogsForReview: noop,
    sendMatchingLogsForReview: noop,
    ...overrides
  });

  return table.render();
}

function getFileAction(output, label) {
  return findElements(output, (element) => {
    return element.type === 'Components/Page/Toolbar/PageToolbarButton' && element.props.label === label;
  })[0];
}

test('bulk file actions wait for a successful non-empty file list', () => {
  const loadingOutput = renderTable({ isFetching: true });
  const failedOutput = renderTable({ error: { status: 503 } });
  const loadedOutput = renderTable({
    isPopulated: true,
    items: [{ id: 4, path: '/books/Example.epub' }]
  });

  [loadingOutput, failedOutput].forEach((output) => {
    assert.equal(getFileAction(output, 'UnmappedFilesRefreshFiles').props.isDisabled, true);
    assert.equal(getFileAction(output, 'UnmappedFilesRetryMatch').props.isDisabled, true);
  });

  assert.equal(getFileAction(loadedOutput, 'UnmappedFilesRefreshFiles').props.isDisabled, false);
  assert.equal(getFileAction(loadedOutput, 'UnmappedFilesRetryMatch').props.isDisabled, false);
});

test('a failed initial file load explains the error and retries the list', () => {
  let retryCount = 0;
  const output = renderTable({
    error: { status: 503 },
    fetchUnmappedFiles: () => retryCount++
  });
  const alert = findElements(output, (element) => {
    return element.type === 'Components/Alert' && element.props.role === 'alert';
  })[0];

  assert.ok(alert);
  assert.match(getText(alert), /UnmappedFilesLoadFailed/);

  const retryButton = findElements(alert, (element) => {
    return element.type === 'Components/Link/Button' && getText(element) === 'Retry';
  })[0];

  assert.ok(retryButton);
  retryButton.props.onPress();
  assert.equal(retryCount, 1);
});

test('a retry in progress replaces the old error with a loading state', () => {
  const output = renderTable({
    error: { status: 503 },
    isFetching: true
  });

  assert.equal(findElements(output, (element) => {
    return element.type === 'Components/Alert' && element.props.role === 'alert';
  }).length, 0);
  assert.equal(findElements(output, (element) => {
    return element.type === 'Components/Loading/LoadingIndicator';
  }).length, 1);
});
