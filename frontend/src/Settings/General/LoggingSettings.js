import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

const logLevelOptions = [
  { key: 'trace', value: 'Trace' },
  { key: 'debug', value: 'Debug' },
  { key: 'info', value: 'Info' },
  { key: 'warn', value: 'Warn' },
  { key: 'error', value: 'Error' },
  { key: 'fatal', value: 'Fatal' },
  { key: 'off', value: 'Off' }
];

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
          values={logLevelOptions}
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
