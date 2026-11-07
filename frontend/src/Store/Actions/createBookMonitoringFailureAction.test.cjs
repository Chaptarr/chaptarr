const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const filePath = path.join(__dirname, 'createBookMonitoringFailureAction.js');
const source = fs.readFileSync(filePath, 'utf8');
const { code } = transformSync(source, {
  filename: filePath,
  babelrc: false,
  configFile: false,
  presets: [
    [
      '@babel/preset-env',
      {
        targets: {
          browsers: ['last 2 versions']
        },
        loose: true,
        modules: 'commonjs'
      }
    ]
  ]
});

const sandbox = {
  exports: {},
  require(moduleName) {
    if (moduleName === 'Utilities/Object/getErrorMessage') {
      return {
        __esModule: true,
        default(xhr, fallback) {
          return xhr?.responseJSON?.message || fallback;
        }
      };
    }

    if (moduleName === 'Utilities/String/translate') {
      return {
        __esModule: true,
        default(key) {
          return `translated:${key}`;
        }
      };
    }

    if (moduleName === './appActions') {
      return {
        showMessage(payload) {
          return { type: 'app/showMessage', payload };
        }
      };
    }

    return module.require(moduleName);
  }
};

vm.runInNewContext(code, sandbox, { filename: filePath });

test('book monitoring failures create a visible error notification with the server message', () => {
  const createAction = sandbox.exports.default;
  const action = createAction({ responseJSON: { message: 'The update was rejected.' } });

  assert.equal(action.type, 'app/showMessage');
  assert.equal(action.payload.name, 'BookMonitoringUpdateFailed');
  assert.equal(action.payload.message, 'The update was rejected.');
  assert.equal(action.payload.type, 'error');
  assert.equal(action.payload.hideAfter, 10);
  assert.match(action.payload.id, /^book-monitoring-update-failed-\d+-\d+$/);
});

test('book monitoring failures use the localized fallback when the server gives no message', () => {
  const createAction = sandbox.exports.default;
  const action = createAction({ status: 500 });

  assert.equal(action.payload.message, 'translated:UnableToUpdateBookMonitoring');
});
