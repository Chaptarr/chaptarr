import translate from 'Utilities/String/translate';

const monitorOptions = [
  { key: 'all', get value() {
    return translate('AllBooks');
  } },
  { key: 'missing', get value() {
    return translate('MissingBooks');
  } },
  { key: 'existing', get value() {
    return translate('BooksWithFiles');
  } },
  { key: 'none', get value() {
    return translate('None');
  } }
];

const monitorOptionKeys = new Set(monitorOptions.map(({ key }) => key));

export function normalizeMonitorOption(value) {
  const normalized = (value ?? '').toString().trim().toLowerCase();
  return monitorOptionKeys.has(normalized) ? normalized : 'none';
}

export function resolveMonitorOptionValue(value, fallbackValue) {
  return normalizeMonitorOption(value ?? fallbackValue);
}

export default monitorOptions;
