const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');
const matching = require('./server-message-match.cjs');
const records = require('./server-messages.json');

function loadPresenter(translations) {
  const file = path.resolve(__dirname, '../String/translateServerMessage.js');
  const { code } = transformSync(fs.readFileSync(file, 'utf8'), {
    filename: file, babelrc: false, configFile: false,
    plugins: ['@babel/plugin-transform-modules-commonjs']
  });
  const sandbox = {
    exports: {},
    require(name) {
      if (name.endsWith('server-message-match.cjs')) {
        return matching;
      }
      if (name.endsWith('server-messages.json')) {
        return records;
      }
      if (name === 'Utilities/String/translate') {
        return {
          translateWithFallback(id, source, tokens) {
            return (translations[id] || source).replace(/\{([^{}]+)\}/g,
              (literal, key) => (Object.hasOwn(tokens, key) ? tokens[key] : literal));
          }
        };
      }
      throw new Error(`Unexpected presenter dependency: ${name}`);
    }
  };
  vm.runInNewContext(code, sandbox, { filename: file });
  return sandbox.exports.default;
}

test('real expanded state/path errors traverse the presentation helper and preserve data', () => {
  const present = loadPresenter({
    'server.message.m0a3d9d1876f5': 'حالة تنزيل غير معروفة: {value0}',
    'server.message.mc78af077e86e': 'يجب ألا يكون داخل المسار {value0}'
  });
  assert.equal(present('Unknown download state: pausedUP'), 'حالة تنزيل غير معروفة: \u2068pausedUP\u2069');
  const folder = '/opt/Book 01 - كتاب/{id}';
  assert.equal(present(`Must not be a descendant of '${folder}'`), `يجب ألا يكون داخل المسار \u2068${folder}\u2069`);
  assert.equal(present('A provider wrote arbitrary exception text'), 'A provider wrote arbitrary exception text');
});

test('literal Hardcover ID examples are never interpreted as dynamic placeholders', () => {
  const present = loadPresenter({
    'server.message.m50abcbe9b4b1': 'المعرّف المدعوم هو hc:{id}'
  });
  assert.equal(present('Only Hardcover series IDs are currently supported (expected hc:{id})'), 'المعرّف المدعوم هو hc:{id}');
});

test('presentation isolation preserves the complete mixed-script capture and the original stored message', () => {
  const present = loadPresenter({ 'server.message.m064135c5401a': 'المسار {value0} غير موجود' });
  const pathValue = '/mnt/Book 01 - كتاب [QA]/file.epub';
  const stored = { message: `Path '${pathValue}' does not exist` };
  const original = stored.message;
  assert.equal(present(stored.message), `المسار \u2068${pathValue}\u2069 غير موجود`);
  assert.equal(stored.message, original);
  assert.deepEqual(matching.matchServerMessage(stored.message, matching.compileServerMessageMatchers(records)).tokens, {
    value0: pathValue
  });
});

test('shared presentation helpers do not add nested isolation to an English fallback message', () => {
  const present = loadPresenter({});
  const raw = 'Path \'/books/Book 01 - كتاب\' does not exist';
  const once = present(raw);
  assert.equal(once, 'Path \'\u2068/books/Book 01 - كتاب\u2069\' does not exist');
  assert.equal(present(once), once);
});

test('new notification/grab/detail presentation resolves synthetic Arabic without changing stored data', () => {
  const source = "This torrent is already present in {value0} as '{value1}'.";
  const record = records.find((entry) => entry.source === source);
  const present = loadPresenter({ [record.id]: 'موجود في {value0}: {value1}' });
  const client = 'client/{value1}/العربية';
  const title = '/books/Book (2026)';
  const stored = { message: `This torrent is already present in ${client} as '${title}'.` };
  const original = JSON.stringify(stored);
  assert.equal(present(stored.message), `موجود في \u2068${client}\u2069: \u2068${title}\u2069`);
  assert.equal(JSON.stringify(stored), original);
  assert.equal(present('Downloading'), 'Downloading');
});
