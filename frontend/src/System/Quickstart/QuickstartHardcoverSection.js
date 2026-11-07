import PropTypes from 'prop-types';
import React, { Component } from 'react';
import translate from 'Utilities/String/translate';
import HardcoverApiKeyModal from './HardcoverApiKeyModal';
import styles from './Quickstart.css';

class QuickstartHardcoverSection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isApiKeyModalOpen: false,
      connectionStatus: null
    };
  }

  //
  // Listeners

  onButtonPress = () => {
    this.setState({ isApiKeyModalOpen: true });
  };

  onApiKeyModalClose = () => {
    this.setState({ isApiKeyModalOpen: false });
  };

  onConnectionSuccess = () => {
    this.setState({ connectionStatus: 'tested' });
    this.props.onConnectionTestSuccess?.();
    // Mark this section as interacted when connection succeeds
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'hardcover' });
    }

  };

  onConnectionFailure = (error) => {
    this.setState({ connectionStatus: 'failed' });
    this.props.onConnectionTestFailure?.(error);
  };

  //
  // Render

  render() {
    const {
      isHardcoverConfigured,
      hardcoverUsername,
      hardcoverAvatarUrl,
      wasSkipped
    } = this.props;

    const {
      isApiKeyModalOpen,
      connectionStatus
    } = this.state;
    let statusKey = 'QuickstartConnectionNotConfigured';

    if (wasSkipped) {
      statusKey = 'QuickstartConnectionSkipped';
    }
    if (isHardcoverConfigured) {
      statusKey = 'QuickstartConnectionConfigured';
    }
    if (connectionStatus === 'tested') {
      statusKey = 'QuickstartConnectionTested';
    }
    if (connectionStatus === 'failed') {
      statusKey = 'QuickstartConnectionFailed';
    }

    const renderButtonContent = () => {
      if (!isHardcoverConfigured) {
        return translate('AddHardcoverButton');
      }

      if (hardcoverUsername) {
        return (
          <span className={styles.hardcoverButtonContent}>
            {hardcoverAvatarUrl ? (
              <img
                className={styles.hardcoverButtonAvatar}
                src={hardcoverAvatarUrl}
                alt=""
              />
            ) : null}
            <span className={styles.hardcoverButtonUsername}>
              {hardcoverUsername}
            </span>
          </span>
        );
      }

      return translate('HardcoverConnectedStatus');
    };

    if (this.props.compact) {
      return (
        <>
          <div className={styles.quickstartCardActions}>
            <button
              className={styles.quickstartCardButton}
              onClick={this.onButtonPress}
            >
              {renderButtonContent()}
            </button>
          </div>
          <div className={styles.quickstartConnectionStatus} aria-live="polite">
            {translate(statusKey)}
          </div>

          <HardcoverApiKeyModal
            isOpen={isApiKeyModalOpen}
            onModalClose={this.onApiKeyModalClose}
            onConnectionSuccess={this.onConnectionSuccess}
            onConnectionFailure={this.onConnectionFailure}
          />
        </>
      );
    }

    return (
      <div className={styles.section}>
        <h2 className={styles.sectionHeader}>
          {translate('Hardcover')}
        </h2>

        {isHardcoverConfigured ? hardcoverUsername && (
          <div className={styles.sectionDescription}>
            <div className={styles.inlineRow}>
              {hardcoverAvatarUrl && (
                <img
                  className={styles.hardcoverAvatar}
                  src={hardcoverAvatarUrl}
                  alt=""
                />
              )}
              <span><strong>{hardcoverUsername}</strong></span>
            </div>
          </div>
        ) : (
          <div className={styles.sectionDescription}>
            {translate('QuickstartHardcoverConnectDescription')}
          </div>
        )}

        <div className={styles.quickstartCardActions}>
          <button
            className={styles.quickstartCardButton}
            onClick={this.onButtonPress}
          >
            {renderButtonContent()}
          </button>
        </div>
        <div className={styles.quickstartConnectionStatus} aria-live="polite">
          {translate(statusKey)}
        </div>

        <HardcoverApiKeyModal
          isOpen={isApiKeyModalOpen}
          onModalClose={this.onApiKeyModalClose}
          onConnectionSuccess={this.onConnectionSuccess}
          onConnectionFailure={this.onConnectionFailure}
        />
      </div>
    );
  }
}

QuickstartHardcoverSection.propTypes = {
  isHardcoverConfigured: PropTypes.bool,
  hardcoverUsername: PropTypes.string,
  hardcoverAvatarUrl: PropTypes.string,
  compact: PropTypes.bool,
  wasSkipped: PropTypes.bool,
  onConnectionTestSuccess: PropTypes.func,
  onConnectionTestFailure: PropTypes.func,
  markSectionInteracted: PropTypes.func
};

export default QuickstartHardcoverSection;
