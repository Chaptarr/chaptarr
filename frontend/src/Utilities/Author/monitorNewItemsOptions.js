import translate from 'Utilities/String/translate';

const monitorNewItemsOptions = [
  { key: 'all', get value() {
    return translate('AllNewBooks');
  } },
  { key: 'new', get value() {
    return translate('FutureReleases');
  } },
  { key: 'none', get value() {
    return translate('None');
  } }
];

const monitorNewItemsOptionKeys = new Set(monitorNewItemsOptions.map(({ key }) => key));

export function normalizeMonitorNewItemsOption(value) {
  const normalized = (value ?? '').toString().trim().toLowerCase();
  return monitorNewItemsOptionKeys.has(normalized) ? normalized : 'none';
}

export function resolveMonitorNewItemsOptionValue(value, fallbackValue) {
  return normalizeMonitorNewItemsOption(value ?? fallbackValue);
}

export default monitorNewItemsOptions;
