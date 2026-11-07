const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { test } = require('node:test');
const { validateSourceEqualities } = require('./source-equality-audit.cjs');
const sourceFingerprint = crypto.createHash('sha256').update('Version\0Version').digest('hex');
const english = { Version: 'Version' };
const locales = [{ tag: 'fr', file: 'fr' }];
const record = { sourceFingerprint, category: 'native-equivalent' };
const check = (entry, catalog = english, tag = 'fr') => validateSourceEqualities({ version: 1, entries: { [tag]: { Version: entry } } }, english, locales, { fr: catalog });

test('a source-equal local word is explicit and source-bound', () => assert.deepEqual(check(record), []));
test('a stale source fingerprint cannot approve unchanged wording', () => assert.equal(check({ ...record, sourceFingerprint: 'stale' }).length, 1));
test('a translated or missing target invalidates the equality record', () => {
  assert.equal(check(record, { Version: 'Une version' }).length, 1);
  assert.equal(check(record, {}).length, 1);
});
test('equality categories and locales cannot silently become blanket waivers', () => {
  assert.equal(check({ ...record, category: 'reviewed' }).length, 1);
  assert.equal(check(record, english, 'unknown').length, 1);
  assert.equal(validateSourceEqualities({ version: 1, entries: [] }, english, locales, {}).length, 1);
});
