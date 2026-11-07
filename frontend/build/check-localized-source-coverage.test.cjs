const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fieldDefinitionText } = require('./check-localized-source-coverage.cjs');

test('field coverage includes multiline labels and punctuation inside quoted help', () => {
  assert.deepEqual(fieldDefinitionText('[FieldDefinition(0,\n Label = "provider.field.label", HelpText = "provider.field.help",\n HelpTextWarning = "warning.with.parentheses")]'), [
    { kind: 'Label', id: 'provider.field.label' },
    { kind: 'HelpText', id: 'provider.field.help' },
    { kind: 'HelpTextWarning', id: 'warning.with.parentheses' }
  ]);
  assert.deepEqual(fieldDefinitionText('[FieldDefinition(0, HelpText = "Literal (text) \\\"example\\\"")]'), [
    { kind: 'HelpText', id: 'Literal (text) "example"' }
  ]);
});
test('new literal help text is visible to the catalog-ID audit', () => {
  assert.deepEqual(fieldDefinitionText('[FieldDefinition(0, Label = "AlreadyKnown", HelpText = "New untranslated text")]')[1], {
    kind: 'HelpText', id: 'New untranslated text'
  });
});
test('unclosed attributes fail instead of silently reducing the coverage count', () => {
  assert.throws(() => fieldDefinitionText('[FieldDefinition(0, Label = "unterminated"'), /Unterminated/);
});

test('technical-marker exclusions require current caller evidence and unchanged catalog literals', () => {
  const fs = require('node:fs');
  const os = require('node:os');
  const path = require('node:path');
  const { sha256 } = require('./check-server-message-catalogs.cjs');
  const { checkSourceRegistry } = require('./check-localized-source-coverage.cjs');
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-marker-proof-'));
  const write = (file, value) => {
    const target = path.join(root, file);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, JSON.stringify(value));
  };
  try {
    const core = 'src/NzbDrone.Core/Localization/Core/';
    write(core + 'en.json', { Proper: 'Proper' });
    write(core + 'fr.json', { Proper: 'Proper' });
    write(core + 'ui-locales.json', { locales: [{ tag: 'fr', file: 'fr' }] });
    const sourceLine = 'qualityString += " Proper";';
    fs.writeFileSync(path.join(root, 'src/Marker.cs'), sourceLine + '\n');
    const file = 'docs/localization/outbound-message-sources.json';
    const inventory = {
      records: [], counts: { messages: 0, sourceCallsites: 0, excludedCallsites: 1 },
      excluded: [{
        category: 'protected-technical-literal', reason: 'Release revision marker',
        referenceId: 'Proper', expectedValue: 'Proper',
        callsites: [{ file: 'src/Marker.cs', line: 1, sourceLine, sourceLineSha256: sha256(sourceLine) }]
      }]
    };
    write(file, inventory);
    assert.deepEqual(checkSourceRegistry(root, file).issues, []);
    write(core + 'fr.json', { Proper: 'Correct' });
    assert.match(checkSourceRegistry(root, file).issues.join('\n'), /protected technical marker changed/);
    write(core + 'fr.json', { Proper: 'Proper' });
    fs.writeFileSync(path.join(root, 'src/Marker.cs'), 'qualityString += " Changed";\n');
    assert.match(checkSourceRegistry(root, file).issues.join('\n'), /excluded caller changed/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('loading fallback data stays source-bound and cannot redirect reads outside the repository', () => {
  const fs = require('node:fs');
  const os = require('node:os');
  const path = require('node:path');
  const { checkLoadingMessages } = require('./check-localized-source-coverage.cjs');
  const outer = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-loading-data-'));
  const root = path.join(outer, 'repo');
  const inventoryFile = path.join(root, 'docs/localization/loading-message-sources.json');
  const expectedFile = 'frontend/src/Components/Loading/loading-messages.json';
  const dataFile = path.join(root, expectedFile);
  const message = { id: 'ui.loading.example', source: 'Original loading copy' };
  try {
    fs.mkdirSync(path.dirname(inventoryFile), { recursive: true });
    const inventory = { records: [message], runtimeDataFile: '../outside.json' };
    fs.writeFileSync(inventoryFile, JSON.stringify(inventory));
    assert.match(checkLoadingMessages(root).issues.join('\n'), /fixed repository path/);
    inventory.runtimeDataFile = expectedFile;
    fs.writeFileSync(inventoryFile, JSON.stringify(inventory));
    fs.mkdirSync(path.dirname(dataFile), { recursive: true });
    const outside = path.join(outer, 'outside.json');
    fs.writeFileSync(outside, 'This must not be parsed');
    fs.symlinkSync(outside, dataFile);
    assert.match(checkLoadingMessages(root).issues.join('\n'), /escapes the repository/);
    fs.unlinkSync(dataFile);
    fs.writeFileSync(dataFile, JSON.stringify([message]));
    assert.deepEqual(checkLoadingMessages(root).issues, []);
    fs.writeFileSync(dataFile, JSON.stringify([{ ...message, source: 'Unreviewed replacement' }]));
    assert.match(checkLoadingMessages(root).issues.join('\n'), /differs from its source inventory/);
  } finally {
    fs.rmSync(outer, { recursive: true, force: true });
  }
});

test('repository source inventories match current callers and static schema metadata during translation preparation', () => {
  const path = require('node:path');
  const { checkLocalizedSourceCoverage } = require('./check-localized-source-coverage.cjs');
  const { checkServerMessageCatalogs } = require('./check-server-message-catalogs.cjs');
  const root = path.resolve(__dirname, '../..');
  // This draft milestone permits the explicitly queued target cells; final acceptance
  // uses check-localization-sources without allowPending after generation/review.
  assert.deepEqual(checkLocalizedSourceCoverage(root, { allowPending: true }).issues, []);
  assert.deepEqual(checkServerMessageCatalogs(root, { allowPending: true }).issues, []);
});

test('source evidence recognizes JavaScript single-quoted IDs while refusing unreferenced IDs', () => {
  const fs = require('node:fs');
  const os = require('node:os');
  const path = require('node:path');
  const { sha256 } = require('./check-server-message-catalogs.cjs');
  const { checkSourceRegistry } = require('./check-localized-source-coverage.cjs');
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'chaptarr-ui-source-'));
  const core = 'src/NzbDrone.Core/Localization/Core/';
  const inventoryFile = 'docs/localization/followup-ui-sources.json';
  const write = (file, value) => {
    fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true });
    fs.writeFileSync(path.join(root, file), JSON.stringify(value));
  };
  try {
    write(core + 'en.json', { 'ui.test.title': 'Example title' });
    write(core + 'ui-locales.json', { locales: [] });
    const sourceFile = 'frontend/src/Example.js';
    fs.mkdirSync(path.dirname(path.join(root, sourceFile)), { recursive: true });
    for (const quote of ["'", '"']) {
      const sourceLine = `translate(${quote}ui.test.title${quote});`;
      fs.writeFileSync(path.join(root, sourceFile), sourceLine + '\n');
      const record = { id: 'ui.test.title', source: 'Example title', sourceFingerprint: sha256('ui.test.title\0Example title'), callsites: [{ file: sourceFile, line: 1, sourceLine, sourceLineSha256: sha256(sourceLine) }] };
      const inventory = { records: [record], counts: { messages: 1, sourceCallsites: 1 }, excluded: [] };
      write(inventoryFile, inventory);
      assert.deepEqual(checkSourceRegistry(root, inventoryFile).issues, []);
      record.callsites[0].sourceLine = 'unrelatedCall();';
      record.callsites[0].sourceLineSha256 = sha256(record.callsites[0].sourceLine);
      fs.writeFileSync(path.join(root, sourceFile), record.callsites[0].sourceLine + '\n');
      write(inventoryFile, inventory);
      assert.match(checkSourceRegistry(root, inventoryFile).issues.join('\n'), /does not reference the stable ID/);
    }
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
