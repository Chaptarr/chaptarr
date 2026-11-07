import PropTypes from 'prop-types';
import React, { Component } from 'react';
import keyboardShortcuts, { shortcuts } from 'Components/keyboardShortcuts';
import IconButton from 'Components/Link/IconButton';
import Link from 'Components/Link/Link';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AuthorSearchInputConnector from './AuthorSearchInputConnector';
import ImportProgressIndicatorConnector from './ImportProgressIndicatorConnector';
import KeyboardShortcutsModal from './KeyboardShortcutsModal';
import PageHeaderActionsMenuConnector from './PageHeaderActionsMenuConnector';
import styles from './PageHeader.css';

class PageHeader extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props);

    this.state = {
      isKeyboardShortcutsModalOpen: false
    };
  }

  componentDidMount() {
    this.props.bindShortcut(shortcuts.OPEN_KEYBOARD_SHORTCUTS_MODAL.key, this.onOpenKeyboardShortcutsModal);
  }

  //
  // Control

  onOpenKeyboardShortcutsModal = () => {
    this.setState({ isKeyboardShortcutsModalOpen: true });
  };

  //
  // Listeners

  onKeyboardShortcutsModalClose = () => {
    this.setState({ isKeyboardShortcutsModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      onSidebarToggle,
      isSidebarVisible,
      experienceMode
    } = this.props;

    return (
      <div className={styles.header}>
        <div className={styles.logoContainer}>
          <Link
            className={styles.logoLink}
            aria-label={translate('ChaptarrLogoAlt')}
            to={'/'}
          >
            <img
              className={styles.logo}
              src={`${window.Chaptarr.urlBase}/Content/Images/chaptarr-logo.svg`}
              alt=""
              aria-hidden="true"
            />
            <span className={styles.brandName}>
              {'Chaptarr'}<span className={styles.brandMark}>{'NG'}</span>
            </span>
          </Link>
        </div>

        <div className={styles.sidebarToggleContainer}>
          <IconButton
            id="sidebar-toggle-button"
            name={icons.NAVBAR_COLLAPSE}
            aria-label={translate('ToggleNavigation')}
            aria-expanded={isSidebarVisible}
            aria-controls="primary-navigation"
            onPress={onSidebarToggle}
          />
        </div>

        <AuthorSearchInputConnector />

        <div className={styles.right}>
          <ImportProgressIndicatorConnector />
          <IconButton
            className={styles.donate}
            name={icons.HEART}
            aria-label={translate('Donate')}
            to="/system/status#donations"
            size={14}
          />
          <PageHeaderActionsMenuConnector
            onKeyboardShortcutsPress={this.onOpenKeyboardShortcutsModal}
            experienceMode={experienceMode}
          />
        </div>

        <KeyboardShortcutsModal
          isOpen={this.state.isKeyboardShortcutsModalOpen}
          onModalClose={this.onKeyboardShortcutsModalClose}
        />
      </div>
    );
  }
}

PageHeader.propTypes = {
  onSidebarToggle: PropTypes.func.isRequired,
  isSidebarVisible: PropTypes.bool.isRequired,
  experienceMode: PropTypes.oneOf(['simple', 'advanced']).isRequired,
  bindShortcut: PropTypes.func.isRequired
};

export default keyboardShortcuts(PageHeader);
