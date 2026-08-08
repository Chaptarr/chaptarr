import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FormInputGroup from 'Components/Form/FormInputGroup';
import PathInputConnector from 'Components/Form/PathInputConnector';
import SelectInput from 'Components/Form/SelectInput';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, inputTypes, kinds, sizes } from 'Helpers/Props';
import importModeOptions from 'InteractiveImport/importModeOptions';
import translate from 'Utilities/String/translate';
import RecentFolderRow from './RecentFolderRow';
import styles from './InteractiveImportSelectFolderModalContent.css';

const recentFoldersColumns = [
  {
    name: 'folder',
    label: 'Folder'
  },
  {
    name: 'lastUsed',
    label: 'Last Used'
  },
  {
    name: 'actions',
    label: ''
  }
];

class InteractiveImportSelectFolderModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      folder: '',
      importMode: props.importMode === 'move' ? 'move' : 'copy'
    };
  }

  //
  // Listeners

  onPathChange = ({ value }) => {
    this.setState({ folder: value });
  };

  onRecentPathPress = (folder) => {
    this.setState({ folder });
  };

  onQuickImportPress = () => {
    this.props.onQuickImportPress(this.state.folder, this.state.importMode);
  };

  onInteractiveImportPress = () => {
    this.props.onInteractiveImportPress(this.state.folder, this.state.importMode);
  };

  onConfirmInteractiveImportPress = () => {
    this.props.onConfirmInteractiveImportPress(this.state.folder, this.state.importMode);
  };

  onImportModeChange = ({ value }) => {
    this.setState({ importMode: value });
  };

  //
  // Render

  render() {
    const {
      recentFolders,
      isCheckingInteractiveImportFolder,
      largeFolderWarning,
      mediaManagementSettings,
      onPathFallbackChange,
      onRemoveRecentFolderPress,
      onModalClose
    } = this.props;

    const {
      folder,
      importMode
    } = this.state;
    const showLargeFolderWarning = !!largeFolderWarning && largeFolderWarning.folder === folder;
    const matchingSettings = mediaManagementSettings.settings || {};
    const bookMatchingStrictness = matchingSettings.bookMatchingStrictness || { value: 'balanced' };
    const usePathAsTagsFallback = matchingSettings.usePathAsTagsFallback || { value: true };
    const isStrictMatching = bookMatchingStrictness.value === 'strict';
    const isPathFallbackBusy = mediaManagementSettings.isFetching || mediaManagementSettings.isSaving;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('ManualImportSelectFolder')}
        </ModalHeader>

        <ModalBody>
          <PathInputConnector
            name="folder"
            value={folder}
            propagateValueOnChange={true}
            onChange={this.onPathChange}
          />

          {
            showLargeFolderWarning &&
              <Alert
                className={styles.largeFolderWarning}
                kind={kinds.WARNING}
              >
                <Icon
                  className={styles.largeFolderWarningIcon}
                  name={icons.WARNING}
                />

                <span>
                  <strong>{largeFolderWarning.fileCount}</strong> {translate('ManualImportLargeFolderWarning')}
                </span>
              </Alert>
          }

          {
            !!recentFolders.length &&
              <div className={styles.recentFoldersContainer}>
                <Table
                  columns={recentFoldersColumns}
                >
                  <TableBody>
                    {
                      recentFolders.slice(0).reverse().map((recentFolder) => {
                        return (
                          <RecentFolderRow
                            key={recentFolder.folder}
                            folder={recentFolder.folder}
                            lastUsed={recentFolder.lastUsed}
                            onPress={this.onRecentPathPress}
                            onRemoveRecentFolderPress={onRemoveRecentFolderPress}
                          />
                        );
                      })
                    }
                  </TableBody>
                </Table>
              </div>
          }

          <div className={styles.buttonsContainer}>
            <div className={styles.buttonContainer}>
              <Button
                className={styles.button}
                kind={kinds.PRIMARY}
                size={sizes.LARGE}
                isDisabled={!folder || isPathFallbackBusy}
                onPress={this.onQuickImportPress}
              >
                <Icon
                  className={styles.buttonIcon}
                  name={icons.QUICK}
                />

                {translate('ManualImportImportAutomatically')}
              </Button>
            </div>

            <div className={styles.buttonContainer}>
              <Button
                className={styles.button}
                kind={kinds.PRIMARY}
                size={sizes.LARGE}
                isDisabled={!folder || isCheckingInteractiveImportFolder || isPathFallbackBusy}
                onPress={showLargeFolderWarning ? this.onConfirmInteractiveImportPress : this.onInteractiveImportPress}
              >
                <Icon
                  className={styles.buttonIcon}
                  name={icons.INTERACTIVE}
                />

                {showLargeFolderWarning ? translate('Continue') : translate('InteractiveImport')}
              </Button>
            </div>
          </div>
        </ModalBody>

        <ModalFooter className={styles.footer}>
          <div className={styles.leftButtons}>
            <SelectInput
              className={styles.importMode}
              name="importMode"
              value={importMode}
              values={importModeOptions}
              onChange={this.onImportModeChange}
            />

            <div className={styles.matchingControl}>
              <FormInputGroup
                type={inputTypes.CHECK}
                name="usePathAsTagsFallback"
                helpText={translate('ManualImportPathFallbackShortLabel')}
                isDisabled={isStrictMatching || isPathFallbackBusy}
                onChange={onPathFallbackChange}
                {...usePathAsTagsFallback}
              />
            </div>
          </div>

          <div className={styles.rightButtons}>
            <Button onPress={onModalClose}>
              {translate('Cancel')}
            </Button>
          </div>
        </ModalFooter>
      </ModalContent>
    );
  }
}

InteractiveImportSelectFolderModalContent.propTypes = {
  recentFolders: PropTypes.arrayOf(PropTypes.object).isRequired,
  importMode: PropTypes.string,
  isCheckingInteractiveImportFolder: PropTypes.bool.isRequired,
  largeFolderWarning: PropTypes.object,
  mediaManagementSettings: PropTypes.object.isRequired,
  onQuickImportPress: PropTypes.func.isRequired,
  onInteractiveImportPress: PropTypes.func.isRequired,
  onConfirmInteractiveImportPress: PropTypes.func.isRequired,
  onPathFallbackChange: PropTypes.func.isRequired,
  onRemoveRecentFolderPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default InteractiveImportSelectFolderModalContent;
