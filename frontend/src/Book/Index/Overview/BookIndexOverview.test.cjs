const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const filePath = path.join(__dirname, 'BookIndexOverview.js');
const source = fs.readFileSync(filePath, 'utf8');
const { code } = transformSync(source, {
  filename: filePath,
  babelrc: false,
  configFile: false,
  presets: [
    [
      '@babel/preset-env',
      {
        targets: {
          browsers: ['last 2 versions']
        },
        loose: true,
        modules: 'commonjs'
      }
    ],
    '@babel/preset-react'
  ]
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

const propType = () => propType;
propType.isRequired = propType;
propType.oneOf = () => propType;
propType.arrayOf = () => propType;
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

const requests = [];

function createAjaxRequest({ url }) {
  const callbacks = { done: null, fail: null };
  const request = {
    done(callback) {
      callbacks.done = callback;
      return request;
    },
    fail(callback) {
      callbacks.fail = callback;
      return request;
    }
  };

  const entry = { url, callbacks, abortCount: 0 };
  const abortRequest = () => {
    entry.abortCount++;

    if (callbacks.fail) {
      callbacks.fail({ aborted: true });
    }
  };

  entry.abortRequest = abortRequest;
  requests.push(entry);

  return { request, abortRequest };
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

function noop() {
  return null;
}

const mocks = new Map([
  ['react', { __esModule: true, default: React, Component }],
  ['prop-types', { __esModule: true, default: PropTypes }],
  ['classnames', { __esModule: true, default: (...values) => values.filter(Boolean).join(' ') }],
  ['Helpers/Props', { icons: { CHECK_CIRCLE: 'check', CIRCLE_OUTLINE: 'circle', EDIT: 'edit', REFRESH: 'refresh', SEARCH: 'search' }, kinds: { DANGER: 'danger' } }],
  ['Styles/Variables/dimensions', { __esModule: true, default: { authorIndexColumnPadding: '10px', authorIndexColumnPaddingSmallScreen: '5px' } }],
  ['Styles/Variables/fonts', { __esModule: true, default: { defaultFontSize: '14px', lineHeight: '1.2' } }],
  ['Utilities/createAjaxRequest', { __esModule: true, default: createAjaxRequest }],
  ['Utilities/String/stripHtml', { __esModule: true, default: (value) => value }],
  ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
  ['Utilities/Table/shouldIgnoreCardSelectionEvent', { __esModule: true, default: () => false }],
  ['Book/Index/getBookIndexItemMediaType', { __esModule: true, default: (_selected, mediaType) => mediaType }],
  ['Book/Index/ProgressBar/BookIndexProgressBar', { __esModule: true, default: 'BookIndexProgressBar' }],
  ['./BookIndexOverviewInfo', { __esModule: true, default: 'BookIndexOverviewInfo' }],
  ['Book/BookCover', { __esModule: true, default: 'BookCover' }],
  ['Book/BookMediaTypeBadge', { __esModule: true, default: 'BookMediaTypeBadge' }],
  ['Author/Delete/DeleteAuthorModal', { __esModule: true, default: 'DeleteAuthorModal' }],
  ['Author/Edit/EditAuthorModalConnector', { __esModule: true, default: 'EditAuthorModalConnector' }],
  ['Components/Alert', { __esModule: true, default: 'Alert' }],
  ['Components/Icon', { __esModule: true, default: 'Icon' }],
  ['Components/Link/Button', { __esModule: true, default: 'Button' }],
  ['Components/Link/IconButton', { __esModule: true, default: 'IconButton' }],
  ['Components/Link/Link', { __esModule: true, default: 'Link' }],
  ['Components/Link/SpinnerIconButton', { __esModule: true, default: 'SpinnerIconButton' }],
  ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
  ['react-text-truncate', { __esModule: true, default: 'TextTruncate' }],
  ['./BookIndexOverview.css', { __esModule: true, default: styles }]
]);

const sandbox = {
  exports: {},
  require(moduleName) {
    if (!mocks.has(moduleName)) {
      throw new Error(`Unexpected import: ${moduleName}`);
    }

    return mocks.get(moduleName);
  }
};

vm.runInNewContext(code, sandbox, { filename: filePath });

function createProps(id) {
  return {
    id,
    authorId: 1,
    title: `Book ${id}`,
    mediaType: 'ebook',
    selectedMediaType: 'ebook',
    titleSlug: `book-${id}`,
    statistics: { bookCount: 1, sizeOnDisk: 0, bookFileCount: 0, totalBookCount: 1 },
    images: [],
    posterWidth: 100,
    posterHeight: 156,
    rowHeight: 220,
    qualityProfile: {},
    overviewOptions: { detailedProgressBar: false },
    showSearchAction: false,
    showRelativeDates: false,
    shortDateFormat: 'MM/DD/YYYY',
    longDateFormat: 'MMMM D, YYYY',
    timeFormat: 'h:mm A',
    isSmallScreen: false,
    isRefreshingBook: false,
    isSearchingBook: false,
    onRefreshBookPress: noop,
    onSearchPress: noop,
    isEditorActive: false,
    isSelected: false,
    onSelectedChange: noop
  };
}

test('overview cards follow recycled book IDs and expose retry after a failed load', () => {
  requests.length = 0;
  const props = createProps(1);
  const BookIndexOverview = sandbox.exports.default;
  const instance = new BookIndexOverview(props);

  instance.componentDidMount();
  assert.equal(requests[0].url, '/book/1/overview');

  instance.props = createProps(2);
  instance.componentDidUpdate(props);
  assert.equal(requests[1].url, '/book/2/overview');
  assert.equal(instance.state.isOverviewLoading, true);

  requests[0].callbacks.done({ overview: 'Stale overview' });
  assert.equal(instance.state.overview, '');

  requests[1].callbacks.fail({ status: 500 });
  assert.equal(instance.state.hasOverviewError, true);
  assert.equal(instance.state.isOverviewLoading, false);

  const tree = instance.render();
  const alert = findElements(tree, (element) => element.type === 'Alert')[0];
  const retryButton = findElements(tree, (element) => element.type === 'Button')[0];

  assert.equal(alert.props.role, 'alert');
  assert.equal(alert.props['data-select-exempt'], 'true');
  assert.equal(retryButton.props.children, 'Retry');

  retryButton.props.onPress();
  assert.equal(requests[2].url, '/book/2/overview');
  assert.equal(instance.state.hasOverviewError, false);
  requests[2].callbacks.done({ overview: 'Fresh overview' });
  assert.equal(instance.state.overview, 'Fresh overview');
  assert.equal(instance.state.isOverviewLoading, false);
});

test('small-screen cards skip hidden overview requests and fetch after switching to desktop', () => {
  requests.length = 0;
  const props = { ...createProps(3), isSmallScreen: true };
  const BookIndexOverview = sandbox.exports.default;
  const instance = new BookIndexOverview(props);

  instance.componentDidMount();

  assert.equal(requests.length, 0);
  assert.equal(instance.state.isOverviewLoading, false);

  const previousProps = instance.props;
  instance.props = { ...props, isSmallScreen: false };
  instance.componentDidUpdate(previousProps);

  assert.equal(requests[0].url, '/book/3/overview');
  assert.equal(instance.state.isOverviewLoading, true);
});

test('switching to a small screen cancels the hidden overview request', () => {
  requests.length = 0;
  const props = createProps(4);
  const BookIndexOverview = sandbox.exports.default;
  const instance = new BookIndexOverview(props);

  instance.componentDidMount();

  const request = requests[0];
  const previousProps = instance.props;
  instance.props = { ...props, isSmallScreen: true };
  instance.componentDidUpdate(previousProps);

  assert.equal(instance.state.isOverviewLoading, false);
  assert.equal(instance.state.hasOverviewError, false);
  assert.equal(request.abortCount, 1);

  request.callbacks.done({ overview: 'Stale mobile overview' });
  assert.equal(instance.state.overview, '');
});
