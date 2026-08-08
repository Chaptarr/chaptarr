import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import MoveAuthorModal from 'Author/MoveAuthor/MoveAuthorModal';
import MetadataProfileSelectInputConnector from 'Components/Form/MetadataProfileSelectInputConnector';
import QualityProfileSelectInputConnector from 'Components/Form/QualityProfileSelectInputConnector';
import RootFolderSelectInputConnector from 'Components/Form/RootFolderSelectInputConnector';
import SelectInput from 'Components/Form/SelectInput';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageContentFooter from 'Components/Page/PageContentFooter';
import Tooltip from 'Components/Tooltip/Tooltip';
import { kinds } from 'Helpers/Props';
import { FolderType } from 'Helpers/Props/folderTypes';
import { fetchRootFolders } from 'Store/Actions/Settings/rootFolders';
import translate from 'Utilities/String/translate';
import AuthorEditorFooterLabel from './AuthorEditorFooterLabel';
import DeleteAuthorModal from './Delete/DeleteAuthorModal';
import TagsModal from './Tags/TagsModal';
import styles from './AuthorEditorFooter.css';

const NO_CHANGE = 'noChange';

const mapDispatchToProps = {
  dispatchFetchRootFolders: fetchRootFolders
};

class AuthorEditorFooter extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      monitored: NO_CHANGE,
      monitorNewItems: NO_CHANGE,
      syncMonitoredAcrossFormats: NO_CHANGE,
      qualityProfileId: NO_CHANGE,
      metadataProfileId: NO_CHANGE,
      rootFolderPath: NO_CHANGE,
      savingTags: false,
      isDeleteAuthorModalOpen: false,
      isTagsModalOpen: false,
      isConfirmMoveModalOpen: false,
      isMonitoringConfirmModalOpen: false,
      pendingMonitoringChange: null,
      destinationRootFolder: null,
      pendingRootFolderMediaType: null
    };
  }

  //
  // Lifecycle

  componentDidMount() {
    this.props.dispatchFetchRootFolders();
  }

  componentDidUpdate(prevProps) {
    const {
      isSaving,
      saveError,
      selectedMediaType
    } = this.props;

    if (prevProps.selectedMediaType !== selectedMediaType) {
      this.setState({
        qualityProfileId: NO_CHANGE,
        metadataProfileId: NO_CHANGE,
        rootFolderPath: NO_CHANGE,
        isConfirmMoveModalOpen: false,
        destinationRootFolder: null,
        pendingRootFolderMediaType: null
      });
    }

    if (prevProps.isSaving && !isSaving && !saveError) {
      this.setState({
        monitored: NO_CHANGE,
        monitorNewItems: NO_CHANGE,
        syncMonitoredAcrossFormats: NO_CHANGE,
        qualityProfileId: NO_CHANGE,
        metadataProfileId: NO_CHANGE,
        rootFolderPath: NO_CHANGE,
        savingTags: false,
        isMonitoringConfirmModalOpen: false,
        pendingMonitoringChange: null
      });
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value });

    if (value === NO_CHANGE) {
      return;
    }

    switch (name) {
      case 'rootFolderPath':
        this.setState({
          isConfirmMoveModalOpen: true,
          destinationRootFolder: value,
          pendingRootFolderMediaType: this.props.selectedMediaType
        });
        break;
      case 'monitored':
        // BULK MONITORING CONFIRMATION: Show confirmation for bulk monitoring changes
        this.setState({
          isMonitoringConfirmModalOpen: true,
          pendingMonitoringChange: { [name]: value === 'monitored' }
        });
        break;
      case 'monitorNewItems':
        // Handle monitor new items changes with media type awareness
        this.props.onSaveSelected(this.buildMonitorNewItemsPayload(value));
        break;
      case 'syncMonitoredAcrossFormats':
        this.props.onSaveSelected({
          syncMonitoredAcrossFormats: value === 'enabled'
        });
        break;
      case 'qualityProfileId':
        this.props.onSaveSelected(this.buildQualityProfilePayload(value));
        break;
      case 'metadataProfileId':
        this.props.onSaveSelected(this.buildMetadataProfilePayload(value));
        break;
      default:
        this.props.onSaveSelected({ [name]: value });
    }
  };

  onApplyTagsPress = (tags, applyTags) => {
    this.setState({
      savingTags: true,
      isTagsModalOpen: false
    });

    this.props.onSaveSelected({
      tags,
      applyTags
    });
  };

  onDeleteSelectedPress = () => {
    this.setState({ isDeleteAuthorModalOpen: true });
  };

  onDeleteAuthorModalClose = () => {
    this.setState({ isDeleteAuthorModalOpen: false });
  };

  onTagsPress = () => {
    this.setState({ isTagsModalOpen: true });
  };

  onTagsModalClose = () => {
    this.setState({ isTagsModalOpen: false });
  };

  onSaveRootFolderPress = () => {
    const pendingMediaType = this.state.pendingRootFolderMediaType || this.props.selectedMediaType;

    this.setState({
      isConfirmMoveModalOpen: false,
      destinationRootFolder: null,
      pendingRootFolderMediaType: null
    });

    this.props.onSaveSelected(this.buildRootFolderPayload(this.state.destinationRootFolder, pendingMediaType, false));
  };

  buildMonitoringPayload = (monitorValue) => {
    const { selectedMediaType } = this.props;
    const monitorExistingValue = monitorValue === true ? 1 : 0;

    // Handle media type specific monitoring
    if (selectedMediaType === 'audiobook') {
      return {
        audiobookMonitorExisting: monitorExistingValue
      };
    }

    if (selectedMediaType === 'ebook') {
      return {
        ebookMonitorExisting: monitorExistingValue
      };
    }

    // 'all' mode - update both types
    return {
      monitored: monitorValue === true,
      audiobookMonitorExisting: monitorExistingValue,
      ebookMonitorExisting: monitorExistingValue
    };
  };

  buildMonitorNewItemsPayload = (value) => {
    if (value === NO_CHANGE) {
      return {};
    }

    const { selectedMediaType } = this.props;
    const monitorNewItems = value === 'yes';

    // Handle media type specific monitoring for new items
    if (selectedMediaType === 'audiobook') {
      return {
        audiobookMonitorFuture: monitorNewItems
      };
    }

    if (selectedMediaType === 'ebook') {
      return {
        ebookMonitorFuture: monitorNewItems
      };
    }

    // 'all' mode - update both types
    return {
      audiobookMonitorFuture: monitorNewItems,
      ebookMonitorFuture: monitorNewItems
    };
  };

  buildQualityProfilePayload = (value) => {
    const { selectedMediaType } = this.props;
    if (selectedMediaType === 'audiobook') {
      return { audiobookQualityProfileId: value };
    }
    if (selectedMediaType === 'ebook') {
      return { ebookQualityProfileId: value };
    }
    return {};
  };

  buildMetadataProfilePayload = (value) => {
    const { selectedMediaType } = this.props;
    if (selectedMediaType === 'audiobook') {
      return { audiobookMetadataProfileId: value };
    }
    if (selectedMediaType === 'ebook') {
      return { ebookMetadataProfileId: value };
    }
    return {};
  };

  buildRootFolderPayload = (rootFolderPath, mediaType, moveFiles) => {
    if (mediaType === 'audiobook') {
      return { audiobookRootFolderPath: rootFolderPath, moveFiles };
    }
    if (mediaType === 'ebook') {
      return { ebookRootFolderPath: rootFolderPath, moveFiles };
    }
    return {};
  };

  onMoveAuthorPress = () => {
    const pendingMediaType = this.state.pendingRootFolderMediaType || this.props.selectedMediaType;

    this.setState({
      isConfirmMoveModalOpen: false,
      destinationRootFolder: null,
      pendingRootFolderMediaType: null
    });

    this.props.onSaveSelected(this.buildRootFolderPayload(this.state.destinationRootFolder, pendingMediaType, true));
  };

  onMonitoringConfirmPress = () => {
    const monitorValue = this.state.pendingMonitoringChange.monitored;
    const changes = this.buildMonitoringPayload(monitorValue);

    this.setState({
      isMonitoringConfirmModalOpen: false,
      pendingMonitoringChange: null
    });

    this.props.onSaveSelected(changes);
  };

  onMonitoringConfirmModalClose = () => {
    // Revert the dropdown to its previous value
    this.setState({
      monitored: NO_CHANGE,
      isMonitoringConfirmModalOpen: false,
      pendingMonitoringChange: null
    });
  };

  //
  // Render

  render() {
    const {
      authorIds,
      selectedCount,
      isSaving,
      isDeleting,
      isOrganizingAuthor,
      isRetaggingAuthor,
      selectedMediaType,
      onOrganizeAuthorPress,
      onRetagAuthorPress
    } = this.props;

    const {
      monitored,
      monitorNewItems,
      syncMonitoredAcrossFormats,
      qualityProfileId,
      metadataProfileId,
      rootFolderPath,
      savingTags,
      isTagsModalOpen,
      isDeleteAuthorModalOpen,
      isConfirmMoveModalOpen,
      isMonitoringConfirmModalOpen,
      pendingMonitoringChange,
      destinationRootFolder
    } = this.state;

    const isTypeSelectionRequired = !selectedMediaType || selectedMediaType === 'all';
    const typeSpecificTooltip = 'Select Audiobooks or Ebooks first';
    const typeSpecificControlsDisabled = !selectedCount || isTypeSelectionRequired;
    const showTypeSpecificTooltip = !!selectedCount && isTypeSelectionRequired;
    const showSyncHelpText = syncMonitoredAcrossFormats === 'enabled';
    const rootFolderType = selectedMediaType === 'audiobook'
      ? FolderType.Audiobook
      : (selectedMediaType === 'ebook' ? FolderType.Ebook : null);
    const profileType = (selectedMediaType === 'audiobook' || selectedMediaType === 'ebook') ? selectedMediaType : null;

    const monitoredOptions = [
      { key: NO_CHANGE, value: translate('NoChange'), isDisabled: true },
      { key: 'monitored', value: translate('Monitored') },
      { key: 'notMonitored', value: translate('NotMonitored') }
    ];

    const monitorNewItemsOptions = [
      { key: NO_CHANGE, value: translate('NoChange'), isDisabled: true },
      { key: 'yes', value: translate('Yes') },
      { key: 'no', value: translate('No') }
    ];

    const syncAcrossFormatsOptions = [
      { key: NO_CHANGE, value: translate('NoChange'), isDisabled: true },
      { key: 'enabled', value: translate('Enabled') },
      { key: 'disabled', value: translate('Disabled') }
    ];

    return (
      <PageContentFooter>
        <div className={styles.footer}>
          <div className={styles.dropdownContainer}>
            <div className={styles.inputContainer}>
              <AuthorEditorFooterLabel
                label={translate('MonitorAuthor')}
                isSaving={isSaving && monitored !== NO_CHANGE}
              />

              <SelectInput
                name="monitored"
                value={monitored}
                values={monitoredOptions}
                isDisabled={!selectedCount}
                onChange={this.onInputChange}
              />
            </div>

            <div className={styles.inputContainer}>
              <AuthorEditorFooterLabel
                label={translate('MonitorNewItems')}
                isSaving={isSaving && monitorNewItems !== NO_CHANGE}
              />

              <SelectInput
                name="monitorNewItems"
                value={monitorNewItems}
                values={monitorNewItemsOptions}
                isDisabled={!selectedCount}
                onChange={this.onInputChange}
              />
            </div>

            <div className={styles.inputContainer}>
              <AuthorEditorFooterLabel
                label="Sync Monitored Audio/eBooks"
                isSaving={isSaving && syncMonitoredAcrossFormats !== NO_CHANGE}
              />

              <SelectInput
                name="syncMonitoredAcrossFormats"
                value={syncMonitoredAcrossFormats}
                values={syncAcrossFormatsOptions}
                isDisabled={!selectedCount}
                onChange={this.onInputChange}
              />

              {
                showSyncHelpText &&
                  <div className={styles.helpText}>
                    {translate('AuthorEditorSyncAcrossFormatsHelpText')}
                  </div>
              }
            </div>

            <div className={styles.inputContainer}>
              <AuthorEditorFooterLabel
                label={translate('QualityProfile')}
                isSaving={isSaving && qualityProfileId !== NO_CHANGE}
              />

              {
                showTypeSpecificTooltip ?
                  <Tooltip
                    className={styles.tooltipWrapper}
                    anchor={
                      <QualityProfileSelectInputConnector
                        name="qualityProfileId"
                        value={qualityProfileId}
                        includeNoChange={true}
                        profileType={profileType}
                        isDisabled={typeSpecificControlsDisabled}
                        onChange={this.onInputChange}
                      />
                    }
                    tooltip={typeSpecificTooltip}
                  /> :
                  <QualityProfileSelectInputConnector
                    name="qualityProfileId"
                    value={qualityProfileId}
                    includeNoChange={true}
                    profileType={profileType}
                    isDisabled={typeSpecificControlsDisabled}
                    onChange={this.onInputChange}
                  />
              }
            </div>

            <div
              className={styles.inputContainer}
            >
              <AuthorEditorFooterLabel
                label={translate('MetadataProfile')}
                isSaving={isSaving && metadataProfileId !== NO_CHANGE}
              />

              {
                showTypeSpecificTooltip ?
                  <Tooltip
                    className={styles.tooltipWrapper}
                    anchor={
                      <MetadataProfileSelectInputConnector
                        name="metadataProfileId"
                        value={metadataProfileId}
                        includeNoChange={true}
                        includeNone={true}
                        profileType={profileType}
                        isDisabled={typeSpecificControlsDisabled}
                        onChange={this.onInputChange}
                      />
                    }
                    tooltip={typeSpecificTooltip}
                  /> :
                  <MetadataProfileSelectInputConnector
                    name="metadataProfileId"
                    value={metadataProfileId}
                    includeNoChange={true}
                    includeNone={true}
                    profileType={profileType}
                    isDisabled={typeSpecificControlsDisabled}
                    onChange={this.onInputChange}
                  />
              }
            </div>

            <div
              className={styles.inputContainer}
            >
              <AuthorEditorFooterLabel
                label={translate('RootFolder')}
                isSaving={isSaving && rootFolderPath !== NO_CHANGE}
              />

              {
                showTypeSpecificTooltip ?
                  <Tooltip
                    className={styles.tooltipWrapper}
                    anchor={
                      <RootFolderSelectInputConnector
                        name="rootFolderPath"
                        value={rootFolderPath}
                        includeNoChange={true}
                        folderType={rootFolderType}
                        isDisabled={typeSpecificControlsDisabled}
                        selectedValueOptions={{ includeFreeSpace: false }}
                        onChange={this.onInputChange}
                      />
                    }
                    tooltip={typeSpecificTooltip}
                  /> :
                  <RootFolderSelectInputConnector
                    name="rootFolderPath"
                    value={rootFolderPath}
                    includeNoChange={true}
                    folderType={rootFolderType}
                    isDisabled={typeSpecificControlsDisabled}
                    selectedValueOptions={{ includeFreeSpace: false }}
                    onChange={this.onInputChange}
                  />
              }
            </div>
          </div>

            <div className={styles.buttonContainer}>
              <div className={styles.buttonContainerContent}>
                <AuthorEditorFooterLabel
                  label={translate('SelectedCountAuthorsSelectedInterp', [selectedCount])}
                  isSaving={false}
                />

                <div className={styles.buttons}>

                <SpinnerButton
                  className={styles.organizeSelectedButton}
                  kind={kinds.WARNING}
                  isSpinning={isOrganizingAuthor}
                  isDisabled={!selectedCount || isOrganizingAuthor || isRetaggingAuthor}
                  onPress={onOrganizeAuthorPress}
                >
                  {translate('RenameFiles')}
                </SpinnerButton>

                <SpinnerButton
                  className={styles.organizeSelectedButton}
                  kind={kinds.WARNING}
                  isSpinning={isRetaggingAuthor}
                  isDisabled={!selectedCount || isOrganizingAuthor || isRetaggingAuthor}
                  onPress={onRetagAuthorPress}
                >
                  {translate('WriteMetadataTags')}
                </SpinnerButton>

                <SpinnerButton
                  className={styles.tagsButton}
                  isSpinning={isSaving && savingTags}
                  isDisabled={!selectedCount || isOrganizingAuthor || isRetaggingAuthor}
                  onPress={this.onTagsPress}
                >
                  {translate('SetChaptarrTags')}
                </SpinnerButton>

                <SpinnerButton
                  className={styles.deleteSelectedButton}
                  kind={kinds.DANGER}
                  isSpinning={isDeleting}
                  isDisabled={!selectedCount || isDeleting}
                  onPress={this.onDeleteSelectedPress}
                >
                  {translate('Delete')}
                </SpinnerButton>

              </div>
            </div>
          </div>
        </div>

        <TagsModal
          isOpen={isTagsModalOpen}
          authorIds={authorIds}
          onApplyTagsPress={this.onApplyTagsPress}
          onModalClose={this.onTagsModalClose}
        />

        <DeleteAuthorModal
          isOpen={isDeleteAuthorModalOpen}
          authorIds={authorIds}
          onModalClose={this.onDeleteAuthorModalClose}
        />

        <MoveAuthorModal
          destinationRootFolder={destinationRootFolder}
          isOpen={isConfirmMoveModalOpen}
          onSavePress={this.onSaveRootFolderPress}
          onMoveAuthorPress={this.onMoveAuthorPress}
        />

        <ConfirmModal
          isOpen={isMonitoringConfirmModalOpen}
          kind={kinds.WARNING}
          title="Confirm Bulk Monitoring Change"
          message={
            pendingMonitoringChange?.monitored ?
              `Are you sure you want to set ${selectedCount} selected authors to monitored?` :
              `Are you sure you want to set ${selectedCount} selected authors to unmonitored?`
          }
          confirmLabel="Apply Changes"
          cancelLabel="Cancel"
          onConfirm={this.onMonitoringConfirmPress}
          onCancel={this.onMonitoringConfirmModalClose}
        />

      </PageContentFooter>
    );
  }
}

AuthorEditorFooter.propTypes = {
  authorIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  selectedCount: PropTypes.number.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  deleteError: PropTypes.object,
  isOrganizingAuthor: PropTypes.bool.isRequired,
  isRetaggingAuthor: PropTypes.bool.isRequired,
  showMetadataProfile: PropTypes.bool.isRequired,
  selectedMediaType: PropTypes.oneOf(['all', 'audiobook', 'ebook']),
  onSaveSelected: PropTypes.func.isRequired,
  onOrganizeAuthorPress: PropTypes.func.isRequired,
  onRetagAuthorPress: PropTypes.func.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired
};

export default connect(undefined, mapDispatchToProps)(AuthorEditorFooter);
