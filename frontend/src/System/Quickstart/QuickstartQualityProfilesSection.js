import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Card from 'Components/Card';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import MediaTypeScope from 'Components/MediaTypeScope';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import EditQualityProfileModalConnector from 'Settings/Profiles/Quality/EditQualityProfileModalConnector';
import { cloneQualityProfile, deleteQualityProfile, fetchQualityProfiles } from 'Store/Actions/settingsActions';
import createSortedSectionSelector from 'Store/Selectors/createSortedSectionSelector';
import translate from 'Utilities/String/translate';
import styles from './QuickstartProfiles.css';

function isAudiobookProfile(profile) {
  return profile.profileType === 1 || `${profile.profileType}`.toLowerCase() === 'audiobook';
}

function isEbookProfile(profile) {
  return profile.profileType === 2 || `${profile.profileType}`.toLowerCase() === 'ebook';
}

function createMapStateToProps() {
  return createSelector(
    createSortedSectionSelector('settings.qualityProfiles', (a, b) => {
      if (a.name < b.name) {
        return -1;
      }

      if (a.name > b.name) {
        return 1;
      }

      return 0;
    }),
    (qualityProfiles) => {
      return {
        ...qualityProfiles
      };
    }
  );
}

const mapDispatchToProps = {
  fetchQualityProfiles,
  deleteQualityProfile,
  cloneQualityProfile
};

class QuickstartQualityProfilesSection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isQualityProfileModalOpen: false,
      editProfileId: null,
      addProfileType: null,
      isDeleteQualityProfileModalOpen: false,
      deleteProfileId: null
    };
  }

  componentDidMount() {
    if (!this.props.isPopulated) {
      this.props.fetchQualityProfiles();
    }
  }

  //
  // Listeners

  onEditProfilePress = (id) => {
    // Mark section as interacted when user opens any profile
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'qualityProfiles' });
    }

    this.setState({
      isQualityProfileModalOpen: true,
      editProfileId: id,
      addProfileType: null
    });
  };

  onAddProfilePress = (profileType) => {
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'qualityProfiles' });
    }

    this.setState({
      isQualityProfileModalOpen: true,
      editProfileId: null,
      addProfileType: profileType
    });
  };

  onModalClose = () => {
    this.setState({
      isQualityProfileModalOpen: false,
      editProfileId: null,
      addProfileType: null
    });
  };

  onDeleteQualityProfilePress = () => {
    this.setState({
      isQualityProfileModalOpen: false,
      isDeleteQualityProfileModalOpen: true,
      deleteProfileId: this.state.editProfileId
    });
  };

  onDeleteQualityProfileModalClose = () => {
    this.setState({
      isDeleteQualityProfileModalOpen: false,
      deleteProfileId: null
    });
  };

  onConfirmDeleteQualityProfile = () => {
    const { deleteProfileId } = this.state;

    this.props.deleteQualityProfile({ id: deleteProfileId });
    this.onDeleteQualityProfileModalClose();
  };

  onCloneProfilePress = (id) => {
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'qualityProfiles' });
    }

    this.props.cloneQualityProfile({ id });
    // Open modal without ID to edit the cloned profile (uses pendingChanges).
    this.setState({
      isQualityProfileModalOpen: true,
      editProfileId: null,
      addProfileType: null
    });
  };

  //
  // Render

  renderQualityProfile(profile) {
    const {
      id,
      name,
      profileType,
      items,
      cutoff
    } = profile;

    if (!items || !Array.isArray(items)) {
      return (
        <Card
          key={id}
          className={styles.qualityProfileCard}
          overlayContent={true}
          onPress={() => this.onEditProfilePress(id)}
        >
          <div className={styles.profileContent}>
            <div className={styles.profileHeader}>
              <div className={styles.profileName}>{name}</div>
              <div
                className={styles.cloneButton}
                onClick={(e) => {
                  e.stopPropagation();
                  this.onCloneProfilePress(id);
                }}
              >
                <Icon
                  name={icons.CLONE}
                  size={12}
                />
              </div>
            </div>
            <MediaTypeScope mediaType={profileType} />

            <div className={styles.qualityProfileDetails}>
              <span>{translate('QuickstartNoQualitySettings')}</span>
            </div>
          </div>
        </Card>
      );
    }

    const isCutoff = (item) => {
      return item.quality?.id === cutoff || (item.items && item.items.some((i) => i.quality.id === cutoff));
    };

    return (
      <Card
        key={id}
        className={styles.qualityProfileCard}
        overlayContent={true}
        onPress={() => this.onEditProfilePress(id)}
      >
        <div className={styles.profileContent}>
          <div className={styles.profileHeader}>
            <div className={styles.profileName}>{name}</div>
            <div
              className={styles.cloneButton}
              onClick={(e) => {
                e.stopPropagation();
                this.onCloneProfilePress(id);
              }}
            >
              <Icon
                name={icons.CLONE}
                size={12}
              />
            </div>
          </div>
          <MediaTypeScope mediaType={profileType} />

          <div className={styles.qualityProfileDetails}>
            {
              // Display highest preference first (reverse rendered order to show best at top)
              items
                .map((item) => {
                  if (!item.allowed) {
                    return null;
                  }

                  const isCutoffItem = isCutoff(item);

                  if (item.quality) {
                    return (
                      <Label
                        key={item.quality.id}
                        kind={kinds.DEFAULT}
                        title={isCutoffItem ? translate('CutoffUnmet') : null}
                      >
                        {item.quality.name}
                      </Label>
                    );
                  }

                  if (item.items) {
                    const allowedItems = item.items.filter((i) => i.allowed);
                    const label = allowedItems.length === item.items.length ? item.name : `${item.name} (${allowedItems.length})`;

                    return (
                      <Tooltip
                        key={item.id}
                        anchor={
                          <Label
                            kind={kinds.DEFAULT}
                            title={isCutoffItem ? translate('CutoffUnmet') : null}
                          >
                            {label}
                          </Label>
                        }
                        tooltip={
                          <div>
                            {
                            // Reverse group display as well
                              allowedItems.slice().reverse().map((groupItem) => {
                                return (
                                  <Label
                                    key={groupItem.quality.id}
                                    kind={kinds.DEFAULT}
                                    title={groupItem.quality.id === cutoff ? translate('CutoffUnmet') : null}
                                  >
                                    {groupItem.quality.name}
                                  </Label>
                                );
                              })
                            }
                          </div>
                        }
                        position={tooltipPositions.RIGHT}
                      />
                    );
                  }

                  return null;
                })
                .reverse()
            }
          </div>
        </div>
      </Card>
    );
  }

  renderAddQualityProfileCard(label, profileType) {
    return (
      <Card
        className={styles.qualityAddProfileCard}
        onPress={() => this.onAddProfilePress(profileType)}
      >
        <div className={styles.addProfileContent}>
          <Icon
            name={icons.ADD}
            size={45}
          />
          <div className={styles.addProfileText}>{label}</div>
        </div>
      </Card>
    );
  }

  render() {
    const {
      isFetching,
      isPopulated,
      isDeleting,
      items
    } = this.props;

    const {
      isQualityProfileModalOpen,
      isDeleteQualityProfileModalOpen,
      editProfileId,
      addProfileType,
      deleteProfileId
    } = this.state;

    if (isFetching && !isPopulated) {
      return (
        <div className={styles.profilesContainer}>
          <LoadingIndicator />
        </div>
      );
    }

    if (!isPopulated) {
      return (
        <div className={styles.profilesContainer}>
          <div>{translate('QuickstartUnableToLoadQualityProfiles')}</div>
        </div>
      );
    }

    const audiobookProfiles = items.filter(isAudiobookProfile);
    const ebookProfiles = items.filter(isEbookProfile);
    const otherProfiles = items.filter((profile) => !isAudiobookProfile(profile) && !isEbookProfile(profile));
    const deleteProfile = items.find((profile) => profile.id === deleteProfileId);

    return (
      <div className={styles.profilesContainer}>
        <div className={styles.profilesTwoColumn}>
          <div className={styles.profileColumn}>
            <h3 className={styles.columnHeader}>{translate('AudiobookQualityProfiles')}</h3>

            {
              audiobookProfiles.map((profile) => this.renderQualityProfile(profile))
            }
            {this.renderAddQualityProfileCard(translate('AddAudiobookQualityProfile'), 'audiobook')}
          </div>

          <div className={styles.profileColumn}>
            <h3 className={styles.columnHeader}>{translate('EbookQualityProfiles')}</h3>
            {
              ebookProfiles.map((profile) => this.renderQualityProfile(profile))
            }
            {this.renderAddQualityProfileCard(translate('AddEbookQualityProfile'), 'ebook')}
          </div>
        </div>

        {
          otherProfiles.length > 0 &&
            <div className={styles.profilesGrid}>
              {
                otherProfiles.map((profile) => this.renderQualityProfile(profile))
              }
            </div>
        }

        <EditQualityProfileModalConnector
          isOpen={isQualityProfileModalOpen}
          id={editProfileId}
          profileType={addProfileType}
          onModalClose={this.onModalClose}
          onDeleteQualityProfilePress={this.onDeleteQualityProfilePress}
        />

        <ConfirmModal
          isOpen={isDeleteQualityProfileModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteQualityProfile')}
          message={translate('DeleteQualityProfileMessageText', { name: deleteProfile?.name || '' })}
          confirmLabel={translate('Delete')}
          isSpinning={isDeleting}
          onConfirm={this.onConfirmDeleteQualityProfile}
          onCancel={this.onDeleteQualityProfileModalClose}
        />
      </div>
    );
  }
}

QuickstartQualityProfilesSection.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isDeleting: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  fetchQualityProfiles: PropTypes.func.isRequired,
  deleteQualityProfile: PropTypes.func.isRequired,
  cloneQualityProfile: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func
};

export default connect(createMapStateToProps, mapDispatchToProps)(QuickstartQualityProfilesSection);
