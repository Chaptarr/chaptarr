import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import PageSectionContent from 'Components/Page/PageSectionContent';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { icons } from 'Helpers/Props';
import { getRootFolderMediaTypeLabel, getRootFolderMediaTypes } from 'Helpers/Props/folderTypes';
import sortByName from 'Utilities/Array/sortByName';
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

class RootFolders extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddRootFolderModalOpen: false
    };
  }

  //
  // Listeners

  onAddRootFolderPress = () => {
    const { setRootFolderValue } = this.props;

    // Set minimal default values - user will configure per-media-type settings via toggle
    if (setRootFolderValue) {
      setRootFolderValue({ name: 'name', value: 'Media Library' });
      setRootFolderValue({ name: 'monitorExisting', value: 1 }); // TRI-STATE: 1 = All books
      setRootFolderValue({ name: 'monitorFuture', value: true });
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
      onConfirmDeleteRootFolder,
      ...otherProps
    } = this.props;

    const {
      isAddRootFolderModalOpen
    } = this.state;

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
          </div>

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
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  qualityProfiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  metadataProfiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  onConfirmDeleteRootFolder: PropTypes.func.isRequired,
  setRootFolderValue: PropTypes.func
};

export default RootFolders;
