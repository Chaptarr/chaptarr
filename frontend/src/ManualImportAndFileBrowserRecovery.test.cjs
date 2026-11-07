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
  createRef() {
    return { current: { scrollTop: 0 } };
  },
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

function stylesMock() {
  return new Proxy({}, {
    get(_target, key) {
      return key;
    }
  });
}

function componentMock(moduleName) {
  const componentName = moduleName.replace(/^.*\//, '').replace(/^\.\//, '');
  return [moduleName, { __esModule: true, default: componentName }];
}

function manualImportMocks() {
  const groupBy = (items, iteratee) => items.reduce((groups, item) => {
    const key = iteratee(item);
    groups[key] = groups[key] || [];
    groups[key].push(item);
    return groups;
  }, {});
  const keyBy = (items, iteratee) => items.reduce((result, item) => {
    const value = typeof iteratee === 'function' ? iteratee(item) : iteratee.split('.').reduce((current, part) => current?.[part], item);
    result[value] = item;
    return result;
  }, {});
  const mapValues = (object, iteratee) => Object.fromEntries(Object.entries(object).map(([key, value]) => [key, iteratee(value, key)]));
  const lodash = {
    'lodash/cloneDeep': (value) => value.map((item) => ({ ...item })),
    'lodash/difference': (items, excluded) => items.filter((item) => !excluded.includes(item)),
    'lodash/filter': (items, predicate) => items.filter(predicate),
    'lodash/find': (items, predicate) => items.find(predicate),
    'lodash/groupBy': groupBy,
    'lodash/includes': (items, item) => items.includes(item),
    'lodash/intersection': (items, other) => items.filter((item) => other.includes(item)),
    'lodash/keyBy': keyBy,
    'lodash/map': (items, iteratee) => items.map(iteratee),
    'lodash/mapValues': mapValues,
    'lodash/some': (items, predicate) => items.some(predicate),
    'lodash/union': (...items) => [...new Set(items.flat())],
    'lodash/values': Object.values,
    'lodash/without': (items, excluded) => items.filter((item) => item !== excluded)
  };
  const mocks = new Map(Object.entries(lodash));
  const components = [
    'Components/Alert', 'Components/Form/FormInputGroup', 'Components/Form/SelectInput', 'Components/Icon', 'Components/Label',
    'Components/Link/Button', 'Components/Loading/LoadingIndicator', 'Components/Menu/Menu',
    'Components/Menu/MenuButton', 'Components/Menu/MenuContent', 'Components/Menu/SelectedMenuItem',
    'Components/Modal/ModalBody', 'Components/Modal/ModalContent', 'Components/Modal/ModalFooter',
    'Components/Modal/ModalHeader', 'Components/Table/Table', 'Components/Table/TableBody',
    'InteractiveImport/Author/SelectAuthorModal', 'InteractiveImport/Book/SelectBookModal',
    'InteractiveImport/Confirmation/ConfirmImportModal', 'InteractiveImport/Edition/SelectEditionModal',
    'InteractiveImport/IndexerFlags/SelectIndexerFlagsModal', 'InteractiveImport/ReleaseGroup/SelectReleaseGroupModal',
    'InteractiveImport/Quality/SelectQualityModal', './InteractiveImportRow'
  ];
  components.forEach((name) => mocks.set(...componentMock(name)));
  mocks.set('react', { __esModule: true, default: React, Component });
  mocks.set('prop-types', { __esModule: true, default: PropTypes });
  mocks.set('Helpers/Props', {
    align: {}, icons: {}, inputTypes: { CHECK: 'check' }, kinds: { DANGER: 'DANGER', INFO: 'INFO', SUCCESS: 'SUCCESS', WARNING: 'WARNING' }, scrollDirections: {}
  });
  mocks.set('InteractiveImport/importModeOptions', { __esModule: true, default: [] });
  mocks.set('Utilities/Object/getErrorMessage', {
    __esModule: true,
    default: (error, fallback) => error?.message || fallback
  });
  mocks.set('Utilities/String/translate', { __esModule: true, default: (key) => key });
  mocks.set('Utilities/Table/getSelectedIds', { __esModule: true, default: () => [] });
  mocks.set('Utilities/Table/selectAll', { __esModule: true, default: () => ({}) });
  mocks.set('Utilities/Table/toggleSelected', { __esModule: true, default: () => ({}) });
  mocks.set('./InteractiveImportModalContent.css', { __esModule: true, default: stylesMock() });
  return mocks;
}

test('manual import failure announces the error, retries, and prevents stale import submission', () => {
  const ManualImport = loadComponent('InteractiveImport/Interactive/InteractiveImportModalContent.js', manualImportMocks());
  let retries = 0;
  const component = new ManualImport({
    allowAuthorChange: true,
    showImportMode: true,
    showFilterExistingFiles: false,
    showReplaceExistingFiles: false,
    filterExistingFiles: true,
    replaceExistingFiles: false,
    importMode: 'move',
    isFetching: false,
    isPopulated: true,
    isSaving: false,
    error: { message: 'Import preview is offline' },
    items: [{ id: 7, path: '/stale/book.m4b', book: { id: 5 } }],
    mediaManagementSettings: { isFetching: false, isSaving: false, settings: {} },
    onRetryPress: () => retries++,
    onSortPress() {},
    onFilterExistingFilesChange() {},
    onReplaceExistingFilesChange() {},
    onImportModeChange() {},
    onPathFallbackChange() {},
    onImportSelectedPress() {},
    saveInteractiveImportItem() {},
    updateInteractiveImportItem() {},
    onModalClose() {}
  });
  const output = component.render();
  const alert = findElements(output, (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];
  const importButton = findElements(output, (element) => element.type === 'Button' && getText(element) === 'Import')[0];

  assert.match(getText(alert), /Import preview is offline/);
  retryButton.props.onPress();
  assert.equal(retries, 1);
  assert.equal(findElements(output, (element) => element.type === 'Table').length, 0);
  assert.equal(importButton.props.isDisabled, true);
});

function fileBrowserMocks() {
  const mocks = new Map([
    componentMock('Components/Alert'), componentMock('Components/Form/PathInput'), componentMock('Components/Link/Button'),
    componentMock('Components/Link/Link'), componentMock('Components/Loading/LoadingIndicator'),
    componentMock('Components/Modal/ModalBody'), componentMock('Components/Modal/ModalContent'),
    componentMock('Components/Modal/ModalFooter'), componentMock('Components/Modal/ModalHeader'),
    componentMock('Components/Scroller/Scroller'), componentMock('Components/Table/Table'),
    componentMock('Components/Table/TableBody'), componentMock('./FileBrowserRow')
  ]);
  mocks.set('react', { __esModule: true, default: React, Component });
  mocks.set('prop-types', { __esModule: true, default: PropTypes });
  mocks.set('Helpers/Props', { kinds: { DANGER: 'DANGER', WARNING: 'WARNING' }, scrollDirections: {} });
  mocks.set('Utilities/Object/getErrorMessage', {
    __esModule: true,
    default: (error, fallback) => error?.message || fallback
  });
  mocks.set('Utilities/String/translate', { __esModule: true, default: (key) => key });
  mocks.set('./FileBrowserModalContent.css', { __esModule: true, default: stylesMock() });
  return mocks;
}

test('file browser failure announces the error and retries the failed directory', () => {
  const FileBrowser = loadComponent('Components/FileBrowser/FileBrowserModalContent.js', fileBrowserMocks());
  const fetchedPaths = [];
  const component = new FileBrowser({
    name: 'rootFolder',
    value: '/books',
    currentPath: '/books',
    isFetching: false,
    isPopulated: false,
    error: { message: 'Permission denied' },
    parent: '/data',
    directories: [],
    files: [],
    isWindowsService: false,
    onFetchPaths: (path) => fetchedPaths.push(path),
    onClearPaths() {},
    onChange() {},
    onModalClose() {}
  });
  const output = component.render();
  const alert = findElements(output, (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
  const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

  assert.match(getText(alert), /Permission denied/);
  retryButton.props.onPress();
  assert.deepEqual(fetchedPaths, ['/books']);
});
