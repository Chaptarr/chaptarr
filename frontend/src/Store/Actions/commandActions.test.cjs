const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

function loadCommandActions(createRequest) {
  const filePath = path.join(__dirname, 'commandActions.js');
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [['@babel/preset-env', { targets: { node: 'current' }, modules: 'commonjs' }]]
  });

  const showMessage = (payload) => ({ type: 'showMessage', payload });
  const hideMessage = (payload) => ({ type: 'hideMessage', payload });
  const mockDefault = (value) => ({ __esModule: true, default: value });
  const mocks = new Map([
    ['redux-batched-actions', { batchActions: (actions) => ({ type: 'batch', actions }) }],
    ['Helpers/Props', { messageTypes: { INFO: 'info', ERROR: 'error', SUCCESS: 'success' } }],
    ['Store/thunks', {
      createThunk: (type) => (payload) => ({ type, payload }),
      handleThunks: () => {}
    }],
    ['Utilities/Command', { isSameCommand: () => false }],
    ['Utilities/createAjaxRequest', mockDefault(createRequest)],
    ['Utilities/String/translate', mockDefault((key) => key)],
    ['./appActions', { hideMessage, showMessage }],
    ['./baseActions', {
      removeItem: (payload) => payload,
      set: (payload) => payload,
      update: (payload) => payload,
      updateItem: (payload) => payload
    }],
    ['./Creators/createHandleActions', mockDefault(() => () => {})],
    ['./Creators/createRemoveItemHandler', mockDefault(() => () => {})]
  ]);

  const module = { exports: {} };
  vm.runInNewContext(code, {
    module,
    exports: module.exports,
    require(moduleName) {
      assert.ok(mocks.has(moduleName), `Unexpected module: ${moduleName}`);
      return mocks.get(moduleName);
    },
    setTimeout: () => ({ fakeTimer: true }),
    clearTimeout: () => {}
  }, { filename: filePath });

  return module.exports;
}

test('command queue failures show one general error and still reject for inline recovery', async() => {
  const requestError = new Error('offline');
  let requestOptions = null;
  const { executeCommandHelper } = loadCommandActions((options) => {
    requestOptions = options;
    return { request: Promise.reject(requestError) };
  });
  const actions = [];

  await assert.rejects(
    executeCommandHelper({ name: 'BookSearch', bookIds: [12] }, (action) => actions.push(action)),
    (error) => error === requestError
  );

  assert.deepEqual(actions.map((action) => action.type), ['showMessage']);
  assert.deepEqual(JSON.parse(JSON.stringify(actions[0].payload)), {
    id: 'command-start-error',
    name: 'CommandStartFailed',
    message: 'UnableToStartCommand',
    type: 'error',
    hideAfter: 8
  });
  assert.deepEqual(JSON.parse(requestOptions.data), { name: 'BookSearch', bookIds: [12] });
});

test('inline recovery can suppress the general queue error without sending the option to the server', async() => {
  let requestOptions = null;
  const { executeCommandHelper } = loadCommandActions((options) => {
    requestOptions = options;
    return { request: Promise.reject(new Error('offline')) };
  });
  const actions = [];

  await assert.rejects(executeCommandHelper({
    name: 'RssSync',
    suppressStartErrorMessage: true
  }, (action) => actions.push(action)));

  assert.deepEqual(actions, []);
  assert.deepEqual(JSON.parse(requestOptions.data), { name: 'RssSync' });
});

test('successful queueing dispatches the command and does not show an error', async() => {
  const queuedCommand = { id: 44, name: 'BookSearch' };
  const { executeCommandHelper } = loadCommandActions(() => ({ request: Promise.resolve(queuedCommand) }));
  const actions = [];

  await executeCommandHelper({ name: 'BookSearch', bookIds: [12] }, (action) => actions.push(action));

  assert.deepEqual(actions, [{ type: 'commands/addCommand', payload: queuedCommand }]);
});
