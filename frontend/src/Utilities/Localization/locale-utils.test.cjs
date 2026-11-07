const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const {
  applyDocumentLocale,
  getCurrentLocale,
  getDocumentDirectionForLocale,
  getDirectionForLocale,
  getSelectableUiLocales,
  getUiLocaleOptionLabel,
  normalizeLocaleTag
} = require('./locale-utils.cjs');

const localizationRoot = path.resolve(__dirname, '../../../../src/NzbDrone.Core/Localization/Core');
const localeRegistry = JSON.parse(fs.readFileSync(path.join(localizationRoot, 'ui-locales.json'), 'utf-8'));
const englishCatalog = JSON.parse(fs.readFileSync(path.join(localizationRoot, 'en.json'), 'utf-8'));
const sourceEqualities = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../../../docs/localization/source-equalities.json'), 'utf-8'));
const localesByTag = new Map(localeRegistry.locales.map((locale) => [locale.tag, locale]));
const localeCatalogs = new Map();

function getResolvedCatalog(locale, seen = new Set()) {
  assert.equal(seen.has(locale.tag), false, `locale fallback cycle: ${locale.tag}`);
  seen.add(locale.tag);

  const resolved = locale.fallback ?
    getResolvedCatalog(localesByTag.get(locale.fallback), seen) :
    { values: {}, sourceLocaleById: {} };
  const catalog = localeCatalogs.get(locale.tag) || JSON.parse(
    fs.readFileSync(path.join(localizationRoot, `${locale.file}.json`), 'utf-8')
  );
  localeCatalogs.set(locale.tag, catalog);

  return {
    values: { ...resolved.values, ...catalog },
    sourceLocaleById: {
      ...resolved.sourceLocaleById,
      ...Object.fromEntries(Object.keys(catalog).map((id) => [id, locale.tag]))
    }
  };
}

function hasNonEnglishUiValues(locale) {
  const { values, sourceLocaleById } = getResolvedCatalog(locale);

  return Object.entries(values).some(([id, target]) => {
    const source = englishCatalog[id];
    if (typeof source !== 'string' || typeof target !== 'string') {
      return false;
    }

    if (target !== source) {
      return true;
    }

    const sourceLocale = sourceLocaleById[id];
    const equality = sourceEqualities.entries[sourceLocale]?.[id];
    const fingerprint = crypto.createHash('sha256').update(`${id}\0${source}`).digest('hex');

    return equality?.category === 'native-equivalent' && equality.sourceFingerprint === fingerprint;
  });
}

test('normalizes locale tags and derives direction from language or script', () => {
  assert.equal(normalizeLocaleTag('pt_BR'), 'pt-BR');
  assert.equal(getDirectionForLocale('ar-EG'), 'rtl');
  assert.equal(getDirectionForLocale('fa'), 'rtl');
  assert.equal(getDirectionForLocale('he-IL'), 'rtl');
  assert.equal(getDirectionForLocale('az-Arab'), 'rtl');
  assert.equal(getDirectionForLocale('az-Latn'), 'ltr');
  assert.equal(getDirectionForLocale('zh-Hant'), 'ltr');
  assert.equal(getDirectionForLocale('en-US'), 'ltr');
  assert.equal(getDocumentDirectionForLocale('ar-EG'), 'ltr');
  assert.equal(getDocumentDirectionForLocale('fa'), 'rtl');
});

test('applies lang and direction to the document root', () => {
  const root = { lang: '', dir: '' };
  assert.deepEqual(applyDocumentLocale('fa_IR', root), { tag: 'fa-IR', direction: 'rtl' });
  assert.equal(root.lang, 'fa-IR');
  assert.equal(root.dir, 'rtl');
  assert.equal(getCurrentLocale(root), 'fa-IR');
});

test('keeps Arabic language metadata while gating whole-document RTL', () => {
  const root = { lang: '', dir: '' };
  assert.equal(getDirectionForLocale('ar'), 'rtl');
  assert.deepEqual(applyDocumentLocale('ar', root), { tag: 'ar', direction: 'ltr' });
  assert.equal(root.lang, 'ar');
  assert.equal(root.dir, 'ltr');
});

test('shows only locales with actual non-English catalog values', () => {
  const selectable = getSelectableUiLocales(localeRegistry.locales);
  const hidden = localeRegistry.locales.filter((locale) => !locale.uiSelectable).map((locale) => locale.tag);

  assert.deepEqual(hidden, ['az', 'kk', 'ms', 'uz']);
  assert.equal(selectable.length, 33);

  for (const locale of localeRegistry.locales) {
    assert.equal(typeof locale.englishName, 'string');
    assert.notEqual(locale.englishName.trim(), '');
    assert.equal(locale.uiSelectable, locale.tag === 'en' || hasNonEnglishUiValues(locale), locale.tag);
  }
});

test('labels language choices with native and English names', () => {
  const arabic = localesByTag.get('ar');
  const spanish = localesByTag.get('es');
  const english = localesByTag.get('en');

  assert.equal(getUiLocaleOptionLabel(arabic), 'العربية — Arabic');
  assert.equal(getUiLocaleOptionLabel(spanish), 'español — Spanish');
  assert.equal(getUiLocaleOptionLabel(english), 'English');
});
