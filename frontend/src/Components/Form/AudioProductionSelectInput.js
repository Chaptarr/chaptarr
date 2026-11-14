import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';
import SelectInput from './SelectInput';

function AudioProductionSelectInput({ value, bulk = false, isDisabled = false, onChange }) {
  let selectedValue = 'auto';
  if (value != null) {
    selectedValue = value ? 'dramatized' : 'standard';
  }
  if (bulk) {
    selectedValue = 'select';
  }
  const values = [
    ...(bulk ? [{ key: 'select', value: translate('RecordingType'), isDisabled: true }] : []),
    { key: 'auto', value: translate('RecordingTypeAutomatic') },
    { key: 'standard', value: translate('RecordingTypeStandard') },
    { key: 'dramatized', value: translate('RecordingTypeDramatized') }
  ];

  return (
    <SelectInput
      name="audioProductionOverride"
      aria-label={translate('RecordingType')}
      value={selectedValue}
      values={values}
      isDisabled={isDisabled}
      onChange={({ value: selected }) => onChange({ value: selected === 'auto' ? null : selected === 'dramatized' })}
    />
  );
}

AudioProductionSelectInput.propTypes = {
  value: PropTypes.bool,
  bulk: PropTypes.bool,
  isDisabled: PropTypes.bool,
  onChange: PropTypes.func.isRequired
};

export default AudioProductionSelectInput;
