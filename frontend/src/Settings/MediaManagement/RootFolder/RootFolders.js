import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import PageSectionContent from 'Components/Page/PageSectionContent';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons, kinds } from 'Helpers/Props';
import { getRootFolderMediaTypeLabel, getRootFolderMediaTypes } from 'Helpers/Props/folderTypes';
import sortByName from 'Utilities/Array/sortByName';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import EditRootFolderModalConnector from './EditRootFolderModalConnector';
import RootFolder from './RootFolder';
import styles from './RootFolders.css';

const columns = [
  {
    name: 'name',
    label: () => translate('Name'),
    className: styles.nameHeader,
    isVisible: true
  },
  {
    name: 'path',
    label: () => translate('Path'),
    className: styles.pathHeader,
    isVisible: true
  },
  {
    name: 'type',
    label: () => translate('Type'),
    className: styles.typeHeader,
    isVisible: true
  },
  {
    name: 'qualityProfile',
    label: () => translate('QualityProfile'),
    className: styles.qualityProfileHeader,
    isVisible: true
  },
  {
    name: 'metadataProfile',
    label: () => translate('MetadataProfile'),
    className: styles.metadataProfileHeader,
    isVisible: true
  },
  {
    name: 'freeSpace',
    label: () => translate('FreeSpace'),
    className: styles.freeSpaceHeader,
    isVisible: true
  },
  {
    name: 'actions',
    label: '',
    className: styles.actionsHeader,
    isVisible: true
  }
];

const authorRootBackfillCountKeys = [
  'candidates',
  'updated',
  'skippedManuallyOverridden',
  'skippedNoDefault',
  'skippedNoProfile'
];

function isValidAuthorRootBackfillPreview(preview) {
  if (!preview || typeof preview !== 'object') {
    return false;
  }

  const counts = authorRootBackfillCountKeys.map((key) => preview[key]);

  if (!counts.every((count) => Number.isSafeInteger(count) && count >= 0)) {
    return false;
  }

  const {
    candidates,
    updated,
    skippedManuallyOverridden,
    skippedNoDefault,
    skippedNoProfile
  } = preview;

  return updated + skippedManuallyOverridden + skippedNoDefault + skippedNoProfile ===
    candidates;
}

class RootFolders extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddRootFolderModalOpen: false,
      isBackfillPreviewModalOpen: false,
      isPreviewingAuthorRoots: false,
      isSubmittingAuthorRootBackfill: false,
      backfillPreview: null,
      backfillPreviewError: null,
      backfillCommandError: null
    };
  }

  componentWillUnmount() {
    this._isUnmounted = true;

    if (this._backfillPreviewRequest) {
      this._backfillPreviewRequest.abortRequest();
    }
  }

  //
  // Listeners

  onAddRootFolderPress = () => {
    const { setRootFolderValue } = this.props;

    // Set minimal default values - user will configure per-media-type settings via toggle
    if (setRootFolderValue) {
      setRootFolderValue({ name: 'name', value: translate('MediaLibrary') });
      setRootFolderValue({ name: 'audiobookMonitored', value: true });
      setRootFolderValue({ name: 'audiobookMonitorExistingMode', value: 'all' });
      setRootFolderValue({ name: 'audiobookMonitorNewItems', value: 'all' });
      setRootFolderValue({ name: 'ebookMonitored', value: true });
      setRootFolderValue({ name: 'ebookMonitorExistingMode', value: 'all' });
      setRootFolderValue({ name: 'ebookMonitorNewItems', value: 'all' });
      setRootFolderValue({ name: 'defaultTags', value: [] });
    }

    this.setState({
      isAddRootFolderModalOpen: true
    });
  };

  onAddRootFolderModalClose = () => {
    this.setState({
      isAddRootFolderModalOpen: false
    });
  };

  onReviewAuthorRootBackfillPress = () => {
    if (this._backfillPreviewRequest || this.state.isPreviewingAuthorRoots) {
      return;
    }

    this.setState({
      isPreviewingAuthorRoots: true,
      backfillPreview: null,
      backfillPreviewError: null,
      backfillCommandError: null
    });

    this._backfillPreviewRequest = createAjaxRequest({
      url: '/author/editor/backfill-roots?dryRun=true',
      method: 'POST',
      dataType: 'json'
    });

    this._backfillPreviewRequest.request.then((preview) => {
      this._backfillPreviewRequest = null;

      if (this._isUnmounted) {
        return;
      }

      if (!isValidAuthorRootBackfillPreview(preview)) {
        this.setState({
          isPreviewingAuthorRoots: false,
          backfillPreview: null,
          isBackfillPreviewModalOpen: false,
          backfillPreviewError: translate('UnableToLoadAuthorRootFolderBackfillPreview')
        });
        return;
      }

      this.setState({
        isPreviewingAuthorRoots: false,
        isBackfillPreviewModalOpen: true,
        backfillPreview: preview
      });
    }).catch(() => {
      this._backfillPreviewRequest = null;

      if (this._isUnmounted) {
        return;
      }

      this.setState({
        isPreviewingAuthorRoots: false,
        backfillPreviewError: translate('UnableToLoadAuthorRootFolderBackfillPreview')
      });
    });
  };

  onBackfillPreviewModalClose = () => {
    if (
      this.state.isSubmittingAuthorRootBackfill ||
      this.props.isBackfillingAuthorRootFolders ||
      this._isSubmittingAuthorRootBackfill
    ) {
      return;
    }

    this.setState({ isBackfillPreviewModalOpen: false });
  };

  canQueueAuthorRootBackfill = (preview) => {
    return isValidAuthorRootBackfillPreview(preview) && preview.updated > 0;
  };

  onConfirmAuthorRootBackfill = () => {
    if (!this.canQueueAuthorRootBackfill(this.state.backfillPreview)) {
      this.setState({ isBackfillPreviewModalOpen: false });
      return;
    }

    if (this.state.isSubmittingAuthorRootBackfill || this._isSubmittingAuthorRootBackfill) {
      return;
    }

    this._isSubmittingAuthorRootBackfill = true;
    this.setState({
      isSubmittingAuthorRootBackfill: true,
      backfillCommandError: null
    });

    Promise.resolve().then(() => this.props.onBackfillAuthorRootFolders()).then(() => {
      this._isSubmittingAuthorRootBackfill = false;

      if (this._isUnmounted) {
        return;
      }

      this.setState({
        isSubmittingAuthorRootBackfill: false,
        isBackfillPreviewModalOpen: false
      });
    }).catch(() => {
      this._isSubmittingAuthorRootBackfill = false;

      if (this._isUnmounted) {
        return;
      }

      this.setState({
        isSubmittingAuthorRootBackfill: false,
        backfillCommandError: translate('UnableToQueueAuthorRootFolderBackfill')
      });
    });
  };

  renderAuthorRootBackfillPreview() {
    const { backfillPreview } = this.state;

    if (!backfillPreview) {
      return null;
    }

    const candidates = Number(backfillPreview.candidates) || 0;
    const updatedCount = Number(backfillPreview.updated);
    const updated = Number.isInteger(updatedCount) ? updatedCount : 0;

    return (
      <div>
        <p>{translate('BackfillAuthorRootFoldersHelpText')}</p>
        <p>{translate('BackfillAuthorRootFoldersSummary', [updated, candidates])}</p>
        {
          this.state.backfillCommandError &&
            <p className={styles.backfillError} role="alert">
              {this.state.backfillCommandError}
            </p>
        }
        {
          candidates === 0 ?
            <p>{translate('NoAuthorsMissingRootFolder')}</p> :
            <ul>
              <li>
                {translate('BackfillAuthorRootFoldersSkippedManuallyOverridden', [
                  Number(backfillPreview.skippedManuallyOverridden) || 0
                ])}
              </li>
              <li>
                {translate('BackfillAuthorRootFoldersSkippedNoDefault', [
                  Number(backfillPreview.skippedNoDefault) || 0
                ])}
              </li>
              <li>
                {translate('BackfillAuthorRootFoldersSkippedNoProfile', [
                  Number(backfillPreview.skippedNoProfile) || 0
                ])}
              </li>
            </ul>
        }
      </div>
    );
  }

  getProfileId = (folder, mediaType, profileType) => {
    const mediaSettings = folder[mediaType];
    const flatName = `${mediaType}${profileType}ProfileId`;

    return folder[flatName] ?? mediaSettings?.[`${profileType.toLowerCase()}ProfileId`];
  };

  getProfileDisplay = (profiles, profileId) => {
    const id = Number(profileId);

    if (!id) {
      return {
        name: translate('NotConfigured'),
        status: 'unconfigured'
      };
    }

    const profile = profiles.find((item) => item.id === id);

    if (!profile) {
      return {
        name: translate('MissingProfile'),
        status: 'missing'
      };
    }

    return {
      name: profile.name,
      status: 'configured'
    };
  };

  getProfileRows = (folder) => {
    const {
      qualityProfiles,
      metadataProfiles
    } = this.props;

    return getRootFolderMediaTypes(folder).map((mediaType) => {
      const isAudiobook = mediaType === 'audiobook';
      const qualityProfileId = this.getProfileId(folder, mediaType, 'Quality');
      const metadataProfileId = this.getProfileId(folder, mediaType, 'Metadata');

      return {
        mediaType,
        label: isAudiobook ? translate('AudiobookLabel') : translate('EbookLabel'),
        quality: this.getProfileDisplay(qualityProfiles, qualityProfileId),
        metadata: this.getProfileDisplay(metadataProfiles, metadataProfileId)
      };
    });
  };

  //
  // Render

  render() {
    const {
      items,
      isDeleting,
      deleteError,
      isBackfillingAuthorRootFolders,
      onConfirmDeleteRootFolder,
      ...otherProps
    } = this.props;

    const {
      isAddRootFolderModalOpen
    } = this.state;
    const canBackfill = this.canQueueAuthorRootBackfill(this.state.backfillPreview);

    return (
      <FieldSet legend={translate('RootFolders')}>
        <PageSectionContent
          errorMessage={translate('UnableToLoadRootFolders')}
          {...otherProps}
        >
          <Table columns={columns}>
            <TableBody>
              {
                [...items].sort(sortByName).map((item) => {
                  return (
                    <RootFolder
                      key={item.id}
                      {...item}
                      mediaTypeLabel={getRootFolderMediaTypeLabel(item)}
                      isMediaTypeConfigured={getRootFolderMediaTypes(item).length > 0}
                      isDefaultAudiobookRootFolder={item.isEffectiveDefaultAudiobook || false}
                      isDefaultEbookRootFolder={item.isEffectiveDefaultEbook || false}
                      profileRows={this.getProfileRows(item)}
                      isDeleting={isDeleting}
                      deleteError={deleteError}
                      onConfirmDeleteRootFolder={onConfirmDeleteRootFolder}
                    />
                  );
                })
              }
            </TableBody>
          </Table>

          <div className={styles.addRootFolderContainer}>
            <Button onPress={this.onAddRootFolderPress}>
              <Icon
                className={styles.addRootFolderIcon}
                name={icons.ADD}
              />
              {translate('AddRootFolder')}
            </Button>

            <SpinnerButton
              className={styles.backfillRootFoldersButton}
              isDisabled={
                isBackfillingAuthorRootFolders ||
                this.state.isPreviewingAuthorRoots ||
                this.state.isSubmittingAuthorRootBackfill ||
                this._isSubmittingAuthorRootBackfill
              }
              isSpinning={this.state.isPreviewingAuthorRoots || this.state.isSubmittingAuthorRootBackfill}
              onPress={this.onReviewAuthorRootBackfillPress}
            >
              {translate(
                this.state.backfillPreviewError ?
                  'RetryAuthorRootFolderBackfillPreview' :
                  'ReviewMissingAuthorRoots'
              )}
            </SpinnerButton>
          </div>

          {
            this.state.backfillPreviewError &&
              <div className={styles.backfillError} role="alert">
                {this.state.backfillPreviewError}
              </div>
          }

          <ConfirmModal
            isOpen={this.state.isBackfillPreviewModalOpen}
            kind={canBackfill ? kinds.PRIMARY : kinds.DEFAULT}
            title={translate('BackfillAuthorRootFoldersTitle')}
            message={this.renderAuthorRootBackfillPreview()}
            hideCancelButton={!canBackfill}
            confirmLabel={canBackfill ?
              translate('FillMissingAuthorRoots') :
              translate('Close')}
            isSpinning={isBackfillingAuthorRootFolders || this.state.isSubmittingAuthorRootBackfill}
            onConfirm={this.onConfirmAuthorRootBackfill}
            onCancel={this.onBackfillPreviewModalClose}
          />

          <EditRootFolderModalConnector
            isOpen={isAddRootFolderModalOpen}
            onModalClose={this.onAddRootFolderModalClose}
          />
        </PageSectionContent>
      </FieldSet>
    );
  }
}

RootFolders.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isDeleting: PropTypes.bool,
  deleteError: PropTypes.object,
  isBackfillingAuthorRootFolders: PropTypes.bool,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  qualityProfiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  metadataProfiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  onConfirmDeleteRootFolder: PropTypes.func.isRequired,
  onBackfillAuthorRootFolders: PropTypes.func.isRequired,
  setRootFolderValue: PropTypes.func
};

export default RootFolders;
