const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const propType = () => propType;
propType.isRequired = propType;
propType.arrayOf = () => propType;

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

function createRequestFactory(requests) {
  return function createAjaxRequest({ url, method = 'GET' }) {
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
    const entry = {
      url,
      method,
      resolve(data) {
        callbacks.done(data);
      },
      reject(xhr) {
        callbacks.fail(xhr);
      },
      abortRequest() {
        callbacks.fail({ aborted: true });
      }
    };

    requests.push(entry);
    return { request, abortRequest: entry.abortRequest };
  };
}

function loadComponent(requests) {
  const filePath = path.join(__dirname, 'ImportListSyncModal.js');
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
    }
  };
  const mocks = new Map([
    ['react', { __esModule: true, default: React, Component }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Helpers/Props', { kinds: { PRIMARY: 'primary' }, sizes: { LARGE: 'large', SMALL: 'small' } }],
    ['Utilities/createAjaxRequest', { __esModule: true, default: createRequestFactory(requests) }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['Utilities/String/translateServerMessage', { __esModule: true, default: (message) => message }],
    ['./ImportListSyncModal.css', { __esModule: true, default: styles }]
  ]);
  const sandbox = {
    exports: {},
    require(moduleName) {
      if (mocks.has(moduleName)) {
        return mocks.get(moduleName);
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

function makeModal(requests, isOpen = true) {
  const ImportListSyncModal = loadComponent(requests);
  const modal = new ImportListSyncModal({
    id: 12,
    name: 'Hardcover shelf',
    isOpen,
    onModalClose: () => null
  });

  return { ImportListSyncModal, modal };
}

test('preview failures offer retry and repeated preview clicks start only one request', () => {
  const requests = [];
  const { modal } = makeModal(requests);
  modal.componentDidMount();
  modal.onPreviewPress();
  modal.onPreviewPress();

  assert.equal(requests.length, 2);
  assert.equal(requests[1].method, 'POST');

  requests[1].reject({ responseJSON: { error: 'preview unavailable' } });
  assert.equal(modal.state.isPreviewing, false);
  assert.equal(modal.state.previewError, 'preview unavailable');
  assert.equal(requests.length, 3);
  assert.equal(requests[0].url, '/importlist/12/history');
  assert.equal(requests[2].url, '/importlist/12/history');

  const output = modal.render();
  const retryButton = findElements(output, (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  assert.equal(requests.length, 4);
  assert.equal(requests[3].url, '/importlist/12/preview');
});

test('history failures expose an explicit retry action', () => {
  const requests = [];
  const { modal } = makeModal(requests);
  modal.componentDidMount();
  requests[0].reject({ statusText: 'history unavailable' });

  assert.equal(modal.state.historyError, 'history unavailable');
  const output = modal.render();
  const retryButton = findElements(output, (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();
  assert.equal(requests.length, 2);
  assert.equal(modal.state.isLoadingHistory, true);
});

test('closing during preview cancels stale results and resets the next open', () => {
  const requests = [];
  const { modal } = makeModal(requests);
  modal.componentDidMount();
  modal.onPreviewPress();

  const previousProps = modal.props;
  modal.props = { ...previousProps, isOpen: false };
  modal.componentDidUpdate(previousProps);
  assert.equal(modal.state.isPreviewing, false);
  assert.equal(modal.state.preview, null);

  requests[1].resolve({ processedCount: 50 });
  assert.equal(modal.state.preview, null);

  const closedProps = modal.props;
  modal.props = { ...closedProps, isOpen: true };
  modal.componentDidUpdate(closedProps);
  modal.onPreviewPress();
  assert.equal(requests[2].url, '/importlist/12/history');
  assert.equal(requests[3].url, '/importlist/12/preview');
  assert.equal(modal.state.isPreviewing, true);
});
