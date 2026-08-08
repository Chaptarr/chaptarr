import translate from 'Utilities/String/translate';

const importModeOptions = [
  { key: 'move', value: () => translate('MoveFiles') },
  { key: 'copy', value: () => translate('HardlinkCopyFiles') }
];

export default importModeOptions;
