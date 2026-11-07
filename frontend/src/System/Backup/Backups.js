import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import BackupRow from './BackupRow';
import EncryptedBackupModal from './EncryptedBackupModal';
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
      isEncryptedBackupModalOpen: false
    };
  }

  //
  // Listeners

  onRestorePress = () => {
    this.setState({ isRestoreModalOpen: true });
  };

  onRestoreModalClose = () => {
    this.setState({ isRestoreModalOpen: false });
  };

  onEncryptedBackupPress = () => {
    this.setState({ isEncryptedBackupModalOpen: true });
  };

  onEncryptedBackupModalClose = () => {
    this.setState({ isEncryptedBackupModalOpen: false });
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
      onRetryPress
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

            <PageToolbarButton
              label={translate('DownloadEncryptedBackup')}
              iconName={icons.BACKUP}
              onPress={this.onEncryptedBackupPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <Alert kind={kinds.WARNING}>
            {translate('BackupsContainUnencryptedCredentials')}
          </Alert>

          {
            isFetching && !isPopulated &&
              <LoadingIndicator />
          }

          {
            !isFetching && !!error &&
              <Alert kind={kinds.DANGER} role="alert">
                <div>{getErrorMessage(error, translate('UnableToLoadBackups'))}</div>
                <Button size="small" onPress={onRetryPress}>
                  {translate('Retry')}
                </Button>
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
                        time
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
                          onDeleteBackupPress={onDeleteBackupPress}
                        />
                      );
                    })
                  }
                </TableBody>
              </Table>
          }
        </PageContentBody>

        <RestoreBackupModalConnector
          isOpen={this.state.isRestoreModalOpen}
          onModalClose={this.onRestoreModalClose}
        />

        <EncryptedBackupModal
          isOpen={this.state.isEncryptedBackupModalOpen}
          onModalClose={this.onEncryptedBackupModalClose}
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
  onRetryPress: PropTypes.func.isRequired
};

export default Backups;
