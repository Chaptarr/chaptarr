const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { compileServerMessageMatchers } = require('../src/Utilities/Localization/server-message-match.cjs');

const targetFiles = [
  'es.json', 'zh_CN.json', 'hi.json', 'de.json', 'ja.json', 'fr.json',
  'pt_BR.json', 'ar.json', 'ko.json', 'zh_TW.json', 'vi.json', 'id.json'
];

function sha256(value) {
  return crypto.createHash('sha256').update(value, 'utf8').digest('hex');
}

function readSourceCallsite(root, callsite) {
  const relative = callsite.file;

  if (typeof relative !== 'string' || !relative || relative.includes('\\') ||
      path.posix.isAbsolute(relative) || path.win32.isAbsolute(relative) || relative.split('/').includes('..') ||
      path.posix.normalize(relative) !== relative) {
    throw new Error('Source callsite path must be a normalized relative path');
  }

  const realRoot = fs.realpathSync(root);
  const file = fs.realpathSync(path.resolve(realRoot, relative));

  if (!file.startsWith(`${realRoot}${path.sep}`)) {
    throw new Error('Source callsite escapes the repository');
  }

  if (!Number.isInteger(callsite.line) || callsite.line < 1 ||
      typeof callsite.sourceLine !== 'string' ||
      sha256(callsite.sourceLine) !== callsite.sourceLineSha256) {
    throw new Error('Invalid source callsite fingerprint');
  }

  const actual = fs.readFileSync(file, 'utf8').split(/\r?\n/)[callsite.line - 1]?.trim();

  if (actual !== callsite.sourceLine) {
    throw new Error('Source callsite changed after message review');
  }

  return actual;
}

function checkServerMessageCatalogs(root, { allowPending = false } = {}) {
  const readJson = (file) => JSON.parse(fs.readFileSync(path.join(root, file), 'utf8'));
  const inventory = readJson('docs/localization/server-message-sources.json');
  const runtime = readJson('frontend/src/Utilities/Localization/server-messages.json');
  const core = 'src/NzbDrone.Core/Localization/Core/';
  const english = readJson(`${core}en.json`);
  const catalogs = Object.fromEntries(targetFiles.map((file) => [file, readJson(core + file)]));
  const issues = [];
  let sourceCallsites = 0;
  let pendingSource = 0;
  let pendingTargets = 0;
  const tokens = (value) => (value.match(/\{[^{}]+\}/g) || []).sort();

  const expectedRuntime = inventory.records.map(({ id, source, parameters, patterns }) => ({
    id, source, parameters, patterns
  }));

  if (JSON.stringify(runtime) !== JSON.stringify(expectedRuntime)) {
    issues.push('Runtime server-message registry differs from the source-bound inventory');
  }

  try {
    compileServerMessageMatchers(runtime);
  } catch (error) {
    issues.push(error.message);
  }

  for (const record of inventory.records) {
    if (record.sourceFingerprint !== sha256(`${record.id}\0${record.source}`)) {
      issues.push(`${record.id}: source fingerprint is stale`);
    }

    if (english[record.id] !== record.source) {
      if (english[record.id] === undefined && allowPending) {
        pendingSource++;
      } else {
        issues.push(`${record.id}: English catalog source is missing or changed`);
      }
    }

    for (const callsite of record.callsites) {
      sourceCallsites++;

      try {
        readSourceCallsite(root, callsite);
      } catch (error) {
        issues.push(`${record.id}: ${callsite.file}:${callsite.line}: ${error.message}`);
      }
    }

    for (const [file, catalog] of Object.entries(catalogs)) {
      const value = catalog[record.id];

      if (typeof value !== 'string' || !value.trim()) {
        if (allowPending) {
          pendingTargets++;
        } else {
          issues.push(`${file}/${record.id}: direct translation is missing`);
        }

        continue;
      }

      if (JSON.stringify(tokens(value)) !== JSON.stringify(tokens(record.source))) {
        issues.push(`${file}/${record.id}: placeholder or literal-brace mismatch`);
      }

      for (const literal of record.protectedLiterals || []) {
        if (!value.includes(literal)) {
          issues.push(`${file}/${record.id}: protected literal was changed: ${literal}`);
        }
      }
    }
  }

  for (const excluded of inventory.excluded) {
    for (const callsite of excluded.callsites) {
      try {
        readSourceCallsite(root, callsite);
      } catch (error) {
        issues.push(`${callsite.file}:${callsite.line}: excluded source changed: ${error.message}`);
      }
    }
  }

  const callers = readJson('docs/localization/server-message-callers.json');
  for (const callsite of callers.callsites) {
    try {
      readSourceCallsite(root, callsite);
    } catch (error) {
      issues.push(`${callsite.file}:${callsite.line}: presentation caller changed: ${error.message}`);
    }
  }
  if (callers.callsites.length !== callers.counts.callsites ||
      new Set(callers.callsites.map((callsite) => callsite.file)).size !== callers.counts.files) {
    issues.push('Server-message presentation caller counts are stale');
  }

  const excludedCallsites = inventory.excluded.reduce((count, record) => count + record.callsites.length, 0);
  const sourceTemplates = new Set(inventory.records.flatMap((record) => record.sourceTemplates)).size;
  const callerIdentity = (callsite) => `${callsite.file}\0${callsite.line}\0${callsite.sourceLineSha256}`;
  const uniqueCallsites = new Set(inventory.records.flatMap((record) => record.callsites.map(callerIdentity))).size;

  if (sourceCallsites !== (inventory.counts.sourceBindings || inventory.counts.sourceCallsites) ||
      uniqueCallsites !== inventory.counts.sourceCallsites ||
      inventory.records.length !== inventory.counts.translatableMessages ||
      inventory.excluded.length !== inventory.counts.excludedTemplates ||
      uniqueCallsites + excludedCallsites !== inventory.counts.sourceOccurrences ||
      sourceTemplates + inventory.excluded.length !== inventory.counts.originalTemplates) {
    issues.push('Server-message inventory coverage counts are stale');
  }

  return {
    issues,
    messages: inventory.records.length,
    sourceCallsites: uniqueCallsites,
    sourceBindings: sourceCallsites,
    locales: targetFiles.length,
    excludedTemplates: inventory.excluded.length,
    pendingSource,
    pendingTargets
  };
}

if (require.main === module) {
  const root = path.resolve(__dirname, '../..');
  const result = checkServerMessageCatalogs(root, { allowPending: process.argv.includes('--allow-pending') });

  if (result.issues.length) {
    console.error(result.issues.join('\n'));
    process.exitCode = 1;
  } else {
    console.log(JSON.stringify(result));
  }
}

module.exports = { checkServerMessageCatalogs, readSourceCallsite, sha256 };
