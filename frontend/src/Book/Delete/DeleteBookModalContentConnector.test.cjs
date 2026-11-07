const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const filePath = path.join(__dirname, 'DeleteBookModalContentConnector.js');
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

const PropTypes = new Proxy({}, {
  get() {
    return propType;
  }
});

function createRequestFactory(requests) {
  return function createAjaxRequest({ url }) {
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

    requests.push({
      url,
      resolve(data) {
        callbacks.done(data);
      }
    });

    return {
      request,
      abortRequest() {
        callbacks.fail({ aborted: true });
      }
    };
  };
}

test('delete confirmation ignores sibling details from the previously selected book', () => {
  const requests = [];
  const mocks = new Map([
    ['connected-react-router', { push: (destination) => ({ type: 'router/push', payload: destination }) }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['react', { __esModule: true, default: React, Component }],
    ['react-redux', { connect: () => (component) => component }],
    ['reselect', { createSelector: () => () => ({}) }],
    ['Store/Actions/bookActions', { deleteBook: () => ({ type: 'book/delete' }) }],
    ['Store/Selectors/createBookSelector', { __esModule: true, default: () => () => ({}) }],
    ['Utilities/createAjaxRequest', { __esModule: true, default: createRequestFactory(requests) }],
    ['./DeleteBookModalContent', { __esModule: true, default: 'DeleteBookModalContent' }]
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
  const DeleteBookModalContentConnector = sandbox.exports.default;
  const noop = () => null;
  const props = {
    bookId: 11,
    authorId: 1,
    onModalClose: noop,
    deleteBook: noop,
    push: noop
  };
  const instance = new DeleteBookModalContentConnector(props);

  instance.componentDidMount();
  assert.equal(requests[0].url, '/book/11/siblings');
  requests[0].resolve({
    currentBook: { id: 11 },
    siblingMediaType: 'ebook',
    siblings: [{ id: 12 }],
    audiobookCount: 1,
    ebookCount: 1
  });

  instance.props = { ...props, bookId: 22 };
  const beforeUpdateView = instance.render();
  assert.equal(beforeUpdateView.props.siblingBook, null);
  assert.equal(beforeUpdateView.props.currentBookDeleteInfo, null);

  instance.componentDidUpdate(props);
  assert.equal(requests[1].url, '/book/22/siblings');
  requests[0].resolve({ currentBook: { id: 11 }, siblings: [{ id: 12 }] });
  assert.equal(instance.state.siblingInfoBookId, 22);
  assert.equal(instance.state.currentBookDeleteInfo, null);

  requests[1].resolve({
    currentBook: { id: 22 },
    siblingMediaType: 'audiobook',
    siblings: [{ id: 23 }],
    audiobookCount: 1,
    ebookCount: 1
  });

  const currentView = instance.render();
  assert.equal(currentView.props.siblingBook.siblings[0].id, 23);
  assert.equal(currentView.props.currentBookDeleteInfo.id, 22);
});
