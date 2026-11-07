import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function getLogLevelOptions() {
  return [
    { key: 'trace', value: translate('Trace') },
    { key: 'debug', value: translate('Debug') },
    { key: 'info', value: translate('Information') },
    { key: 'warn', value: translate('Warning') },
    { key: 'error', value: translate('Error') },
    { key: 'fatal', value: translate('Fatal') },
    { key: 'off', value: translate('Off') }
  ];
}

function LoggingSettings(props) {
  const {
    settings,
    onInputChange
  } = props;

  const {
    logLevel
  } = settings;

  return (
    <FieldSet legend={translate('Logging')}>
      <FormGroup>
        <FormLabel>
          {translate('LogLevel')}
        </FormLabel>

        <FormInputGroup
          type={inputTypes.SELECT}
          name="logLevel"
          values={getLogLevelOptions()}
          helpTextWarning={logLevel.value === 'trace' ? translate('LogLevelvalueTraceTraceLoggingShouldOnlyBeEnabledTemporarily') : undefined}
          onChange={onInputChange}
          {...logLevel}
        />
      </FormGroup>
    </FieldSet>
  );
}

LoggingSettings.propTypes = {
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default LoggingSettings;
