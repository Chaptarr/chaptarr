const localeRegistry = require('../../../../src/NzbDrone.Core/Localization/Core/ui-locales.json');
const localeUtils = require('./locale-utils.cjs');

const allUiLocales = Object.freeze(
  localeRegistry.locales.map((locale) => Object.freeze(locale))
);

export const SUPPORTED_UI_LOCALES = Object.freeze(
  localeUtils.getSelectableUiLocales(allUiLocales)
);
export const SUPPORTED_UI_LOCALE_IDS = new Set(SUPPORTED_UI_LOCALES.map((locale) => locale.id));

const uiLocaleById = new Map(allUiLocales.map((locale) => [locale.id, locale]));

export function getUiLocaleById(id) {
  return uiLocaleById.get(Number(id));
}

export const getUiLocaleOptionLabel = localeUtils.getUiLocaleOptionLabel;
export const normalizeLocaleTag = localeUtils.normalizeLocaleTag;
export const getDirectionForLocale = localeUtils.getDirectionForLocale;
export const getDocumentDirectionForLocale = localeUtils.getDocumentDirectionForLocale;
export const applyDocumentLocale = localeUtils.applyDocumentLocale;
export const getLocale = localeUtils.getCurrentLocale;
