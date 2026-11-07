const fs = require('node:fs');
const path = require('node:path');
const { readSourceCallsite, sha256 } = require('./check-server-message-catalogs.cjs');

function fieldDefinitionText(text) {
  const attributes = [];
  const start = /\[FieldDefinition\(/g;
  let match;
  while ((match = start.exec(text))) {
    let cursor = start.lastIndex;
    let depth = 1;
    let quoted = false;
    let escaped = false;
    for (; cursor < text.length; cursor++) {
      const char = text[cursor];
      if (quoted) {
        if (escaped) escaped = false;
        else if (char === '\\') escaped = true;
        else if (char === '"') quoted = false;
      } else if (char === '"') quoted = true;
      else if (char === '(') depth++;
      else if (char === ')' && --depth === 0) break;
    }
    if (depth) throw new Error('Unterminated FieldDefinition attribute');
    attributes.push(text.slice(start.lastIndex, cursor));
  }
  return attributes.flatMap((attribute) => Array.from(
    attribute.matchAll(/\b(Label|HelpText|HelpTextWarning)\s*=\s*("(?:[^"\\]|\\.)*")/g),
    (entry) => ({ kind: entry[1], id: JSON.parse(entry[2]) })
  ));
}

function checkSourceRegistry(root, file, { allowPending = false } = {}) {
  const inventory = JSON.parse(fs.readFileSync(path.join(root, file), 'utf8'));
  const core = path.join(root, 'src/NzbDrone.Core/Localization/Core');
  const english = JSON.parse(fs.readFileSync(path.join(core, 'en.json'), 'utf8'));
  const locales = JSON.parse(fs.readFileSync(path.join(core, 'ui-locales.json'), 'utf8')).locales;
  const tags = ['ar', 'de', 'es', 'fr', 'hi', 'id', 'ja', 'ko', 'pt-BR', 'vi', 'zh-Hans', 'zh-Hant'];
  const targets = locales.filter((locale) => tags.includes(locale.tag)).map((locale) => ({
    tag: locale.tag,
    catalog: JSON.parse(fs.readFileSync(path.join(core, locale.file + '.json'), 'utf8'))
  }));
  const issues = [];
  const ids = new Set();
  let sourceCallsites = 0;
  let pendingTargets = 0;
  const referencesId = (line, id) => line.includes(JSON.stringify(id)) || line.includes(`'${id}'`);
  const tokens = (text) => (text.match(/\{[^{}]+\}/g) || []).sort();
  for (const record of inventory.records) {
    if (ids.has(record.id) || english[record.id] !== record.source ||
        sha256(record.id + '\0' + record.source) !== record.sourceFingerprint) {
      issues.push(record.id + ': duplicate ID or stale source/fingerprint');
    }
    ids.add(record.id);
    if (!record.callsites.length) issues.push(record.id + ': source coverage has no callers');
    for (const callsite of record.callsites) {
      sourceCallsites++;
      try {
        readSourceCallsite(root, callsite);
        if (!referencesId(callsite.sourceLine, record.id)) {
          // A linked formatting source is allowed only alongside a proven ID caller.
          if (!record.callsites.some((site) => referencesId(site.sourceLine, record.id))) {
            issues.push(record.id + ': source caller does not reference the stable ID');
          }
        }
      } catch (error) {
        issues.push(record.id + ': ' + error.message);
      }
    }
    for (const { tag, catalog } of targets) {
      const value = catalog[record.id];
      if (typeof value !== 'string' || !value.trim()) {
        pendingTargets++;
        if (!allowPending) issues.push(tag + '/' + record.id + ': direct translation missing');
        continue;
      }
      if (JSON.stringify(tokens(value)) !== JSON.stringify(tokens(record.source))) {
        issues.push(tag + '/' + record.id + ': placeholder or literal-brace mismatch');
      }
      for (const literal of record.protectedLiterals || []) {
        if (!value.includes(literal)) issues.push(tag + '/' + record.id + ': protected literal changed: ' + literal);
      }
    }
  }
  let excludedCallsites = 0;
  for (const exclusion of inventory.excluded || []) {
    if (!exclusion.reason || !['protected-technical-literal', 'dynamic-metadata-field'].includes(exclusion.category)) {
      issues.push(file + ': exclusions need an explicit data/literal category and reason');
    }
    for (const callsite of exclusion.callsites) {
      excludedCallsites++;
      try {
        readSourceCallsite(root, callsite);
      } catch (error) {
        issues.push(file + ': excluded caller changed: ' + error.message);
      }
    }
    if (exclusion.referenceId) {
      if (english[exclusion.referenceId] !== exclusion.expectedValue ||
          targets.some(({ catalog }) => catalog[exclusion.referenceId] !== exclusion.expectedValue)) {
        issues.push(exclusion.referenceId + ': protected technical marker changed');
      }
    }
  }
  if (excludedCallsites !== (inventory.counts.excludedCallsites || 0)) {
    issues.push(file + ': excluded caller counts changed');
  }
  const expectedCallsites = inventory.counts.sourceCallsites ?? inventory.counts.mappedCallsites;
  const expectedMessages = inventory.counts.messages ?? inventory.counts.mappedDistinctSources;
  if (sourceCallsites !== expectedCallsites || ids.size !== expectedMessages) {
    issues.push(file + ': coverage counts changed');
  }
  return { issues, messages: ids.size, sourceCallsites, excludedCallsites, pendingTargets };
}

function checkFieldDefinitions(root) {
  const core = 'src/NzbDrone.Core/Localization/Core/en.json';
  const english = JSON.parse(fs.readFileSync(path.join(root, core), 'utf8'));
  const issues = [];
  let total = 0;
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name);
      if (entry.isDirectory() && !entry.name.includes('.Test') && !['bin', 'obj'].includes(entry.name)) walk(file);
      else if (entry.isFile() && file.endsWith('.cs')) {
        for (const field of fieldDefinitionText(fs.readFileSync(file, 'utf8'))) {
          total++;
          if (!Object.hasOwn(english, field.id)) {
            issues.push(path.relative(root, file) + ': ' + field.kind + ' is not a catalog ID: ' + field.id);
          }
        }
      }
    }
  };
  walk(path.join(root, 'src'));
  const inventory = JSON.parse(fs.readFileSync(path.join(root, 'docs/localization/provider-field-sources.json'), 'utf8'));
  if (total !== inventory.counts.total) issues.push('Provider attribute coverage count changed: ' + total);
  return { issues, fields: total };
}

function checkLoadingMessages(root) {
  const inventory = JSON.parse(fs.readFileSync(path.join(root, 'docs/localization/loading-message-sources.json'), 'utf8'));
  const expectedFile = 'frontend/src/Components/Loading/loading-messages.json';
  if (inventory.runtimeDataFile !== expectedFile) {
    return { issues: ['Loading message data must use its fixed repository path'], messages: 0 };
  }
  const runtimeFile = fs.realpathSync(path.join(root, expectedFile));
  if (!runtimeFile.startsWith(fs.realpathSync(root) + path.sep)) {
    return { issues: ['Loading message data escapes the repository'], messages: 0 };
  }
  const messages = JSON.parse(fs.readFileSync(runtimeFile, 'utf8'));
  const expected = inventory.records.map(({ id, source }) => ({ id, source }));
  return {
    issues: JSON.stringify(messages) === JSON.stringify(expected) ? [] : ['Loading message fallback data differs from its source inventory'],
    messages: messages.length
  };
}

function checkLocalizedSourceCoverage(root, options = {}) {
  const results = {
    provider: checkSourceRegistry(root, 'docs/localization/provider-field-sources.json', options),
    outbound: checkSourceRegistry(root, 'docs/localization/outbound-message-sources.json', options),
    options: checkSourceRegistry(root, 'docs/localization/provider-option-sources.json', options),
    loading: checkSourceRegistry(root, 'docs/localization/loading-message-sources.json', options),
    followupUi: checkSourceRegistry(root, 'docs/localization/followup-ui-sources.json', options),
    loadingData: checkLoadingMessages(root),
    attributes: checkFieldDefinitions(root)
  };
  return { ...results, issues: Object.values(results).flatMap((result) => result.issues) };
}

if (require.main === module) {
  const result = checkLocalizedSourceCoverage(path.resolve(__dirname, '../..'), {
    allowPending: process.argv.includes('--allow-pending')
  });
  if (result.issues.length) {
    console.error(result.issues.join('\n'));
    process.exitCode = 1;
  } else console.log(JSON.stringify(result));
}
module.exports = { fieldDefinitionText, checkLocalizedSourceCoverage, checkSourceRegistry, checkLoadingMessages };
