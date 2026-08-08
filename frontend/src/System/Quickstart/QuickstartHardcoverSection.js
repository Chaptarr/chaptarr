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
      isApiKeyModalOpen: false
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
    // Mark this section as interacted when connection succeeds
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'hardcover' });
    }
  };

  //
  // Render

  render() {
    const {
      isHardcoverConfigured,
      hardcoverUsername,
      hardcoverAvatarUrl
    } = this.props;

    const {
      isApiKeyModalOpen
    } = this.state;

    const renderButtonContent = () => {
      if (!isHardcoverConfigured) {
        return 'Add Hardcover';
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

      return 'Connected';
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

          <HardcoverApiKeyModal
            isOpen={isApiKeyModalOpen}
            onModalClose={this.onApiKeyModalClose}
            onConnectionSuccess={this.onConnectionSuccess}
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

        <HardcoverApiKeyModal
          isOpen={isApiKeyModalOpen}
          onModalClose={this.onApiKeyModalClose}
          onConnectionSuccess={this.onConnectionSuccess}
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
  markSectionInteracted: PropTypes.func
};

export default QuickstartHardcoverSection;
