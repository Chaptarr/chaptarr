import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Card from 'Components/Card';
import MediaTypeToggle from 'Components/Form/MediaTypeToggle';
import Icon from 'Components/Icon';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import MediaTypeScope from 'Components/MediaTypeScope';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { icons, kinds } from 'Helpers/Props';
import { FolderType, getRootFolderMediaTypeScope } from 'Helpers/Props/folderTypes';
import EditRootFolderModalConnector from 'Settings/MediaManagement/RootFolder/EditRootFolderModalConnector';
import { deleteRootFolder, fetchRootFolders, setRootFolderValue } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import { hasRootFolderForScope } from './QuickstartSetupUtils';
import styles from './Quickstart.css';
import rootFolderStyles from './QuickstartRootFolders.css';
import setupStyles from './QuickstartSetup.css';

class QuickstartRootFoldersSection extends Component {
  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddRootFolderModalOpen: false,
      newRootFolderScope: props.mediaScope || null,
      editFolderId: null,
      isDeleteRootFolderModalOpen: false,
      deleteRootFolderId: null
    };
  }

  componentDidMount() {
    const {
      rootFoldersState,
      fetchRootFolders: fetchRootFoldersAction
    } = this.props;

    if (!rootFoldersState.isPopulated) {
      fetchRootFoldersAction();
    }
  }

  componentDidUpdate(prevProps) {
    if (prevProps.mediaScope !== this.props.mediaScope && this.props.mediaScope) {
      this.setState({ newRootFolderScope: this.props.mediaScope });
    }
  }

  onAddRootFolder = () => {
    const { setRootFolderValue: setRootFolderValueAction } = this.props;
    const { newRootFolderScope } = this.state;
    let folderType = FolderType.Audiobook;

    if (newRootFolderScope === 'both') {
      folderType = FolderType.Mixed;
    } else if (newRootFolderScope === 'ebook') {
      folderType = FolderType.Ebook;
    }

    setRootFolderValueAction({ name: 'name', value: translate('MediaLibrary') });
    setRootFolderValueAction({ name: 'folderType', value: folderType });

    if (newRootFolderScope !== 'ebook') {
      setRootFolderValueAction({ name: 'audiobookMonitored', value: true });
      setRootFolderValueAction({ name: 'audiobookMonitorExistingMode', value: 'all' });
      setRootFolderValueAction({ name: 'audiobookMonitorNewItems', value: 'all' });
    }

    if (newRootFolderScope !== 'audiobook') {
      setRootFolderValueAction({ name: 'ebookMonitored', value: true });
      setRootFolderValueAction({ name: 'ebookMonitorExistingMode', value: 'all' });
      setRootFolderValueAction({ name: 'ebookMonitorNewItems', value: 'all' });
    }

    setRootFolderValueAction({ name: 'defaultTags', value: [] });

    this.setState({ isAddRootFolderModalOpen: true });
  };

  onEditFolder = (folderId) => {
    this.setState({
      isAddRootFolderModalOpen: true,
      editFolderId: folderId
    });
  };

  onNewRootFolderScopeChange = (newRootFolderScope) => {
    this.setState({ newRootFolderScope });
  };

  onModalClose = () => {
    this.setState({
      isAddRootFolderModalOpen: false,
      editFolderId: null
    });
  };

  onRootFolderAdded = () => {
    const { markSectionInteracted } = this.props;

    if (markSectionInteracted) {
      markSectionInteracted({ section: 'rootFolders' });
    }
  };

  onDeleteRootFolderPress = () => {
    this.setState({
      isAddRootFolderModalOpen: false,
      isDeleteRootFolderModalOpen: true,
      deleteRootFolderId: this.state.editFolderId
    });
  };

  onDeleteRootFolderModalClose = () => {
    this.setState({
      isDeleteRootFolderModalOpen: false,
      deleteRootFolderId: null
    });
  };

  onConfirmDeleteRootFolder = () => {
    const { deleteRootFolderId } = this.state;

    this.props.deleteRootFolder({ id: deleteRootFolderId });
    this.onDeleteRootFolderModalClose();
  };

  render() {
    const { rootFoldersState, mediaScope } = this.props;
    const {
      isAddRootFolderModalOpen,
      newRootFolderScope,
      editFolderId,
      isDeleteRootFolderModalOpen,
      deleteRootFolderId
    } = this.state;
    const { isFetching, isPopulated, items } = rootFoldersState;

    if (isFetching) {
      return (
        <QuickstartSection
          sectionKey="rootFolders"
          title={translate('QuickstartRootFoldersTitle')}
        >
          <LoadingIndicator />
        </QuickstartSection>
      );
    }

    if (!isPopulated) {
      return null;
    }

    const rootFoldersArray = items || [];
    const hasRootFolders = rootFoldersArray.length > 0;
    const hasRootFoldersForScope = hasRootFolderForScope(rootFoldersArray, mediaScope);
    const deleteFolder = rootFoldersArray.find((folder) => folder.id === deleteRootFolderId);
    return (
      <QuickstartSection
        sectionKey="rootFolders"
        title={translate('QuickstartRootFoldersTitle')}
        isComplete={hasRootFoldersForScope}
      >
        {!hasRootFolders && (
          <div className={styles.sectionDescription}>
            {translate('QuickstartRootFoldersDescription')}
          </div>
        )}

        <div className={rootFolderStyles.rootFoldersContainer}>
          <div className={rootFolderStyles.rootFoldersStack}>
            {rootFoldersArray.map((folder) => {
              const isDefaultAudiobookRootFolder = folder.isEffectiveDefaultAudiobook || false;
              const isDefaultEbookRootFolder = folder.isEffectiveDefaultEbook || false;
              const mediaTypeScope = getRootFolderMediaTypeScope(folder);

              return (
                <Card
                  key={folder.id}
                  className={rootFolderStyles.rootFolderCard}
                  onPress={() => this.onEditFolder(folder.id)}
                >
                  <div className={rootFolderStyles.rootFolderContent}>
                    <Icon
                      name={icons.FOLDER_OPEN}
                      size={20}
                    />

                    <div className={rootFolderStyles.rootFolderDetails}>
                      {
                        folder.name &&
                          <div className={rootFolderStyles.rootFolderName}>{folder.name}</div>
                      }

                      <div className={rootFolderStyles.rootFolderPath}>{folder.path}</div>

                      <MediaTypeScope
                        className={rootFolderStyles.rootFolderScope}
                        mediaType={mediaTypeScope}
                      />

                      {
                        (isDefaultAudiobookRootFolder || isDefaultEbookRootFolder) &&
                          <div className={rootFolderStyles.rootFolderBadges}>

                            {
                              isDefaultAudiobookRootFolder &&
                                <div className={rootFolderStyles.rootFolderDefault}>{translate('DefaultAudiobookRootFolderBadge')}</div>
                            }

                            {
                              isDefaultEbookRootFolder &&
                                <div className={rootFolderStyles.rootFolderDefault}>{translate('DefaultEbookRootFolderBadge')}</div>
                            }
                          </div>
                      }
                    </div>

                    <Icon
                      name={icons.CHECK_CIRCLE}
                      kind={kinds.SUCCESS}
                      size={20}
                    />
                  </div>
                </Card>
              );
            })}

            <div className={setupStyles.rootFolderScopeChooser}>
              {newRootFolderScope ? (
                <>
                  <div className={styles.sectionDescription}>
                    {translate('QuickstartNewRootFolderScopeDescription')}
                  </div>
                  <MediaTypeToggle
                    selectedMediaType={newRootFolderScope}
                    onMediaTypeChange={this.onNewRootFolderScopeChange}
                    includeBoth={true}
                  />
                  <Card
                    className={rootFolderStyles.addRootFolderCard}
                    onPress={this.onAddRootFolder}
                  >
                    <div className={rootFolderStyles.addRootFolderContent}>
                      <Icon
                        name={icons.ADD}
                        size={20}
                      />
                      <span>{translate('AddRootFolder')}</span>
                    </div>
                  </Card>
                </>
              ) : (
                <div className={styles.sectionDescription}>
                  {translate('QuickstartSelectFormatsFirst')}
                </div>
              )}
            </div>
          </div>
        </div>

        <EditRootFolderModalConnector
          id={editFolderId || 0}
          isOpen={isAddRootFolderModalOpen}
          onModalClose={this.onModalClose}
          onDeleteRootFolderPress={this.onDeleteRootFolderPress}
          onSaveSuccess={this.onRootFolderAdded}
        />

        <ConfirmModal
          isOpen={isDeleteRootFolderModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteRootFolder')}
          message={translate('DeleteRootFolderMessageText', { name: deleteFolder?.name || deleteFolder?.path || '' })}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteRootFolder}
          onCancel={this.onDeleteRootFolderModalClose}
        />
      </QuickstartSection>
    );
  }
}

QuickstartRootFoldersSection.propTypes = {
  mediaScope: PropTypes.oneOf(['audiobook', 'ebook', 'both']),
  rootFoldersState: PropTypes.object.isRequired,
  fetchRootFolders: PropTypes.func.isRequired,
  setRootFolderValue: PropTypes.func.isRequired,
  deleteRootFolder: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func
};

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.rootFolders,
    (rootFoldersState) => {
      return {
        rootFoldersState
      };
    }
  );
}

const mapDispatchToProps = {
  fetchRootFolders,
  setRootFolderValue,
  deleteRootFolder
};

export default connect(createMapStateToProps, mapDispatchToProps)(QuickstartRootFoldersSection);
