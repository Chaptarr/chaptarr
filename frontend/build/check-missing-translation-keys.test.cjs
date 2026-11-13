const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
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

const expectedSearchWarnings = {
  ca: 'Esteu segur que voleu realitzar una cerca massiva de llibres per {count} llibres?',
  de: 'Sind Sie sicher, dass Sie eine Massensuche für {count} Bücher starten wollen?',
  el: 'Είστε βέβαιοι ότι θέλετε να πραγματοποιήσετε μαζική αναζήτηση βιβλίων για {count} βιβλία;',
  es: '¿Estás seguro que quieres llevar a cabo una búsqueda masiva para {count} libros?',
  fi: 'Haluatko varmasti etsiä kaikkia {count} kirjaa?',
  fr: 'Voulez-vous vraiment effectuer une recherche groupée de {count} livres ?',
  hu: 'Biztosan tömeges könyvkeresést kíván végrehajtani {count} könyveknél?',
  pt_BR: 'Tem certeza de que deseja executar pesquisa em massa para {count} livros?',
  zh_CN: '您确定要对{count}本书进行批量书籍搜索吗？'
};

for (const [locale, expected] of Object.entries(expectedSearchWarnings)) {
  test(`mass book search keeps the existing ${locale} translation`, () => {
    const translations = JSON.parse(fs.readFileSync(path.join(__dirname, `../../src/NzbDrone.Core/Localization/Core/${locale}.json`), 'utf8'));
    assert.equal(translations.SearchAuthorBooksConfirm, expected);
  });
}

test('finds the keys passed through count/summary helpers and conditional backend translations', () => {
  const source = 'AddCountMessage(parts, "RetagCount", count); AddSummary(messages, "RetagEbookPass", batch); GetLocalizedString(flag ? "First" : "Second", values);';
  assert.deepEqual(translationKeys(source, 'service.cs'), ['RetagCount', 'RetagEbookPass', 'First', 'Second']);
});

test('finds translated map values without treating unrelated values as keys', () => {
  const source = 'const messages = { skipped: "SkipReason", failed: "FailedReason" }; const other = { value: "NotAKey" }; translate(messages[reason] || "Fallback");';
  assert.deepEqual(translationKeys(source, 'preview.js'), ['SkipReason', 'FailedReason', 'Fallback']);
});

test('member access reads only the named property', () => {
  const source = 'const column = { name: "title", width: "10px", label: "Title" }; translate(column.label); translate(column["label"]);';
  assert.deepEqual(translationKeys(source, 'view.js'), ['Title']);
});

test('backend conditions may contain nested parentheses', () => {
  assert.deepEqual(translationKeys('GetLocalizedString(Foo(a) ? "AA" : "BB")', 'service.cs'), ['AA', 'BB']);
});

test('backend argument boundaries exclude following unrelated strings and calls', () => {
  const source = 'GetLocalizedString("One"); Log(flag ? "decoy" : "value"); GetLocalizedString(Foo(a, b) ? "Two" : "Three");';
  assert.deepEqual(translationKeys(source, 'service.cs'), ['One', 'Two', 'Three']);
});

test('object spreads and methods do not crash literal map lookup', () => {
  const source = 'const messages = { ...base, a: "Known", method() { return "NotAKey"; } }; translate(messages.a); translate(messages[reason]);';
  assert.deepEqual(translationKeys(source, 'view.js'), ['Known']);
});

test('computed keys are not mistaken for identifier names', () => {
  const source = 'const messages = { [key]: "Dynamic", ["a"]: "Known" }; translate(messages.a); translate(messages[reason]);';
  assert.deepEqual(translationKeys(source, 'view.js'), ['Known', 'Dynamic']);
});

test("computed identifier key does not resolve as a literal property", () => {
  assert.deepEqual(translationKeys('const m = { [a]: "X" }; translate(m.a);', "view.js"), []);
});
