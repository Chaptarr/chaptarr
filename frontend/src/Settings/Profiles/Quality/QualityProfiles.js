import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Card from 'Components/Card';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import PageSectionContent from 'Components/Page/PageSectionContent';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import EditQualityProfileModalConnector from './EditQualityProfileModalConnector';
import QualityProfile from './QualityProfile';
import styles from './QualityProfiles.css';

function isAudiobookProfile(profile) {
  return profile.profileType === 1 || `${profile.profileType}`.toLowerCase() === 'audiobook';
}

function isEbookProfile(profile) {
  return profile.profileType === 2 || `${profile.profileType}`.toLowerCase() === 'ebook';
}

class QualityProfiles extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isQualityProfileModalOpen: false,
      addProfileType: null
    };
  }

  //
  // Listeners

  onAddAudiobookQualityProfilePress = () => {
    this.setState({
      isQualityProfileModalOpen: true,
      addProfileType: 'audiobook'
    });
  };

  onAddEbookQualityProfilePress = () => {
    this.setState({
      isQualityProfileModalOpen: true,
      addProfileType: 'ebook'
    });
  };

  onModalClose = () => {
    this.setState({
      isQualityProfileModalOpen: false,
      addProfileType: null
    });
  };

  //
  // Render

  renderAddQualityProfileCard(label, onPress) {
    return (
      <Card
        className={styles.addQualityProfile}
        onPress={onPress}
      >
        <div className={styles.center}>
          <Icon
            name={icons.ADD}
            size={30}
          />
          <div className={styles.addQualityProfileLabel}>
            {label}
          </div>
        </div>
      </Card>
    );
  }

  renderQualityProfileSection(title, addLabel, onAddPress, profiles) {
    const {
      isDeleting,
      onConfirmDeleteQualityProfile
    } = this.props;

    return (
      <div className={styles.profileSection}>
        <div className={styles.profileSectionHeader}>
          {title}
        </div>

        <div className={styles.qualityProfiles}>
          {
            profiles.map((item) => {
              return (
                <QualityProfile
                  key={item.id}
                  {...item}
                  isDeleting={isDeleting}
                  onConfirmDeleteQualityProfile={onConfirmDeleteQualityProfile}
                />
              );
            })
          }

          {this.renderAddQualityProfileCard(addLabel, onAddPress)}
        </div>
      </div>
    );
  }

  renderUnknownQualityProfiles(profiles) {
    const {
      isDeleting,
      onConfirmDeleteQualityProfile
    } = this.props;

    if (!profiles.length) {
      return null;
    }

    return (
      <div className={styles.profileSection}>
        <div className={styles.profileSectionHeader}>
          {translate('OtherQualityProfiles')}
        </div>

        <div className={styles.qualityProfiles}>
          {
            profiles.map((item) => {
              return (
                <QualityProfile
                  key={item.id}
                  {...item}
                  isDeleting={isDeleting}
                  onConfirmDeleteQualityProfile={onConfirmDeleteQualityProfile}
                />
              );
            })
          }
        </div>
      </div>
    );
  }

  render() {
    const {
      items,
      ...otherProps
    } = this.props;
    const audiobookProfiles = items.filter(isAudiobookProfile);
    const ebookProfiles = items.filter(isEbookProfile);
    const otherProfiles = items.filter((item) => !isAudiobookProfile(item) && !isEbookProfile(item));

    return (
      <FieldSet legend={translate('QualityProfiles')}>
        <PageSectionContent
          errorMessage={translate('UnableToLoadQualityProfiles')}
          {...otherProps}
        >
          {this.renderQualityProfileSection(translate('AudiobookQualityProfiles'), translate('AddAudiobookQualityProfile'), this.onAddAudiobookQualityProfilePress, audiobookProfiles)}
          {this.renderQualityProfileSection(translate('EbookQualityProfiles'), translate('AddEbookQualityProfile'), this.onAddEbookQualityProfilePress, ebookProfiles)}
          {this.renderUnknownQualityProfiles(otherProfiles)}

          <EditQualityProfileModalConnector
            isOpen={this.state.isQualityProfileModalOpen}
            profileType={this.state.addProfileType}
            onModalClose={this.onModalClose}
          />
        </PageSectionContent>
      </FieldSet>
    );
  }
}

QualityProfiles.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isDeleting: PropTypes.bool.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onConfirmDeleteQualityProfile: PropTypes.func.isRequired
};

export default QualityProfiles;
