import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function BackupSettings(props) {
  const {
    advancedSettings,
    settings,
    onInputChange
  } = props;

  const {
    backupFolder,
    backupInterval,
    backupRetention,
    backupIncludeLogsAndCache
  } = settings;

  if (!advancedSettings) {
    return null;
  }

  return (
    <FieldSet legend={translate('Backups')}>
      <FormGroup
        advancedSettings={advancedSettings}
        isAdvanced={true}
      >
        <FormLabel>
          {translate('Folder')}
        </FormLabel>

        <FormInputGroup
          type={inputTypes.PATH}
          name="backupFolder"
          helpText={translate('BackupFolderHelpText')}
          onChange={onInputChange}
          {...backupFolder}
        />
      </FormGroup>

      <FormGroup
        advancedSettings={advancedSettings}
        isAdvanced={true}
      >
        <FormLabel>
          {translate('Interval')}
        </FormLabel>

        <FormInputGroup
          type={inputTypes.NUMBER}
          name="backupInterval"
          unit="days"
          min={0}
          max={7}
          helpText={translate('BackupIntervalHelpText')}
          helpTextWarning={backupInterval.value === 0 ? translate('ScheduledBackupsDisabled') : undefined}
          onChange={onInputChange}
          {...backupInterval}
        />
      </FormGroup>

      <FormGroup
        advancedSettings={advancedSettings}
        isAdvanced={true}
      >
        <FormLabel>
          {translate('Retention')}
        </FormLabel>

        <FormInputGroup
          type={inputTypes.NUMBER}
          name="backupRetention"
          unit="days"
          helpText={translate('BackupRetentionHelpText')}
          onChange={onInputChange}
          {...backupRetention}
        />
      </FormGroup>
      <FormGroup advancedSettings={advancedSettings} isAdvanced={true}>
        <FormLabel>{translate('BackupIncludeLogsAndCache')}</FormLabel>
        <FormInputGroup
          type={inputTypes.CHECK}
          name="backupIncludeLogsAndCache"
          helpText={translate('BackupIncludeLogsAndCacheHelp')}
          onChange={onInputChange}
          {...backupIncludeLogsAndCache}
        />
      </FormGroup>
    </FieldSet>
  );
}

BackupSettings.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default BackupSettings;
