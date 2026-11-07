import createAjaxRequest from 'Utilities/createAjaxRequest';
import intlFormatters from 'Utilities/Localization/formatters.cjs';
import { applyDocumentLocale, getLocale } from 'Utilities/Localization/locale';
import messageFormatting from 'Utilities/Localization/message-formatting.cjs';

const { formatNumber } = intlFormatters;
const { resolveMessageFallback, resolvePluralKey, resolveSelectKey } = messageFormatting;

function getTranslations() {
  return createAjaxRequest({
    global: false,
    dataType: 'json',
    url: '/localization',
  }).request;
}

let translations: Record<string, string> = {};

export async function fetchTranslations(): Promise<boolean> {
  try {
    const data = await getTranslations();
    translations = data.Strings || {};

    if (typeof data.Locale === 'string') {
      applyDocumentLocale(data.Locale);
    }

    return true;
  } catch {
    return false;
  }
}

type TranslationValue = string | number | boolean;
type TranslationTokens = Record<string, TranslationValue>;

function interpolate(template: string, tokens: TranslationTokens) {
  const formattedTokens: Record<string, TranslationValue> = { ...tokens };

  Object.keys(tokens).forEach((token, index) => {
    if (!Object.prototype.hasOwnProperty.call(formattedTokens, String(index))) {
      formattedTokens[String(index)] = tokens[token];
    }
  });

  formattedTokens.appName = 'Chaptarr';

  return template.replace(/\{([a-z0-9]+?)\}/gi, (match, tokenMatch) => {
    if (!Object.prototype.hasOwnProperty.call(formattedTokens, tokenMatch)) {
      return match;
    }

    const value = formattedTokens[tokenMatch];

    if (typeof value === 'number' && Number.isFinite(value)) {
      return formatNumber(value, {}, getLocale());
    }

    return String(value);
  });
}

function translateWithDefault(
  key: string,
  fallback: string,
  tokens: TranslationTokens,
  missingKey = key
) {
  const { isProduction = true } = window.Chaptarr;

  if (!isProduction && !Object.prototype.hasOwnProperty.call(translations, key)) {
    console.warn(`Missing translation for key: ${missingKey}`);
  }

  const translation = translations[key] || fallback;

  return interpolate(translation, tokens);
}

export function translateWithFallback(
  key: string,
  fallback: string,
  tokens: TranslationTokens = {}
) {
  return translateWithDefault(key, fallback, tokens);
}

export function translatePlural(
  key: string,
  count: number,
  tokens: TranslationTokens = {}
) {
  const fallbackKey = `${key}.other`;
  const selectedKey = resolvePluralKey(key, count, translations, getLocale());

  return translateWithDefault(
    selectedKey,
    resolveMessageFallback(key, translations),
    { ...tokens, count },
    fallbackKey
  );
}

export function translateSelect(
  key: string,
  selection: string | number | boolean,
  tokens: TranslationTokens = {}
) {
  const fallbackKey = `${key}.other`;
  const keyToUse = resolveSelectKey(key, selection, translations);

  return translateWithDefault(
    keyToUse,
    resolveMessageFallback(key, translations),
    tokens,
    fallbackKey
  );
}

export default function translate(
  key: string,
  tokens: TranslationTokens = {}
) {
  return translateWithDefault(key, key, tokens);
}
