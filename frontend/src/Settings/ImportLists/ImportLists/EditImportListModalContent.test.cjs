const test = require('node:test');
const assert = require('node:assert/strict');
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
    const entry = {
      url,
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

function loadComponent(requests, hooks) {
  const filePath = path.join(__dirname, 'EditImportListModalContent.js');
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
    Fragment: 'Fragment',
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
  const kinds = { INFO: 'info', WARNING: 'warning', DANGER: 'danger', SUCCESS: 'success', PRIMARY: 'primary', DEFAULT: 'default' };
  const moduleMocks = new Map([
    ['react', { __esModule: true, default: React, ...hooks }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Helpers/Props', {
      icons: { INFO: 'info' },
      inputTypes: { ROOT_FOLDER_SELECT: 'root-folder-select', TEXT: 'text', CHECK: 'check', SELECT: 'select' },
      kinds,
      tooltipPositions: { RIGHT: 'right' }
    }],
    ['Helpers/Props/folderTypes', { FolderType: { Audiobook: 'audiobook', Ebook: 'ebook' } }],
    ['Utilities/createAjaxRequest', { __esModule: true, default: createRequestFactory(requests) }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }],
    ['./EditImportListModalContent.css', { __esModule: true, default: styles }]
  ]);

  const sandbox = {
    exports: {},
    require(moduleName) {
      if (moduleMocks.has(moduleName)) {
        return moduleMocks.get(moduleName);
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

function makeProps() {
  return {
    advancedSettings: false,
    isFetching: false,
    isSaving: false,
    isTesting: false,
    showMetadataProfile: true,
    item: {
      implementation: { value: 'HardcoverLibraryImportList' },
      implementationName: 'Hardcover Library',
      fields: []
    },
    onInputChange: () => null,
    onFieldChange: () => null,
    onModalClose: () => null,
    onSavePress: () => null,
    onTestPress: () => null,
    onAdvancedSettingsPress: () => null
  };
}

test('Hardcover list settings explain status failures, allow retry, and ignore stale results', () => {
  const requests = [];
  const harness = createHookHarness();
  const EditImportListModalContent = loadComponent(requests, {
    useEffect: harness.useEffect,
    useState: harness.useState
  });

  let output = harness.render(EditImportListModalContent, makeProps());
  assert.equal(requests.length, 1);
  assert.equal(requests[0].url, '/config/hardcover');
  assert.equal(findElements(output, (element) => element.props?.role === 'status').length, 1);

  requests[0].reject({ status: 503 });
  output = harness.render(EditImportListModalContent, makeProps());
  assert.equal(findElements(output, (element) => element.props?.role === 'alert').length, 1);

  const retryButton = findElements(output, (element) => element.type === 'Components/Link/Button')
    .find((element) => element.props.children === 'Retry');
  assert.ok(retryButton);
  retryButton.props.onPress();

  output = harness.render(EditImportListModalContent, makeProps());
  assert.equal(requests.length, 2);
  assert.equal(findElements(output, (element) => element.props?.role === 'status').length, 1);

  requests[0].resolve({ enabled: true, hasToken: true, username: 'stale account' });
  output = harness.render(EditImportListModalContent, makeProps());
  assert.equal(findElements(output, (element) => element.props?.children === 'HardcoverConnectedStatus').length, 0);

  requests[1].resolve({ enabled: true, hasToken: true, username: 'current account' });
  output = harness.render(EditImportListModalContent, makeProps());
  const content = findElements(output, () => true)
    .map((element) => element.props?.children)
    .flat(Infinity)
    .filter((value) => typeof value === 'string');

  assert.ok(content.includes('HardcoverConnectedStatus'));
  assert.ok(content.includes('current account'));
  assert.ok(!content.includes('Retry'));
});

test('Hardcover settings only call an enabled token connected', () => {
  const requests = [];
  const harness = createHookHarness();
  const EditImportListModalContent = loadComponent(requests, {
    useEffect: harness.useEffect,
    useState: harness.useState
  });

  harness.render(EditImportListModalContent, makeProps());
  requests[0].resolve({ enabled: false, hasToken: true, username: 'disabled account' });
  const output = harness.render(EditImportListModalContent, makeProps());

  assert.equal(findElements(output, (element) => element.type === 'Components/Link/Button')
    .some((element) => element.props.children === 'ConnectHardcover'), true);
  assert.equal(findElements(output, (element) => element.props?.children === 'HardcoverConnectedStatus').length, 0);
});
