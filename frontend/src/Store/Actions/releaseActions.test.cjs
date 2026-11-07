const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

function loadReleaseActions(createRequest) {
  const filePath = path.join(__dirname, 'releaseActions.js');
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [['@babel/preset-env', { targets: { node: 'current' }, modules: 'commonjs' }]]
  });

  let registeredHandlers = null;
  const enumValues = new Proxy({}, {
    get(_target, name) {
      return name;
    }
  });
  const defaultExport = (value) => ({ __esModule: true, default: value });
  const mocks = new Map([
    ['redux-actions', { createAction: (type) => (payload) => ({ type, payload }) }],
    ['Helpers/Props', {
      filterBuilderTypes: enumValues,
      filterBuilderValueTypes: enumValues,
      filterTypes: enumValues,
      sortDirections: enumValues
    }],
    ['Store/Actions/baseActions', { set: (payload) => ({ type: 'set', payload }) }],
    ['Store/thunks', {
      createThunk: (type) => (payload) => ({ type, payload }),
      handleThunks: (handlers) => {
        registeredHandlers = handlers;
      }
    }],
    ['Utilities/createAjaxRequest', defaultExport(createRequest)],
    ['Utilities/String/translate', defaultExport((key) => key)],
    ['./Creators/createHandleActions', defaultExport(() => () => {})],
    ['./Creators/Reducers/createSetClientSideCollectionFilterReducer', defaultExport(() => () => {})],
    ['./Creators/Reducers/createSetClientSideCollectionSortReducer', defaultExport(() => () => {})]
  ]);

  const module = { exports: {} };
  vm.runInNewContext(code, {
    module,
    exports: module.exports,
    require(moduleName) {
      assert.ok(mocks.has(moduleName), `Unexpected module: ${moduleName}`);
      return mocks.get(moduleName);
    }
  }, { filename: filePath });

  return { actions: module.exports, getHandlers: () => registeredHandlers };
}

test('interactive release search uses the response-wrapper route', () => {
  let requestOptions = null;
  const request = {
    done() {
      return this;
    },
    fail() {
      return this;
    }
  };
  const { actions, getHandlers } = loadReleaseActions((options) => {
    requestOptions = options;
    return { request, abortRequest: () => {} };
  });
  const dispatched = [];
  const fetchReleases = getHandlers()[actions.FETCH_RELEASES];

  fetchReleases(
    () => ({ releases: { bypassFilters: true } }),
    { bookId: 42 },
    (action) => dispatched.push(action)
  );

  assert.equal(requestOptions.url, '/release/search');
  assert.equal(requestOptions.traditional, true);
  assert.equal(requestOptions.data.bookId, 42);
  assert.equal(requestOptions.data.bypassFilters, true);
  assert.equal(dispatched[0].type, 'set');
});
