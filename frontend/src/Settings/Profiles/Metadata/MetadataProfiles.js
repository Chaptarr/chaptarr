import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Card from 'Components/Card';
import FieldSet from 'Components/FieldSet';
import Icon from 'Components/Icon';
import PageSectionContent from 'Components/Page/PageSectionContent';
import { icons, metadataProfileNames } from 'Helpers/Props';
import sortByName from 'Utilities/Array/sortByName';
import translate from 'Utilities/String/translate';
import EditMetadataProfileModalConnector from './EditMetadataProfileModalConnector';
import MetadataProfile from './MetadataProfile';
import styles from './MetadataProfiles.css';

class MetadataProfiles extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isMetadataProfileModalOpen: false,
      editProfileId: null,
      newProfileType: null
    };
  }

  //
  // Listeners

  onCloneMetadataProfilePress = (id) => {
    this.props.onCloneMetadataProfilePress(id);
    this.setState({ isMetadataProfileModalOpen: true });
  };

  onEditMetadataProfilePress = (id, profileType) => {
    this.setState({ 
      isMetadataProfileModalOpen: true,
      editProfileId: id,
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

  //
  // Render

  render() {
    const {
      items,
      isDeleting,
      onConfirmDeleteMetadataProfile,
      ...otherProps
    } = this.props;

    // Filter out NONE profiles and separate by type
    const filteredItems = items.filter((item) => item.name !== metadataProfileNames.NONE);
    
    // Separate profiles by ProfileType
    const audiobookProfiles = filteredItems.filter(p => p.profileType === 1).sort(sortByName); // Audiobook
    const ebookProfiles = filteredItems.filter(p => p.profileType === 2).sort(sortByName); // Ebook

    return (
      <FieldSet legend={translate('MetadataProfiles')}>
        <PageSectionContent
          errorMessage={translate('UnableToLoadMetadataProfiles')}
          {...otherProps}
        >
          <div className={styles.profilesColumns}>
            <div className={styles.profileColumn}>
              <h3 className={styles.columnHeader}>{translate('AudiobookMetadataProfiles')}</h3>
              <div className={styles.metadataProfiles}>
                {
                  audiobookProfiles.map((item) => {
                    return (
                      <MetadataProfile
                        key={item.id}
                        {...item}
                        isDeleting={isDeleting}
                        onConfirmDeleteMetadataProfile={onConfirmDeleteMetadataProfile}
                        onCloneMetadataProfilePress={this.onCloneMetadataProfilePress}
                      />
                    );
                  })
                }

                <Card
                  className={styles.addMetadataProfile}
                  onPress={() => this.onEditMetadataProfilePress(null, 1)}
                >
                  <div className={styles.center}>
                    <Icon
                      name={icons.ADD}
                      size={45}
                    />
                    <div className={styles.addText}>{translate('AddAudiobookProfile')}</div>
                  </div>
                </Card>
              </div>
            </div>

            <div className={styles.profileColumn}>
              <h3 className={styles.columnHeader}>{translate('EbookMetadataProfiles')}</h3>
              <div className={styles.metadataProfiles}>
                {
                  ebookProfiles.map((item) => {
                    return (
                      <MetadataProfile
                        key={item.id}
                        {...item}
                        isDeleting={isDeleting}
                        onConfirmDeleteMetadataProfile={onConfirmDeleteMetadataProfile}
                        onCloneMetadataProfilePress={this.onCloneMetadataProfilePress}
                      />
                    );
                  })
                }

                <Card
                  className={styles.addMetadataProfile}
                  onPress={() => this.onEditMetadataProfilePress(null, 2)}
                >
                  <div className={styles.center}>
                    <Icon
                      name={icons.ADD}
                      size={45}
                    />
                    <div className={styles.addText}>{translate('AddEbookProfile')}</div>
                  </div>
                </Card>
              </div>
            </div>
          </div>

          <EditMetadataProfileModalConnector
            isOpen={this.state.isMetadataProfileModalOpen}
            id={this.state.editProfileId}
            profileType={this.state.newProfileType}
            onModalClose={this.onModalClose}
          />
        </PageSectionContent>
      </FieldSet>
    );
  }
}

MetadataProfiles.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isDeleting: PropTypes.bool.isRequired,
  onConfirmDeleteMetadataProfile: PropTypes.func.isRequired,
  onCloneMetadataProfilePress: PropTypes.func.isRequired
};

export default MetadataProfiles;
