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

class Component {
  constructor(props) {
    this.props = props;
    this.state = {};
  }
}

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

function loadErrorPage() {
  const filePath = path.join(__dirname, 'Components/Page/ErrorPage.js');
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
  const sandbox = {
    exports: {},
    require(moduleName) {
      if (moduleName === 'prop-types') {
        return { __esModule: true, default: PropTypes };
      }
      if (moduleName === 'react') {
        return { __esModule: true, default: React };
      }
      if (moduleName === 'Components/Link/Button') {
        return { __esModule: true, default: 'Button' };
      }
      if (moduleName === 'Utilities/Object/getErrorMessage') {
        return { __esModule: true, default: (error, fallback) => error?.message || fallback };
      }
      if (moduleName === 'Utilities/String/translate') {
        return {
          __esModule: true,
          default: (key) => key,
          translateWithFallback: (key, fallback) => fallback
        };
      }
      if (moduleName === './ErrorPage.css') {
        return { __esModule: true, default: { page: 'page', errorMessage: 'errorMessage', retry: 'retry', version: 'version' } };
      }
      return { __esModule: true, default: moduleName };
    }
  };
  vm.runInNewContext(code, sandbox, { filename: filePath });
  return sandbox.exports.default;
}

function loadPageConnector() {
  const filePath = path.join(__dirname, 'Components/Page/PageConnector.js');
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [
      [
        '@babel/preset-env',
        {
          targets: { node: 'current' },
          loose: true,
          modules: 'commonjs'
        }
      ],
      '@babel/preset-react'
    ]
  });
  const sandbox = {
    exports: {},
    localStorage: {
      setItem() {},
      removeItem() {}
    },
    require(moduleName) {
      if (moduleName === 'prop-types') {
        return { __esModule: true, default: PropTypes };
      }
      if (moduleName === 'react') {
        return { __esModule: true, default: React, Component };
      }
      if (moduleName === 'react-redux') {
        return { connect: () => (component) => component };
      }
      if (moduleName === 'react-router-dom') {
        return { withRouter: (component) => component };
      }
      if (moduleName === 'reselect') {
        return { createSelector: () => () => ({}) };
      }
      if (moduleName.startsWith('Store/')) {
        return new Proxy({}, { get: () => () => ({}) });
      }
      if (moduleName.startsWith('Store/Selectors/')) {
        return { __esModule: true, default: () => () => ({}) };
      }
      if (moduleName.startsWith('./')) {
        return { __esModule: true, default: moduleName };
      }
      return { __esModule: true, default: moduleName };
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
  if (typeof element === 'string') {
    return element;
  }
  if (Array.isArray(element)) {
    return element.map(getText).join(' ');
  }
  if (!element || typeof element !== 'object') {
    return '';
  }
  return getText(element.props?.children);
}

test('startup data failures identify the source and offer a working retry', () => {
  const ErrorPage = loadErrorPage();
  let retries = 0;
  const output = ErrorPage({
    version: '0.9.978',
    isLocalStorageSupported: true,
    languagesError: { message: 'Language service is offline' },
    onRetryPress: () => retries++
  });
  const retryButton = findElements(output, (element) => element.type === 'Button')[0];

  assert.match(getText(output), /Language service is offline/);
  assert.equal(retryButton.props.children, 'Retry');
  retryButton.props.onPress();
  assert.equal(retries, 1);
});

test('browser storage failures explain the issue without offering a retry that cannot help', () => {
  const ErrorPage = loadErrorPage();
  const output = ErrorPage({
    version: '0.9.978',
    isLocalStorageSupported: false,
    onRetryPress() {}
  });

  assert.match(getText(output), /Local Storage is not supported/);
  assert.equal(findElements(output, (element) => element.type === 'Button').length, 0);
});

test('startup Retry dispatches only the resources that failed', () => {
  const PageConnector = loadPageConnector();
  const requested = [];
  const props = {
    authorError: { message: 'author request failed' },
    languagesError: { message: 'language request failed' },
    dispatchFetchAuthor: () => requested.push('author'),
    dispatchFetchCustomFilters: () => requested.push('custom filters'),
    dispatchFetchTags: () => requested.push('tags'),
    dispatchFetchLanguages: () => requested.push('languages'),
    dispatchFetchQualityProfiles: () => requested.push('quality profiles'),
    dispatchFetchMetadataProfiles: () => requested.push('metadata profiles'),
    dispatchFetchImportLists: () => requested.push('import lists'),
    dispatchFetchIndexerFlags: () => requested.push('indexer flags'),
    dispatchFetchUISettings: () => requested.push('UI settings'),
    dispatchFetchStatus: () => requested.push('system status'),
    dispatchFetchTranslations: () => requested.push('translations')
  };
  const connector = new PageConnector(props);

  connector.onRetryPress();

  assert.deepEqual(requested, ['author', 'languages']);
});
