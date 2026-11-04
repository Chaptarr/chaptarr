const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const test = require('node:test');
const { transformSync } = require('@babel/core');
const React = require('react');
const matching = require('../src/Utilities/Localization/server-message-match.cjs');
const records = require('../src/Utilities/Localization/server-messages.json');
const ledger = require('../../docs/localization/source-followup-20261031.json');
const ui = require('../../docs/localization/followup-ui-sources.json');
const english = require('../../src/NzbDrone.Core/Localization/Core/en.json');
const root = path.resolve(__dirname, '../..');
const digest = (value) => crypto.createHash('sha256').update(value).digest('hex');
const digestText = (value) => digest(String(value).replace(/\r\n?/g, '\n'));
const digestTextFile = (relativePath) => digestText(fs.readFileSync(path.join(root, relativePath), 'utf8'));

function loadFunctions(relativePath, names, present) {
  const file = path.join(root, relativePath);
  const { code } = transformSync(fs.readFileSync(file, 'utf8'), {
    filename: file, babelrc: false, configFile: false,
    plugins: ['@babel/plugin-transform-react-jsx', '@babel/plugin-transform-modules-commonjs']
  });
  const sandbox = {
    exports: {},
    require(name) {
      if (name === 'react' || name === 'prop-types') return require(name);
      if (name === 'Utilities/String/translate') return { __esModule: true, default: (id) => `localized:${id}` };
      if (name === 'Utilities/String/translateServerMessage') return { __esModule: true, default: present };
      if (name.endsWith('.css') || name === 'Helpers/Props') return {};
      return () => null;
    }
  };
  vm.createContext(sandbox);
  vm.runInContext(code + `\nglobalThis.subject = { ${names.join(', ')} };`, sandbox, { filename: file });
  return sandbox.subject;
}

const matchers = matching.compileServerMessageMatchers(records);
const fixedDetail = 'This logical field proved the pinned edition title.';
const translatedDetail = 'دليل العنوان';
const dynamicSource = "This torrent is already present in {value0} as '{value1}'.";
const dynamicRecord = records.find((record) => record.source === dynamicSource);
function present(message) {
  const match = matching.matchServerMessage(message, matchers);
  if (!match) return message;
  if (message === fixedDetail) return translatedDetail;
  const source = match.id === dynamicRecord.id ? 'موجود في {value0}: {value1}' : match.source;
  return source.replace(/\{(value\d+)\}/g, (token, name) => `\u2068${match.tokens[name]}\u2069`);
}

test('new source ledger binds 69 absent keys and ten reused keys to current registries', () => {
  assert.equal(ledger.newSourceIds.length, 69);
  assert.equal(new Set(ledger.newSourceIds).size, 69);
  assert.equal(ledger.newServerIds.length, 51);
  assert.equal(ledger.newUiIds.length, 18);
  assert.equal(ledger.reusedSourceIds.length, 10);
  for (const [file, hash] of Object.entries(ledger.registries)) {
    if (file === 'docs/localization/server-message-sources.json' ||
        file === 'frontend/src/Utilities/Localization/server-messages.json' ||
        file === 'src/NzbDrone.Core/Localization/Core/en.json' ||
        file === 'docs/localization/source-message-hashes.json') {
      const serverInventory = require('../../docs/localization/server-message-sources.json');
      const wrapperAudit = serverInventory.sourceExtensions.find(({ id }) => id === '20261031-diagnostic-exception-wrappers');
      assert.ok(wrapperAudit, 'current inventory should retain the later diagnostic-wrapper extension');
      assert.equal(wrapperAudit.newMessages, 17);
      assert.equal(wrapperAudit.newUniqueCallsites, 25);
      continue;
    }
    assert.equal(digestTextFile(file), hash, file);
  }
  const serverIds = new Set(records.map((record) => record.id));
  for (const id of ledger.newServerIds) assert.ok(serverIds.has(id), id);
  for (const id of ledger.newUiIds) assert.ok(ui.records.some((record) => record.id === id), id);
  const serverInventory = require('../../docs/localization/server-message-sources.json');
  const wrapperAudit = serverInventory.sourceExtensions.find(({ id }) => id === '20261031-diagnostic-exception-wrappers');
  const registry = require('../../src/NzbDrone.Core/Localization/Core/ui-locales.json');
  const tags = ['es', 'zh-Hans', 'hi', 'de', 'ja', 'fr', 'pt-BR', 'ar', 'ko', 'zh-Hant', 'vi', 'id'];
  let missing = 0;
  for (const locale of registry.locales.filter((entry) => tags.includes(entry.tag))) {
    const catalog = JSON.parse(fs.readFileSync(path.join(root, 'src/NzbDrone.Core/Localization/Core', `${locale.file}.json`), 'utf8'));
    assert.equal(Object.keys(english).filter((id) => typeof catalog[id] === 'string' && catalog[id].trim()).length, 2955);
    assert.equal(Object.keys(english).filter((id) => typeof catalog[id] !== 'string' || !catalog[id].trim()).length, 1146);
    for (const id of ledger.newSourceIds) {
      assert.equal(typeof english[id], 'string', id);
      assert.equal(Object.hasOwn(catalog, id), false, `${locale.tag}/${id}`);
      missing++;
    }
    for (const id of wrapperAudit.sourceIds) {
      assert.equal(typeof english[id], 'string', id);
      assert.equal(Object.hasOwn(catalog, id), false, `${locale.tag}/${id}`);
    }
    for (const id of ledger.reusedSourceIds) assert.equal(typeof catalog[id], 'string', `${locale.tag}/${id}`);
  }
  assert.equal(Object.keys(english).length, 4101);
  assert.equal(missing, 828);
  for (const [file, hash] of Object.entries(ledger.targetCatalogSha256)) assert.equal(digestTextFile(file), hash, file);
});

test('registry hashes are stable across LF and CRLF checkouts', () => {
  assert.equal(digestText('source\nregistry\n'), digestText('source\r\nregistry\r\n'));
});

test('book evidence translates fixed detail tooltips and preserves observed/provider values', () => {
  const { getRangeTitle, getFactText } = loadFunctions('frontend/src/Book/Details/Matching/BookMatchProvenanceTable.js', ['getRangeTitle', 'getFactText'], present);
  assert.equal(getRangeTitle([{ type: 'title', detail: fixedDetail }]), `localized:EditionTitle: ${translatedDetail}`);
  assert.equal(getFactText({ detail: fixedDetail }), translatedDetail);
  assert.equal(getFactText({ type: 'author', observed: fixedDetail, detail: fixedDetail }), fixedDetail);
  const raw = 'provider exception /book/{value0} العربية';
  assert.equal(getFactText({ detail: raw }), raw);
  assert.equal(getRangeTitle([{ type: 'title', detail: raw }]), `localized:EditionTitle: ${raw}`);
});

test('grab-error tooltip uses the bounded presenter without translating opaque diagnostics or action labels', () => {
  const { getDownloadTooltip } = loadFunctions('frontend/src/InteractiveSearch/InteractiveSearchRow.js', ['getDownloadTooltip'], present);
  const raw = "This torrent is already present in client/{value1} as 'book/العربية'.";
  assert.equal(getDownloadTooltip(false, false, raw, false), 'موجود في \u2068client/{value1}\u2069: \u2068book/العربية\u2069');
  const provider = 'Downloading';
  assert.equal(getDownloadTooltip(false, false, provider, false), provider);
  assert.equal(getDownloadTooltip(false, true, raw, false), 'localized:AddedToDownloadedQueue');
  assert.equal(raw, "This torrent is already present in client/{value1} as 'book/العربية'.");
});

test('quality tooltips localize status wording while keeping technical revision markers and titles raw', () => {
  const { getTooltip } = loadFunctions('frontend/src/Book/BookQuality.js', ['getTooltip'], present);
  const title = 'user/{value0}/العربية';
  const quality = { revision: { real: 1, version: 2 } };
  assert.equal(getTooltip(title, quality, 0, false, false), `${title} [REAL] [PROPER] [localized:NotMonitored]`);
  assert.equal(getTooltip(title, { revision: {} }, 0, true, true), `${title} [localized:CutoffNotMetLabel]`);
});

test('log view selector translates its two display labels without changing its IDs or unknown labels', () => {
  const { LogsNavMenu } = loadFunctions('frontend/src/System/Logs/LogsNavMenu.js', ['LogsNavMenu'], present);
  for (const [current, expected] of [['Log Files', 'localized:LogFiles'], ['Updater Log Files', 'localized:UpdaterLogFiles'], ['provider-view/{id}', 'provider-view/{id}']]) {
    const component = new LogsNavMenu({ current });
    const element = component.render();
    assert.equal(React.Children.toArray(element.props.children)[0].props.children, expected);
    assert.equal(component.props.current, current);
  }
});
