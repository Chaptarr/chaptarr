const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');
const formatHelpers = require('./BookRelatedFormats.cjs');

const propType = () => propType;
propType.isRequired = propType;
propType.oneOf = () => propType;

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
    return callback();
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
      },
      reject(error) {
        callbacks.fail(error);
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

function loadComponent(requests) {
  const filePath = path.join(__dirname, 'BookRelatedFormats.js');
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
    get: (_target, key) => key.toString()
  });
  const moduleMocks = new Map([
    ['react', { __esModule: true, default: React, Component }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Components/Book/BookFormatActions', { __esModule: true, default: 'BookFormatActions' }],
    ['Components/Link/Button', { __esModule: true, default: 'Button' }],
    ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
    ['Search/Book/AddNewBookModal', { __esModule: true, default: 'AddNewBookModal' }],
    ['Utilities/createAjaxRequest', { __esModule: true, default: createRequestFactory(requests) }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['./BookRelatedFormats.cjs', formatHelpers],
    ['./BookRelatedFormats.css', { __esModule: true, default: styles }]
  ]);
  const sandbox = {
    exports: {},
    require(moduleName) {
      if (!moduleMocks.has(moduleName)) {
        throw new Error(`Unexpected import: ${moduleName}`);
      }

      return moduleMocks.get(moduleName);
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
  if (Array.isArray(element)) {
    return element.map(getText).join(' ');
  }

  if (typeof element === 'string' || typeof element === 'number') {
    return String(element);
  }

  return element?.props ? getText(element.props.children) : '';
}

function makeBookRelatedFormats(requests) {
  const BookRelatedFormats = loadComponent(requests);
  const component = new BookRelatedFormats({
    bookId: 7,
    mediaType: 'ebook',
    title: 'Dune',
    book: { id: 7, mediaType: 'ebook', title: 'Dune', foreignBookId: 'hc:123' },
    author: { foreignAuthorId: 'hc:456', authorName: 'Frank Herbert' }
  });

  component.componentDidMount();
  return component;
}

test('related formats show loading, retry a failed lookup, and reveal actions after success', () => {
  const requests = [];
  const component = makeBookRelatedFormats(requests);
  const loadingView = component.render();
  const status = findElements(loadingView, (element) => element.props?.role === 'status')[0];

  assert.ok(status);
  assert.equal(getText(status), 'CheckingRelatedFormats');
  assert.equal(findElements(loadingView, (element) => element.type === 'BookFormatActions').length, 0);

  requests[0].reject({ status: 503 });
  const failureView = component.render();
  const alert = findElements(failureView, (element) => element.props?.role === 'alert')[0];
  const retryButton = findElements(failureView, (element) => {
    return element.type === 'Button' && getText(element) === 'Retry';
  })[0];

  assert.ok(alert);
  assert.match(getText(alert), /LoadingRelatedFormatsFailed/);
  assert.equal(getText(retryButton), 'Retry');

  retryButton.props.onPress();
  assert.equal(requests.length, 2);
  assert.equal(requests[1].url, '/book/7/related-formats');

  requests[1].resolve({ formats: [{ bookId: 8, mediaType: 'audiobook', title: 'Dune' }] });
  const successView = component.render();
  const actions = findElements(successView, (element) => element.type === 'BookFormatActions')[0];

  assert.ok(actions);
  assert.equal(actions.props.localAudiobookBooks.length, 1);
  assert.equal(actions.props.localEbookBooks.length, 1);
});
