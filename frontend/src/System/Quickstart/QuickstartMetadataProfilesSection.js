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
import { icons, kinds } from 'Helpers/Props';
import EditMetadataProfileModalConnector from 'Settings/Profiles/Metadata/EditMetadataProfileModalConnector';
import { cloneMetadataProfile, deleteMetadataProfile, fetchMetadataProfiles } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import styles from './QuickstartProfiles.css';

function createMapStateToProps() {
  return createSelector(
    (state) => state.settings.metadataProfiles,
    (metadataProfiles) => {
      return {
        ...metadataProfiles,
        profiles: metadataProfiles.items.filter((profile) => profile.name !== 'None')
      };
    }
  );
}

const mapDispatchToProps = {
  fetchMetadataProfiles,
  deleteMetadataProfile,
  cloneMetadataProfile
};

class QuickstartMetadataProfilesSection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isMetadataProfileModalOpen: false,
      editProfileId: null,
      newProfileType: null,
      isDeleteMetadataProfileModalOpen: false,
      deleteProfileId: null
    };
  }

  componentDidMount() {
    if (!this.props.isPopulated) {
      this.props.fetchMetadataProfiles();
    }
  }

  //
  // Listeners

  onEditProfilePress = (id) => {
    // Mark section as interacted when user opens any profile
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'metadataProfiles' });
    }

    this.setState({
      isMetadataProfileModalOpen: true,
      editProfileId: id,
      newProfileType: null
    });
  };

  onAddProfilePress = (profileType) => {
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'metadataProfiles' });
    }

    this.setState({
      isMetadataProfileModalOpen: true,
      editProfileId: null,
      newProfileType: profileType
    });
  };

  onModalClose = () => {
    this.setState({
      isMetadataProfileModalOpen: false,
      editProfileId: null,
      newProfileType: null
    });
  };

  onDeleteMetadataProfilePress = () => {
    this.setState({
      isMetadataProfileModalOpen: false,
      isDeleteMetadataProfileModalOpen: true,
      deleteProfileId: this.state.editProfileId
    });
  };

  onDeleteMetadataProfileModalClose = () => {
    this.setState({
      isDeleteMetadataProfileModalOpen: false,
      deleteProfileId: null
    });
  };

  onConfirmDeleteMetadataProfile = () => {
    const { deleteProfileId } = this.state;

    this.props.deleteMetadataProfile({ id: deleteProfileId });
    this.onDeleteMetadataProfileModalClose();
  };

  onCloneProfilePress = (id) => {
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'metadataProfiles' });
    }

    this.props.cloneMetadataProfile({ id });
    // Open modal without ID to edit the cloned profile
    this.setState({
      isMetadataProfileModalOpen: true,
      editProfileId: null,
      newProfileType: null
    });
  };

  //
  // Render

  renderMetadataProfile(profile) {
    const {
      id,
      name,
      profileType,
      minPopularity,
      minPages,
      ignored
    } = profile;
    let ignoredTerms = [];

    if (Array.isArray(ignored)) {
      ignoredTerms = ignored;
    } else if (typeof ignored === 'string') {
      ignoredTerms = ignored.split(',');
    }

    return (
      <Card
        key={id}
        className={styles.profileCard}
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

          <div className={styles.profileDetails}>
            {minPopularity !== undefined && minPopularity > 0 && (
              <div className={styles.metadataItem}>
                <span className={styles.metadataLabel}>{translate('MinPopularityLabel')}</span>
                <span className={styles.metadataValue}>{minPopularity}</span>
              </div>
            )}

            {minPages !== undefined && minPages > 0 && (
              <div className={styles.metadataItem}>
                <span className={styles.metadataLabel}>{translate('MinPagesLabel')}</span>
                <span className={styles.metadataValue}>{minPages}</span>
              </div>
            )}

            {ignored && ignored.length > 0 && (
              <div className={styles.ignoredTerms}>
                <div className={styles.ignoredTermsLabel}>{translate('IgnoredTermsLabel')}</div>
                <div className={styles.ignoredTermsList}>
                  {
                    ignoredTerms.map((term, index) => {
                      return (
                        <Label
                          key={index}
                          kind={kinds.DANGER}
                        >
                          {typeof term === 'string' ? term.trim() : term}
                        </Label>
                      );
                    })
                  }
                </div>
              </div>
            )}
          </div>
        </div>
      </Card>
    );
  }

  render() {
    const {
      isFetching,
      isPopulated,
      isDeleting,
      profiles
    } = this.props;

    const {
      isMetadataProfileModalOpen,
      isDeleteMetadataProfileModalOpen,
      editProfileId,
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
          <div>{translate('QuickstartUnableToLoadMetadataProfiles')}</div>
        </div>
      );
    }

    // Separate profiles by ProfileType
    const audiobookProfiles = profiles.filter((p) => p.profileType === 1); // Audiobook
    const ebookProfiles = profiles.filter((p) => p.profileType === 2); // Ebook
    const deleteProfile = profiles.find((profile) => profile.id === deleteProfileId);

    return (
      <div className={styles.profilesContainer}>
        <div className={styles.profilesTwoColumn}>
          <div className={styles.profileColumn}>
            <h3 className={styles.columnHeader}>{translate('AudiobookMetadataProfiles')}</h3>
            {
              audiobookProfiles.map((profile) => this.renderMetadataProfile(profile))
            }
            <Card
              className={styles.addProfileCard}
              onPress={() => this.onAddProfilePress(1)}
            >
              <div className={styles.addProfileContent}>
                <Icon
                  name={icons.ADD}
                  size={45}
                />
                <div className={styles.addProfileText}>{translate('AddAudiobookProfile')}</div>
              </div>
            </Card>
          </div>

          <div className={styles.profileColumn}>
            <h3 className={styles.columnHeader}>{translate('EbookMetadataProfiles')}</h3>
            {
              ebookProfiles.map((profile) => this.renderMetadataProfile(profile))
            }
            <Card
              className={styles.addProfileCard}
              onPress={() => this.onAddProfilePress(2)}
            >
              <div className={styles.addProfileContent}>
                <Icon
                  name={icons.ADD}
                  size={45}
                />
                <div className={styles.addProfileText}>{translate('AddEbookProfile')}</div>
              </div>
            </Card>
          </div>
        </div>

        <EditMetadataProfileModalConnector
          isOpen={isMetadataProfileModalOpen}
          id={editProfileId}
          profileType={this.state.newProfileType}
          onModalClose={this.onModalClose}
          onDeleteMetadataProfilePress={this.onDeleteMetadataProfilePress}
        />

        <ConfirmModal
          isOpen={isDeleteMetadataProfileModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteMetadataProfile')}
          message={translate('DeleteMetadataProfileMessageText', { name: deleteProfile?.name || '' })}
          confirmLabel={translate('Delete')}
          isSpinning={isDeleting}
          onConfirm={this.onConfirmDeleteMetadataProfile}
          onCancel={this.onDeleteMetadataProfileModalClose}
        />
      </div>
    );
  }
}

QuickstartMetadataProfilesSection.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  isDeleting: PropTypes.bool.isRequired,
  error: PropTypes.object,
  profiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  fetchMetadataProfiles: PropTypes.func.isRequired,
  deleteMetadataProfile: PropTypes.func.isRequired,
  cloneMetadataProfile: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func
};

export default connect(createMapStateToProps, mapDispatchToProps)(QuickstartMetadataProfilesSection);
