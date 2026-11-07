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
    ['Helpers/Props', { kinds: { DANGER: 'DANGER', INFO: 'INFO', WARNING: 'WARNING' } }],
    ['Utilities/Object/getErrorMessage', {
      __esModule: true,
      default: (error, fallback) => error?.message || fallback
    }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['Utilities/Table/getSelectedIds', { __esModule: true, default: () => [] }],
    ['Utilities/Table/selectAll', { __esModule: true, default: () => ({}) }],
    ['Utilities/Table/toggleSelected', { __esModule: true, default: () => ({}) }],
    ['./RetagPreviewRow', { __esModule: true, default: 'RetagPreviewRow' }],
    ['./OrganizePreviewRow', { __esModule: true, default: 'OrganizePreviewRow' }],
    ['./ConvertPreviewRow', { __esModule: true, default: 'ConvertPreviewRow' }],
    ['./RetagPreviewModalContent.css', { __esModule: true, default: styles }],
    ['./OrganizePreviewModalContent.css', { __esModule: true, default: styles }],
    ['./ConvertPreviewModalContent.css', { __esModule: true, default: styles }]
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

function renderFailedPreview(componentPath, overrides = {}) {
  const Preview = loadComponent(componentPath);
  const component = new Preview({
    isFetching: false,
    isPopulated: false,
    error: { message: 'Preview service is offline' },
    items: [],
    path: '/books',
    trackFormat: 'M4B',
    renameBooksEnabled: true,
    canMoveToCanonicalAuthorFolder: false,
    moveToCanonicalAuthorFolder: false,
    onMoveToCanonicalAuthorFolderChange() {},
    onRetagPress() {},
    onOrganizePress() {},
    onConvertPress() {},
    onRetryPress() {},
    onModalClose() {},
    ...overrides
  });

  return component.render();
}

const previewComponents = [
  ['retag', 'Retag/RetagPreviewModalContent.js'],
  ['organize', 'Organize/OrganizePreviewModalContent.js'],
  ['convert', 'Convert/ConvertPreviewModalContent.js']
];

for (const [name, componentPath] of previewComponents) {
  test(`${name} preview error is announced and can be retried`, () => {
    let retries = 0;
    const output = renderFailedPreview(componentPath, {
      onRetryPress: () => retries++
    });
    const alert = findElements(output, (element) => element.type === 'Alert' && element.props.role === 'alert')[0];
    const retryButton = findElements(alert, (element) => element.type === 'Button')[0];

    assert.match(getText(alert), /Preview service is offline/);
    assert.equal(retryButton.props.children, 'Retry');
    retryButton.props.onPress();
    assert.equal(retries, 1);
  });
}
