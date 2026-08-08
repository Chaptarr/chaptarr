import PropTypes from 'prop-types';
import React from 'react';
import PageToolbarStatusButton from 'Components/Page/Toolbar/PageToolbarStatusButton';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function AdvancedSettingsButton(props) {
  const {
    advancedSettings,
    onAdvancedSettingsPress,
    showLabel
  } = props;

  return (
    <PageToolbarStatusButton
      label={advancedSettings ? translate('HideAdvanced') : translate('ShowAdvanced')}
      iconName={icons.ADVANCED_SETTINGS}
      isEnabled={advancedSettings}
      enabledTitle={translate('ShownClickToHide')}
      disabledTitle={translate('HiddenClickToShow')}
      showLabel={showLabel}
      onPress={onAdvancedSettingsPress}
    />
  );
}

AdvancedSettingsButton.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  showLabel: PropTypes.bool.isRequired
};

AdvancedSettingsButton.defaultProps = {
  showLabel: true
};

export default AdvancedSettingsButton;
