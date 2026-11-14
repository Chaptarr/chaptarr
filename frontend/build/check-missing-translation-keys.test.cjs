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
