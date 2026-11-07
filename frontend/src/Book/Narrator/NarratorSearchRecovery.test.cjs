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

function loadComponent() {
  const filePath = path.join(__dirname, 'NarratorSearchModalContent.js');
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
    ['Components/Link/Button', { __esModule: true, default: 'Button' }],
    ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
    ['Components/Modal/ModalBody', { __esModule: true, default: 'ModalBody' }],
    ['Components/Modal/ModalContent', { __esModule: true, default: 'ModalContent' }],
    ['Components/Modal/ModalFooter', { __esModule: true, default: 'ModalFooter' }],
    ['Components/Modal/ModalHeader', { __esModule: true, default: 'ModalHeader' }],
    ['Components/Table/Table', { __esModule: true, default: 'Table' }],
    ['Components/Table/TableBody', { __esModule: true, default: 'TableBody' }],
    ['Helpers/Props', { kinds: { DANGER: 'DANGER', INFO: 'INFO', WARNING: 'WARNING' } }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['./NarratorSearchRow', { __esModule: true, default: 'NarratorSearchRow' }],
    ['./NarratorSearchModalContent.css', { __esModule: true, default: styles }]
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

function renderNarratorSearch(overrides = {}) {
  const NarratorSearchModalContent = loadComponent();
  const component = new NarratorSearchModalContent({
    bookId: 7,
    bookTitle: 'Dune',
    authorName: 'Frank Herbert',
    discovery: {},
    isSearching: false,
    isSettingPreferred: false,
    onNarratorSelect() {},
    onRefreshPress() {},
    onModalClose() {},
    ...overrides
  });

  return { component, output: component.render() };
}

test('narrator discovery failure announces the error and exposes its refresh callback', () => {
  let retries = 0;
  const { output } = renderNarratorSearch({
    discovery: { errorMessage: 'Provider is offline' },
    onRefreshPress: () => retries++
  });
  const alert = findElements(output, (element) => element.type === 'Alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

  assert.equal(alert.props.role, 'alert');
  assert.equal(alert.props.children[0], 'Provider is offline');
  assert.equal(retryButton.props.children, 'Retry');
  retryButton.props.onPress();
  assert.equal(retries, 1);
});

test('retry loading hides stale narrator errors and retry actions', () => {
  const { output } = renderNarratorSearch({
    discovery: { errorMessage: 'Provider is offline' },
    isSearching: true
  });

  assert.equal(findElements(output, (element) => element.type === 'LoadingIndicator').length, 1);
  assert.equal(findElements(output, (element) => element.type === 'Alert').length, 0);
  assert.equal(findElements(output, (element) => element.type === 'Button' && element.props.children === 'Retry').length, 0);
});
