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

function loadComponent(componentPath) {
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
  const componentNames = new Map([
    ['Components/Alert', 'Alert'],
    ['Components/Link/Button', 'Button'],
    ['Components/Loading/LoadingIndicator', 'LoadingIndicator'],
    ['Components/Modal/ConfirmModal', 'ConfirmModal'],
    ['Components/Page/PageContent', 'PageContent'],
    ['Components/Page/PageContentBody', 'PageContentBody'],
    ['Components/Page/Toolbar/PageToolbar', 'PageToolbar'],
    ['Components/Page/Toolbar/PageToolbarButton', 'PageToolbarButton'],
    ['Components/Page/Toolbar/PageToolbarSection', 'PageToolbarSection'],
    ['Components/Table/Table', 'Table'],
    ['Components/Table/TableBody', 'TableBody'],
    ['Components/Table/TableOptions/TableOptionsModalWrapper', 'TableOptionsModalWrapper'],
    ['Components/Table/TablePager', 'TablePager'],
    ['./BlocklistRowConnector', 'BlocklistRowConnector'],
    ['./IgnoredDownloadRowConnector', 'IgnoredDownloadRowConnector']
  ]);
  const helpers = {
    align: { RIGHT: 'RIGHT' },
    icons: { REMOVE: 'REMOVE', CLEAR: 'CLEAR', TABLE: 'TABLE' },
    inputTypes: { NUMBER: 'number', SELECT: 'select', CHECK: 'check' },
    kinds: { DANGER: 'DANGER', INFO: 'INFO' },
    sizes: { MEDIUM: 'medium' }
  };
  const sandbox = {
    exports: {},
    require(moduleName) {
      if (moduleName === 'react') {
        return { __esModule: true, default: React, Component };
      }
      if (moduleName === 'react-redux') {
        return { connect: () => (ConnectedComponent) => ConnectedComponent };
      }
      if (moduleName === 'lodash/find') {
        return { __esModule: true, default: (items, query) => items.find((item) => Object.entries(query).every(([key, value]) => item[key] === value)) };
      }
      if (moduleName === 'lodash/reduceRight') {
        return { __esModule: true, default: (items, iteratee, accumulator) => items.reduceRight(iteratee, accumulator) };
      }
      if (moduleName === 'prop-types') {
        return { __esModule: true, default: PropTypes };
      }
      if (moduleName === 'Helpers/Props') {
        return helpers;
      }
      if (moduleName === 'Utilities/Object/getErrorMessage') {
        return { __esModule: true, default: (error, fallback) => error?.message || fallback };
      }
      if (moduleName === 'Utilities/Object/getRemovedItems') {
        return { __esModule: true, default: () => [] };
      }
      if (moduleName === 'Utilities/Object/hasDifferentItems') {
        return { __esModule: true, default: () => false };
      }
      if (moduleName === 'Utilities/String/translate') {
        return { __esModule: true, default: (key) => key };
      }
      if (moduleName === 'Utilities/Table/getSelectedIds') {
        return {
          __esModule: true,
          default: (selectedState) => Object.keys(selectedState).filter((id) => selectedState[id]).map(Number)
        };
      }
      if (moduleName === 'Utilities/Table/removeOldSelectedState') {
        return { __esModule: true, default: () => ({}) };
      }
      if (moduleName === 'Utilities/Table/selectAll' || moduleName === 'Utilities/Table/toggleSelected') {
        return { __esModule: true, default: () => ({}) };
      }
      const componentName = componentNames.get(moduleName);
      if (componentName) {
        return { __esModule: true, default: componentName };
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

const activityPages = [
  [
    'Blocklist',
    'Activity/Blocklist/Blocklist.js',
    {
      isAuthorFetching: false,
      isAuthorPopulated: true,
      isClearingBlocklistExecuting: false,
      onClearBlocklistPress() {}
    }
  ],
  [
    'Ignored Downloads',
    'Activity/Ignored/IgnoredDownloads.js',
    {}
  ]
];

for (const [name, componentPath, pageProps] of activityPages) {
  test(`${name} load failure is announced and can be retried`, () => {
    const ActivityPage = loadComponent(componentPath);
    let retries = 0;
    const component = new ActivityPage({
      isFetching: false,
      isPopulated: false,
      error: { message: 'Activity service is offline' },
      items: [],
      columns: [],
      totalRecords: 0,
      isRemoving: false,
      onRemoveSelected() {},
      onRetryPress: () => retries++,
      ...pageProps
    });
    const output = component.render();
    const alert = findElements(output, (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
    const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

    assert.match(getText(alert), /Activity service is offline/);
    retryButton.props.onPress();
    assert.equal(retries, 1);
  });
}

test('book file quality load failure is announced and can be retried', () => {
  const BookFileEditor = loadComponent('BookFile/Editor/BookFileEditorTableContent.js');
  let retries = 0;
  const component = new BookFileEditor({
    isDeleting: false,
    isFetching: false,
    isPopulated: true,
    error: 'Quality profiles are unavailable',
    items: [{ id: 7 }],
    qualities: [],
    onDeletePress() {},
    onQualityChange() {},
    onRetryPress: () => retries++,
    dispatchDeleteBookFile() {}
  });
  component.state.selectedState = { 7: true };
  const output = component.render();
  const alert = findElements(output, (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];
  const qualitySelect = findElements(output, (element) => element.props?.name === 'quality')[0];

  assert.match(getText(alert), /Quality profiles are unavailable/);
  assert.equal(qualitySelect.props.isDisabled, true);
  retryButton.props.onPress();
  assert.equal(retries, 1);
});

test('manual import file details load failure retries the selected book file', () => {
  const FileDetails = loadComponent('BookFile/FileDetailsConnector.js');
  const requests = [];
  const component = new FileDetails({
    items: [],
    id: 27,
    isFetching: false,
    error: { message: 'File details are unavailable' },
    fetchBookFiles: (request) => requests.push(request)
  });
  const alert = findElements(component.render(), (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

  assert.match(getText(alert), /File details are unavailable/);
  retryButton.props.onPress();
  assert.equal(requests.length, 1);
  assert.equal(requests[0].id, 27);

  const fallback = new FileDetails({
    items: [],
    id: 28,
    isFetching: false,
    error: {},
    fetchBookFiles() {}
  }).render();
  assert.match(getText(fallback), /LoadingBookFilesFailed/);
});

test('conversion settings load failure exposes the server error and retries', () => {
  const ConversionSettings = loadComponent('Settings/Conversion/ConversionSettings.js');
  let retries = 0;
  const component = new ConversionSettings({
    isFetching: false,
    error: { message: 'Conversion settings are unavailable' },
    settings: {},
    hasSettings: false,
    advancedSettings: false,
    onInputChange() {},
    onSavePress() {},
    onRetryPress: () => retries++
  });
  const alert = findElements(component.render(), (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

  assert.match(getText(alert), /Conversion settings are unavailable/);
  retryButton.props.onPress();
  assert.equal(retries, 1);
});
