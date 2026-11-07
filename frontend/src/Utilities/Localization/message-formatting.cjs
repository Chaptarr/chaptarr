const { pluralCategory } = require('./formatters.cjs');

function hasMessage(translations, key) {
  return Object.prototype.hasOwnProperty.call(translations, key);
}

function resolvePluralKey(key, count, translations, locale) {
  const amount = Number(count);
  const category = pluralCategory(amount, locale);
  const exactKey = `${key}.=${amount}`;
  const categoryKey = `${key}.${category}`;

  if (hasMessage(translations, exactKey)) {
    return exactKey;
  }

  if (hasMessage(translations, categoryKey)) {
    return categoryKey;
  }

  return `${key}.other`;
}

function resolveSelectKey(key, selection, translations) {
  const selectedKey = `${key}.${String(selection)}`;

  return hasMessage(translations, selectedKey) ?
    selectedKey :
    `${key}.other`;
}

function resolveMessageFallback(key, translations) {
  const otherKey = `${key}.other`;

  return translations[otherKey] || translations[key] || key;
}

module.exports = {
  resolveMessageFallback,
  resolvePluralKey,
  resolveSelectKey
};
