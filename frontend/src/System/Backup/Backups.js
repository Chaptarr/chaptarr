import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import BackupRow from './BackupRow';
import RestoreBackupModalConnector from './RestoreBackupModalConnector';

const columns = [
  {
    name: 'type',
    isVisible: true
  },
  {
    name: 'name',
    label: () => translate('Name'),
    isVisible: true
  },
  {
    name: 'size',
    label: () => translate('Size'),
    isVisible: true
  },
  {
    name: 'time',
    label: () => translate('Time'),
    isVisible: true
  },
  {
    name: 'actions',
    isVisible: true
  }
];

class Backups extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isRestoreModalOpen: false,
      isConfirmDeleteModalOpen: false,
      isConfirmCancelModalOpen: false
    };
  }

  //
  // Listeners

  onDeleteFailedRestoresPress = () => {
    this.setState({ isConfirmDeleteModalOpen: true });
  };

  onDeleteModalClose = () => {
    this.setState({ isConfirmDeleteModalOpen: false });
  };

  onConfirmDeletePress = () => {
    this.onDeleteModalClose();
    this.props.onDismissFailedRestoresPress();
  };

  onCancelRestorePress = () => {
    this.setState({ isConfirmCancelModalOpen: true });
  };

  onCancelRestoreModalClose = () => {
    this.setState({ isConfirmCancelModalOpen: false });
  };

  onConfirmCancelRestorePress = () => {
    this.onCancelRestoreModalClose();
    this.props.onCancelRestorePress();
  };

  onRestorePress = () => {
    this.setState({ isRestoreModalOpen: true });
  };

  onRestoreModalClose = () => {
    this.setState({ isRestoreModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      items,
      backupExecuting,
      onBackupPress,
      onDeleteBackupPress,
      backupStatus,
      backupStatusError,
      restoreError
    } = this.props;

    const hasBackups = isPopulated && !!items.length;
    const noBackups = isPopulated && !items.length;

    return (
      <PageContent title={translate('Backups')}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label={translate('BackupNow')}
              iconName={icons.BACKUP}
              isSpinning={backupExecuting}
              onPress={onBackupPress}
            />

            <PageToolbarButton
              label={translate('RestoreBackup')}
              iconName={icons.RESTORE}
              onPress={this.onRestorePress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <Alert kind={kinds.WARNING}>
            {translate('BackupsContainUnencryptedCredentials')}
          </Alert>

          {backupStatusError && <Alert kind={kinds.WARNING}>{translate('BackupStatusUnavailable')}</Alert>}
          {backupStatus.scheduleDisabled && <Alert kind={kinds.WARNING}>{translate('ScheduledBackupsDisabled')}</Alert>}
          {backupStatus.databaseBackupUnavailableReason && <Alert kind={kinds.WARNING}>{translate('BackupToolsUnavailableWarning')}</Alert>}
          {backupStatus.restoreFailed &&
            <Alert kind={kinds.WARNING}>
              {translate('BackupRestoreFailedWarning')}
              <Button isDisabled={backupStatus.pendingRestore} onPress={this.onDeleteFailedRestoresPress}>
                {translate('DeleteFailedRestoreFiles')}
              </Button>
            </Alert>}
          {backupStatus.pendingRestore &&
            <Alert kind={kinds.WARNING}>
              {translate('BackupRestorePendingWarning')}
              {backupStatus.canCancelRestore && <Button onPress={this.onCancelRestorePress}>{translate('CancelRestore')}</Button>}
            </Alert>}
          {restoreError && <Alert kind={kinds.DANGER}>{restoreError.responseJSON?.message || translate('BackupRestoreRequestFailed')}</Alert>}

          {
            isFetching && !isPopulated &&
              <LoadingIndicator />
          }

          {
            !isFetching && !!error &&
              <Alert kind={kinds.DANGER}>
                {translate('UnableToLoadBackups')}
              </Alert>
          }

          {
            noBackups &&
              <Alert kind={kinds.INFO}>
                {translate('NoBackupsAreAvailable')}
              </Alert>
          }

          {
            hasBackups &&
              <Table
                columns={columns}
              >
                <TableBody>
                  {
                    items.map((item) => {
                      const {
                        id,
                        type,
                        name,
                        path,
                        size,
                        time,
                        includedComponents,
                        omittedComponents
                      } = item;

                      return (
                        <BackupRow
                          key={id}
                          id={id}
                          type={type}
                          name={name}
                          path={path}
                          size={size}
                          time={time}
                          includedComponents={includedComponents}
                          omittedComponents={omittedComponents}
                          onDeleteBackupPress={onDeleteBackupPress}
                        />
                      );
                    })
                  }
                </TableBody>
              </Table>
          }
        </PageContentBody>

        <ConfirmModal
          isOpen={this.state.isConfirmDeleteModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteFailedRestoreFiles')}
          message={translate('DeleteFailedRestoreFilesMessage')}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeletePress}
          onCancel={this.onDeleteModalClose}
        />

        <ConfirmModal
          isOpen={this.state.isConfirmCancelModalOpen}
          kind={kinds.WARNING}
          title={translate('CancelRestore')}
          message={translate('CancelRestoreConfirm')}
          confirmLabel={translate('CancelRestore')}
          onConfirm={this.onConfirmCancelRestorePress}
          onCancel={this.onCancelRestoreModalClose}
        />

        <RestoreBackupModalConnector
          isOpen={this.state.isRestoreModalOpen}
          onModalClose={this.onRestoreModalClose}
        />
      </PageContent>
    );
  }

}

Backups.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.array.isRequired,
  backupExecuting: PropTypes.bool.isRequired,
  onBackupPress: PropTypes.func.isRequired,
  onDeleteBackupPress: PropTypes.func.isRequired,
  backupStatus: PropTypes.object.isRequired,
  backupStatusError: PropTypes.object,
  restoreError: PropTypes.object,
  onCancelRestorePress: PropTypes.func.isRequired,
  onDismissFailedRestoresPress: PropTypes.func.isRequired
};

export default Backups;
