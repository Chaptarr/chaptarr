import PropTypes from 'prop-types';
import React, { Component } from 'react';
import keyboardShortcuts, { shortcuts } from 'Components/keyboardShortcuts';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AdvancedSettingsButton from './AdvancedSettingsButton';
import PendingChangesModal from './PendingChangesModal';

function getSaveResult(error) {
  if (!error) {
    return {
      wasSuccessful: true,
      hasWarning: false,
      hasError: false
    };
  }

  if (error.status !== 400) {
    return {
      wasSuccessful: false,
      hasWarning: false,
      hasError: true
    };
  }

  const failures = error.responseJSON;

  if (!Array.isArray(failures)) {
    return {
      wasSuccessful: false,
      hasWarning: false,
      hasError: true
    };
  }

  const hasWarning = failures.some((failure) => failure && failure.isWarning);
  const hasError = failures.some((failure) => failure && !failure.isWarning);

  return {
    wasSuccessful: false,
    hasWarning,
    hasError
  };
}

class SettingsToolbar extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._saveResultTimeout = null;

    this.state = {
      wasSuccessful: false,
      hasWarning: false,
      hasError: false
    };
  }

  componentDidMount() {
    this.props.bindShortcut(shortcuts.SAVE_SETTINGS.key, this.saveSettings, { isGlobal: true });
  }

  componentDidUpdate(prevProps) {
    const {
      isSaving,
      saveError
    } = this.props;

    if (!prevProps.isSaving && isSaving) {
      this.resetSaveResult();
    }

    if (prevProps.isSaving && !isSaving) {
      const saveResult = getSaveResult(saveError);

      this.setState(saveResult, () => {
        const {
          wasSuccessful,
          hasWarning,
          hasError
        } = saveResult;

        if (wasSuccessful || hasWarning || hasError) {
          this._saveResultTimeout = setTimeout(this.resetSaveResult, 3000);
        }
      });
    }
  }

  componentWillUnmount() {
    if (this._saveResultTimeout) {
      clearTimeout(this._saveResultTimeout);
    }
  }

  //
  // Control

  resetSaveResult = () => {
    if (this._saveResultTimeout) {
      clearTimeout(this._saveResultTimeout);
      this._saveResultTimeout = null;
    }

    this.setState({
      wasSuccessful: false,
      hasWarning: false,
      hasError: false
    });
  };

  saveSettings = (event) => {
    event.preventDefault();

    const {
      hasPendingChanges,
      onSavePress
    } = this.props;

    if (hasPendingChanges) {
      onSavePress();
    }
  };

  //
  // Render

  render() {
    const {
      advancedSettings,
      showSave,
      isSaving,
      hasPendingChanges,
      additionalButtons,
      hasPendingLocation,
      onSavePress,
      onConfirmNavigation,
      onCancelNavigation,
      onAdvancedSettingsPress
    } = this.props;

    const {
      wasSuccessful,
      hasWarning,
      hasError
    } = this.state;

    const showSaveResult = wasSuccessful || hasWarning || hasError;

    let saveIconName = icons.SAVE;
    let saveIconKind = kinds.DEFAULT;

    if (!isSaving && showSaveResult) {
      saveIconName = icons.CHECK_CIRCLE;
      saveIconKind = kinds.SUCCESS;

      if (hasWarning) {
        saveIconName = icons.WARNING;
        saveIconKind = kinds.WARNING;
      }

      if (hasError) {
        saveIconName = icons.DANGER;
        saveIconKind = kinds.DANGER;
      }
    }

    return (
      <PageToolbar>
        <PageToolbarSection>
          <AdvancedSettingsButton
            advancedSettings={advancedSettings}
            onAdvancedSettingsPress={onAdvancedSettingsPress}
          />

          {
            showSave &&
              <PageToolbarButton
                label={hasPendingChanges ? translate('HasPendingChangesSaveChanges') : translate('HasPendingChangesNoChanges')}
                iconName={saveIconName}
                iconKind={saveIconKind}
                isSpinning={isSaving}
                isDisabled={!hasPendingChanges}
                onPress={onSavePress}
              />
          }

          {
            additionalButtons
          }
        </PageToolbarSection>

        <PendingChangesModal
          isOpen={hasPendingLocation}
          onConfirm={onConfirmNavigation}
          onCancel={onCancelNavigation}
        />
      </PageToolbar>
    );
  }
}

SettingsToolbar.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  showSave: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool,
  saveError: PropTypes.object,
  hasPendingLocation: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool,
  additionalButtons: PropTypes.node,
  onSavePress: PropTypes.func,
  onAdvancedSettingsPress: PropTypes.func.isRequired,
  onConfirmNavigation: PropTypes.func.isRequired,
  onCancelNavigation: PropTypes.func.isRequired,
  bindShortcut: PropTypes.func.isRequired
};

SettingsToolbar.defaultProps = {
  showSave: true
};

export default keyboardShortcuts(SettingsToolbar);
