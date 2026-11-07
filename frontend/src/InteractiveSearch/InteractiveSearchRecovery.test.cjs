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

function Component(props) {
  this.props = props;
  this.state = {};
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

function loadComponent(componentPath, moduleMocks) {
  const filePath = path.join(__dirname, componentPath);
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

function makeMocks() {
  const styles = new Proxy({}, {
    get(_target, key) {
      return key;
    }
  });

  return new Map([
    ['react', { __esModule: true, default: React, Component }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Components/Alert', { __esModule: true, default: 'Alert' }],
    ['Components/Icon', { __esModule: true, default: 'Icon' }],
    ['Components/Link/Button', { __esModule: true, default: 'Button' }],
    ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
    ['Components/Table/Table', { __esModule: true, default: 'Table' }],
    ['Components/Table/TableBody', { __esModule: true, default: 'TableBody' }],
    ['Helpers/Props', { icons: {}, kinds: { DANGER: 'DANGER', INFO: 'INFO', WARNING: 'WARNING' }, sortDirections: {} }],
    ['Utilities/Object/getErrorMessage', {
      __esModule: true,
      default: (error, fallback) => error?.message || fallback
    }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['./FilterWarning', { __esModule: true, default: 'FilterWarning' }],
    ['./InteractiveSearchMediaTypeToggle', { __esModule: true, default: 'InteractiveSearchMediaTypeToggle' }],
    ['./InteractiveSearchRow', { __esModule: true, default: 'InteractiveSearchRow' }],
    ['./InteractiveSearch.css', { __esModule: true, default: styles }]
  ]);
}

test('interactive search error is announced and offers a working retry', () => {
  const InteractiveSearch = loadComponent('InteractiveSearch.js', makeMocks());
  let retries = 0;
  const component = new InteractiveSearch({
    searchPayload: { bookId: 17, initialMediaType: 'ebook' },
    isFetching: false,
    isPopulated: false,
    error: { message: 'Indexer is offline' },
    totalReleasesCount: 0,
    items: [],
    hiddenItems: [],
    onSortPress() {},
    onGrabPress() {},
    onToggleBypass() {},
    onRetryPress: () => retries++
  });
  const output = component.render();
  const alert = findElements(output, (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

  assert.match(getText(alert), /Indexer is offline/);
  assert.equal(retryButton.props.children, 'Retry');
  retryButton.props.onPress();
  assert.equal(retries, 1);
});
