import PropTypes from 'prop-types';
import React from 'react';
import SettingsToolbar from 'Settings/SettingsToolbar';

const noop = () => {};

function MediaManagementToolbarStub({ advancedSettings, hasPendingChanges, onSavePress }) {
  return (
    <SettingsToolbar
      advancedSettings={advancedSettings}
      isSaving={false}
      saveError={null}
      hasPendingLocation={false}
      hasPendingChanges={hasPendingChanges}
      onSavePress={onSavePress}
      onAdvancedSettingsPress={noop}
      onConfirmNavigation={noop}
      onCancelNavigation={noop}
    />
  );
}

MediaManagementToolbarStub.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  onSavePress: PropTypes.func.isRequired
};

export default MediaManagementToolbarStub;
