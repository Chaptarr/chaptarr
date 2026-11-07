const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const propType = () => propType;
propType.isRequired = propType;
propType.oneOf = () => propType;
propType.arrayOf = () => propType;
propType.oneOfType = () => propType;

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

function createHookHarness() {
  const states = [];
  const effects = [];
  let stateIndex = 0;
  let effectIndex = 0;
  let pendingEffects = [];

  function useState(initialValue) {
    const index = stateIndex++;

    if (!(index in states)) {
      states[index] = initialValue;
    }

    return [states[index], (update) => {
      states[index] = typeof update === 'function' ? update(states[index]) : update;
    }];
  }

  function useEffect(callback, dependencies) {
    const index = effectIndex++;
    const activeEffect = effects[index];
    const dependenciesChanged = !activeEffect || dependencies.some((dependency, dependencyIndex) => {
      return dependency !== activeEffect.dependencies[dependencyIndex];
    });

    if (dependenciesChanged) {
      pendingEffects.push({ index, callback, dependencies });
    }
  }

  function render(component, props) {
    stateIndex = 0;
    effectIndex = 0;
    pendingEffects = [];

    const output = component(props);
    const nextEffects = pendingEffects;
    pendingEffects = [];

    nextEffects.forEach(({ index, callback, dependencies }) => {
      if (effects[index]?.cleanup) {
        effects[index].cleanup();
      }

      // Keep the effect cleanup for the next dependency change.
      // eslint-disable-next-line callback-return
      const cleanup = callback();
      effects[index] = { dependencies, cleanup };
    });

    return output;
  }

  return { render, states, useEffect, useState };
}

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
      reject(xhr) {
        callbacks.fail(xhr);
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

function loadComponent(relativePath, moduleMocks, hooks) {
  const filePath = path.join(__dirname, relativePath);
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
    ['react', { __esModule: true, default: React, ...hooks }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['./BookEditionCatalog.css', { __esModule: true, default: styles }],
    ...moduleMocks
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
  return sandbox.exports.default;
}

function createHooks(harness) {
  return {
    useEffect: harness.useEffect,
    useState: harness.useState
  };
}

test('edition catalog ignores late results and clears the prior book while switching IDs', () => {
  const requests = [];
  const harness = createHookHarness();
  const moduleMocks = [
    ['Components/Label', { __esModule: true, default: 'Label' }],
    ['Components/Link/Button', { __esModule: true, default: 'Button' }],
    ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
    ['Components/Table/Cells/TableRowCell', { __esModule: true, default: 'TableRowCell' }],
    ['Components/Table/Table', { __esModule: true, default: 'Table' }],
    ['Components/Table/TableBody', { __esModule: true, default: 'TableBody' }],
    ['Components/Table/TableRow', { __esModule: true, default: 'TableRow' }],
    ['Utilities/createAjaxRequest', { __esModule: true, default: createRequestFactory(requests) }]
  ];
  const BookEditionCatalog = loadComponent(
    './Editions/BookEditionCatalog.js',
    moduleMocks,
    createHooks(harness)
  );

  harness.render(BookEditionCatalog, { bookId: 101, mediaType: 'ebook' });
  assert.equal(requests[0].url, '/book/101/editions/catalog?mediaType=ebook');

  const nextBookView = harness.render(BookEditionCatalog, { bookId: 202, mediaType: 'audiobook' });
  assert.equal(nextBookView.type, 'LoadingIndicator');
  assert.equal(requests[1].url, '/book/202/editions/catalog?mediaType=audiobook');

  requests[0].resolve([{ editionId: 101, title: 'Old edition' }]);
  assert.equal(harness.states[0].items.length, 0);

  requests[1].resolve([{ editionId: 202, title: 'Current edition' }]);
  assert.equal(harness.states[0].bookId, 202);
  assert.equal(harness.states[0].items[0].title, 'Current edition');
});

test('narrator comparison ignores a late result from the previously viewed series', () => {
  const requests = [];
  const harness = createHookHarness();
  const moduleMocks = [
    ['Components/Label', { __esModule: true, default: 'Label' }],
    ['Components/Link/Button', { __esModule: true, default: 'Button' }],
    ['Components/Loading/LoadingIndicator', { __esModule: true, default: 'LoadingIndicator' }],
    ['Utilities/createAjaxRequest', { __esModule: true, default: createRequestFactory(requests) }],
    ['./SeriesNarratorMatrix.css', { __esModule: true, default: styles }]
  ];
  const SeriesNarratorMatrix = loadComponent(
    '../../Series/Details/SeriesNarratorMatrix.js',
    moduleMocks,
    createHooks(harness)
  );
  const oldMatrix = { books: [], totalBooks: 1, narrators: [{ name: 'Old narrator' }] };
  const currentMatrix = { books: [], totalBooks: 1, narrators: [{ name: 'Current narrator' }] };

  harness.render(SeriesNarratorMatrix, { title: 'Old series', authorId: 10 });
  assert.match(requests[0].url, /title=Old%20series&authorId=10/);

  const nextSeriesView = harness.render(SeriesNarratorMatrix, { title: 'New series', authorId: 20 });
  assert.equal(nextSeriesView.type, 'LoadingIndicator');
  assert.match(requests[1].url, /title=New%20series&authorId=20/);

  requests[0].resolve(oldMatrix);
  assert.equal(harness.states[0].matrix, null);

  requests[1].resolve(currentMatrix);
  assert.equal(harness.states[0].title, 'New series');
  assert.equal(harness.states[0].matrix.narrators[0].name, 'Current narrator');
});
