import translate from 'Utilities/String/translate';

const monitorOptions = [
  { key: 'all', get value() {
    return translate('AllBooks');
  } },
  { key: 'existing', get value() {
    return translate('ExistingBooks');
  } },
  { key: 'missing', get value() {
    return translate('MissingBooks');
  } },
  { key: 'none', get value() {
    return translate('None');
  } }
];

export default monitorOptions;
