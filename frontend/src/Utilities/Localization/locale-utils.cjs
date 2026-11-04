const RTL_LANGUAGES = new Set([
  'ar', 'ckb', 'dv', 'fa', 'he', 'khw', 'ks', 'ku', 'ps', 'sd', 'ug', 'ur', 'yi'
]);
const RTL_SCRIPTS = new Set(['adlm', 'arab', 'hebr', 'nkoo', 'rohg', 'syrc', 'thaa']);
const RTL_DOCUMENT_LAYOUT_REVIEW_REQUIRED = new Set(['ar']);

function normalizeLocaleTag(locale) {
  return String(locale || 'en').replace(/_/g, '-').trim() || 'en';
}

function getDirectionForLocale(locale) {
  const normalized = normalizeLocaleTag(locale);
  const parts = normalized.split('-');
  const script = parts.find((part) => /^[A-Za-z]{4}$/.test(part));

  if (script) {
    return RTL_SCRIPTS.has(script.toLowerCase()) ? 'rtl' : 'ltr';
  }

  return RTL_LANGUAGES.has(parts[0].toLowerCase()) ? 'rtl' : 'ltr';
}

function getDocumentDirectionForLocale(locale) {
  const language = normalizeLocaleTag(locale).split('-')[0].toLowerCase();

  if (RTL_DOCUMENT_LAYOUT_REVIEW_REQUIRED.has(language)) {
    return 'ltr';
  }

  return getDirectionForLocale(locale);
}

function getSelectableUiLocales(locales) {
  return locales.filter((locale) => locale.uiSelectable === true);
}

function getUiLocaleOptionLabel(locale) {
  const nativeName = String(locale.name || '').trim();
  const englishName = String(locale.englishName || '').trim();

  if (locale.tag === 'en' || !englishName || nativeName === englishName) {
    return nativeName || englishName;
  }

  return `${nativeName} — ${englishName}`;
}

function applyDocumentLocale(locale, root) {
  const element = root || (typeof document !== 'undefined' ? document.documentElement : null);

  if (!element) {
    return null;
  }

  const tag = normalizeLocaleTag(locale);
  element.lang = tag;
  element.dir = getDocumentDirectionForLocale(tag);

  return { tag, direction: element.dir };
}

function getCurrentLocale(root) {
  const element = root || (typeof document !== 'undefined' ? document.documentElement : null);
  const documentLocale = element && element.lang;
  const browserLocale = typeof navigator !== 'undefined' && navigator.language;

  return normalizeLocaleTag(documentLocale || browserLocale || 'en');
}

module.exports = {
  applyDocumentLocale,
  getCurrentLocale,
  getDirectionForLocale,
  getDocumentDirectionForLocale,
  getSelectableUiLocales,
  getUiLocaleOptionLabel,
  normalizeLocaleTag
};
