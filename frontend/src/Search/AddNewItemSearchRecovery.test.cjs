const assert = require('node:assert/strict');
const { test } = require('node:test');
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

Component.prototype.setState = function(update, callback) {
  const nextState = typeof update === 'function' ? update(this.state, this.props) : update;
  this.state = { ...this.state, ...nextState };

  if (callback) {
    callback();
  }
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

function loadComponent(searchAsYouType) {
  const filePath = path.join(__dirname, 'AddNewItem.js');
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

  const styles = new Proxy({}, {
    get(_target, key) {
      return key;
    }
  });
  const moduleMocks = new Map([
    ['react', { __esModule: true, default: React, Component }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Components/Alert', { __esModule: true, default: 'Alert' }],
    ['Components/Form/CheckInput', { __esModule: true, default: 'CheckInput' }],
    ['Components/Form/SelectInput', { __esModule: true, default: 'SelectInput' }],
    ['Components/Form/TextInput', { __esModule: true, default: 'TextInput' }],
    ['Components/Icon', { __esModule: true, default: 'Icon' }],
    ['Components/Link/Button', { __esModule: true, default: 'Button' }],
    ['Components/Link/Link', { __esModule: true, default: 'Link' }],
    ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
    ['Components/Page/PageContent', { __esModule: true, default: 'PageContent' }],
    ['Components/Page/PageContentBody', { __esModule: true, default: 'PageContentBody' }],
    ['Helpers/Props', {
      icons: { CARET_DOWN: 'CARET_DOWN', REMOVE: 'REMOVE', SEARCH: 'SEARCH' },
      kinds: { INFO: 'INFO', PRIMARY: 'PRIMARY', WARNING: 'WARNING' }
    }],
    ['Utilities/Object/getErrorMessage', {
      __esModule: true,
      default: (error, fallback) => error?.message || fallback
    }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['./Author/AddNewAuthorSearchResultConnector', { __esModule: true, default: 'AddNewAuthorSearchResultConnector' }],
    ['./Book/AddNewBookSearchResultConnector', { __esModule: true, default: 'AddNewBookSearchResultConnector' }],
    ['./Series/AddNewSeriesSearchResult', { __esModule: true, default: 'AddNewSeriesSearchResult' }],
    ['./AddNewItem.css', { __esModule: true, default: styles }]
  ]);

  const sandbox = {
    exports: {},
    window: {
      localStorage: {
        getItem() {
          return searchAsYouType ? 'true' : 'false';
        },
        setItem() {}
      }
    },
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

function renderFailedSearch(searchAsYouType) {
  const AddNewItem = loadComponent(searchAsYouType);
  const submittedSearches = [];
  const component = new AddNewItem({
    term: 'Dune',
    provider: 'hardcover',
    isHardcoverConfigured: true,
    metadataProviderConfig: { isPopulated: false, item: {} },
    isFetching: false,
    error: { message: 'Provider is offline' },
    isAdding: false,
    addError: null,
    items: [],
    hasExistingAuthors: true,
    onSearchChange() {},
    onClearSearch() {},
    onSearchSubmit(term, provider) {
      submittedSearches.push({ term, provider });
    }
  });

  component.state.isFetching = false;
  component.state.hasSearched = true;

  return { component, output: component.render(), submittedSearches };
}

test('failed search-as-you-type is announced and offers a working Retry action', () => {
  const { component, output, submittedSearches } = renderFailedSearch(true);
  const alert = findElements(output, (element) => element.type === 'Alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')
    .find((button) => button.props.children === 'Retry');

  assert.equal(alert.props.role, 'alert');
  assert.equal(retryButton !== undefined, true);
  retryButton.props.onPress();

  assert.deepEqual(submittedSearches, [{ term: 'Dune', provider: 'hardcover' }]);
  assert.equal(component.state.isFetching, true);
});

test('manual-search errors keep the existing Search action without a duplicate Retry', () => {
  const { output } = renderFailedSearch(false);
  const buttons = findElements(output, (element) => element.type === 'Button');

  assert.equal(buttons.some((button) => button.props.children === 'Search'), true);
  assert.equal(buttons.some((button) => button.props.children === 'Retry'), false);
});
