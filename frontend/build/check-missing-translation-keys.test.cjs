const assert = require('node:assert/strict');
const test = require('node:test');
const { translationKeys } = require('./check-missing-translation-keys');

test('finds both translated ternary branches without treating the condition as a key', () => {
  const source = 'translate(eventType === \'split\' ? \'PlantedMissingSplit\' : \'PlantedMissingMerge\', { source: \'name\' })';
  assert.deepEqual(translationKeys(source, 'history.js'), ['PlantedMissingSplit', 'PlantedMissingMerge']);
});

test('finds literal alternatives inside expressions', () => {
  const source = 'translate(dynamicKey || \'Fallback\'); translate(\'Book\' + (selected ? \'Selected\' : \'Missing\')); translate(`Fixed`);';
  assert.deepEqual(translationKeys(source, 'view.tsx'), ['Fallback', 'BookSelected', 'BookMissing', 'Fixed']);
});

test('does not mistake values, comments or an unresolved key suffix for translations', () => {
  const source = '// translate(\'Comment\')\nconst text = "translate(\'Text\')"; translate(prefix + \'Suffix\'); translate(key, { label: \'Value\' });';
  assert.deepEqual(translationKeys(source, 'view.js'), []);
});

test('keeps backend literal translation coverage', () => {
  assert.deepEqual(translationKeys('localization.GetLocalizedString("KnownKey")', 'service.cs'), ['KnownKey']);
});
