import PropTypes from 'prop-types';
import React, { Component } from 'react';
import ErrorBoundary from 'Components/Error/ErrorBoundary';
import Label from 'Components/Label';
import IconButton from 'Components/Link/IconButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import EditRootFolderModalConnector from './EditRootFolderModalConnector';
import styles from './RootFolder.css';

class RootFolder extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditRootFolderModalOpen: false,
      isDeleteRootFolderModalOpen: false
    };
  }

  componentDidUpdate(prevProps) {
    const { isDeleting, deleteError } = this.props;

    // Close modal when deletion completes (successfully or with error)
    if (prevProps.isDeleting && !isDeleting && this.state.isDeleteRootFolderModalOpen) {
      // Clear safety timeout since deletion completed
      clearTimeout(this._safetyTimeout);
      this.setState({ isDeleteRootFolderModalOpen: false });
    }

    // Also close modal if there's a new error
    if (!prevProps.deleteError && deleteError && this.state.isDeleteRootFolderModalOpen) {
      // Clear safety timeout since we have an error
      clearTimeout(this._safetyTimeout);
      this.setState({ isDeleteRootFolderModalOpen: false });
    }
  }

  componentWillUnmount() {
    // Clear any pending timeouts
    clearTimeout(this._safetyTimeout);

    // Clean up any open modals to prevent memory leaks
    if (this.state.isDeleteRootFolderModalOpen || this.state.isEditRootFolderModalOpen) {
      this.setState({
        isDeleteRootFolderModalOpen: false,
        isEditRootFolderModalOpen: false
      });
    }
  }

  //
  // Listeners

  onEditRootFolderPress = () => {
    this.setState({ isEditRootFolderModalOpen: true });
  };

  onEditRootFolderModalClose = () => {
    this.setState({ isEditRootFolderModalOpen: false });
  };

  onDeleteRootFolderPress = () => {
    this.setState({
      isEditRootFolderModalOpen: false,
      isDeleteRootFolderModalOpen: true
    });
  };

  onDeleteRootFolderModalClose= () => {
    this.setState({ isDeleteRootFolderModalOpen: false });
  };

  onConfirmDeleteRootFolder = () => {
    try {
      // Don't close modal here - let it close via componentDidUpdate
      // when the deletion completes or fails
      this.props.onConfirmDeleteRootFolder(this.props.id);

      // Safety timeout to prevent modal from staying open forever
      this._safetyTimeout = setTimeout(() => {
        if (this.state.isDeleteRootFolderModalOpen) {
          console.warn('Root folder deletion modal timeout - force closing to prevent grey screen');
          this.setState({ isDeleteRootFolderModalOpen: false });
        }
      }, 10000); // 10 second timeout

    } catch (error) {
      console.error('Error during root folder deletion:', error);
      // Close modal on error to prevent grey screen
      this.setState({ isDeleteRootFolderModalOpen: false });
    }
  };

  //
  // Render

  renderProfileValue(profile) {
    const {
      name,
      status
    } = profile;

    if (status === 'missing') {
      return (
        <span className={styles.missingProfile}>
          {name}
        </span>
      );
    }

    if (status === 'unconfigured') {
      return (
        <span className={styles.notConfigured}>
          {name}
        </span>
      );
    }

    return name;
  }

  renderProfileRows(profileRows, profileType) {
    if (!profileRows.length) {
      return (
        <span className={styles.notConfigured}>
          {translate('NotConfigured')}
        </span>
      );
    }

    const showMediaTypePrefix = profileRows.length > 1;

    return profileRows.map((profileRow) => {
      return (
        <div
          key={profileRow.mediaType}
          className={styles.profileRow}
        >
          {
            showMediaTypePrefix &&
              <span className={styles.profileType}>
                {profileRow.label}:
              </span>
          }

          <span className={styles.profileValue}>
            {this.renderProfileValue(profileRow[profileType])}
          </span>
        </div>
      );
    });
  }

  render() {
    const {
      id,
      name,
      path,
      mediaTypeLabel,
      isMediaTypeConfigured,
      isDefaultAudiobookRootFolder,
      isDefaultEbookRootFolder,
      profileRows,
      accessible,
      freeSpace,
      isDeleting
    } = this.props;

    const isUnavailable = accessible === false;
    const freeSpaceContent = freeSpace == null ? '-' : formatBytes(freeSpace);

    return (
      <TableRow>
        <TableRowCell className={styles.nameCell}>
          <div className={styles.name}>
            {name || path}
          </div>
        </TableRowCell>

        <TableRowCell className={styles.pathCell}>
          <div className={styles.path}>
            {path}
          </div>
        </TableRowCell>

        <TableRowCell className={styles.typeCell}>
          <div className={styles.typeLabels}>
            <Label kind={isMediaTypeConfigured ? kinds.INFO : kinds.WARNING}>
              {mediaTypeLabel}
            </Label>

            {
              isDefaultAudiobookRootFolder &&
                <Label kind={kinds.SUCCESS}>
                  {translate('DefaultAudiobookRootFolderBadge')}
                </Label>
            }

            {
              isDefaultEbookRootFolder &&
                <Label kind={kinds.SUCCESS}>
                  {translate('DefaultEbookRootFolderBadge')}
                </Label>
            }
          </div>
        </TableRowCell>

        <TableRowCell className={styles.qualityProfileCell}>
          {this.renderProfileRows(profileRows, 'quality')}
        </TableRowCell>

        <TableRowCell className={styles.metadataProfileCell}>
          {this.renderProfileRows(profileRows, 'metadata')}
        </TableRowCell>

        <TableRowCell className={styles.freeSpace}>
          {
            isUnavailable ?
              <Label kind={kinds.DANGER}>
                {translate('Unavailable')}
              </Label> :
              freeSpaceContent
          }
        </TableRowCell>

        <TableRowCell className={styles.actions}>
          <IconButton
            className={styles.actionButton}
            title={translate('EditRootFolder')}
            name={icons.EDIT}
            onPress={this.onEditRootFolderPress}
          />

          <IconButton
            className={styles.actionButton}
            title={translate('DeleteRootFolder')}
            name={icons.REMOVE}
            isDisabled={isDeleting}
            onPress={this.onDeleteRootFolderPress}
          />
        </TableRowCell>

        <EditRootFolderModalConnector
          id={id}
          isOpen={this.state.isEditRootFolderModalOpen}
          onModalClose={this.onEditRootFolderModalClose}
          onDeleteRootFolderPress={this.onDeleteRootFolderPress}
        />

        <ErrorBoundary errorComponent={() => <div>{translate('RootFolderErrorInDeleteModal')}</div>}>
          <ConfirmModal
            isOpen={this.state.isDeleteRootFolderModalOpen}
            kind={kinds.DANGER}
            title={translate('DeleteRootFolder')}
            message={translate('DeleteRootFolderMessageText', { name: name || path })}
            confirmLabel={translate('Delete')}
            isSpinning={isDeleting}
            onConfirm={this.onConfirmDeleteRootFolder}
            onCancel={this.onDeleteRootFolderModalClose}
          />
        </ErrorBoundary>
      </TableRow>
    );
  }
}

RootFolder.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  path: PropTypes.string.isRequired,
  mediaTypeLabel: PropTypes.string.isRequired,
  isMediaTypeConfigured: PropTypes.bool.isRequired,
  isDefaultAudiobookRootFolder: PropTypes.bool.isRequired,
  isDefaultEbookRootFolder: PropTypes.bool.isRequired,
  profileRows: PropTypes.arrayOf(PropTypes.object).isRequired,
  accessible: PropTypes.bool,
  freeSpace: PropTypes.number,
  isDeleting: PropTypes.bool,
  deleteError: PropTypes.object,
  onConfirmDeleteRootFolder: PropTypes.func.isRequired
};

export default RootFolder;
