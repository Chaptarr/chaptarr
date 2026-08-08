import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Card from 'Components/Card';
import Label from 'Components/Label';
import MediaTypeScope from 'Components/MediaTypeScope';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import Tooltip from 'Components/Tooltip/Tooltip';
import { kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import EditQualityProfileModalConnector from './EditQualityProfileModalConnector';
import styles from './QualityProfile.css';

function findQualityName(items, qualityId) {
  if (!qualityId) {
    return null;
  }

  for (const item of items || []) {
    if (item.quality?.id === qualityId) {
      return item.quality.name;
    }

    const groupMatch = findQualityName(item.items, qualityId);
    if (groupMatch) {
      return groupMatch;
    }
  }

  return null;
}

class QualityProfile extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditQualityProfileModalOpen: false,
      isDeleteQualityProfileModalOpen: false
    };
  }

  //
  // Listeners

  onEditQualityProfilePress = () => {
    this.setState({ isEditQualityProfileModalOpen: true });
  };

  onEditQualityProfileModalClose = () => {
    this.setState({ isEditQualityProfileModalOpen: false });
  };

  onDeleteQualityProfilePress = () => {
    this.setState({
      isEditQualityProfileModalOpen: false,
      isDeleteQualityProfileModalOpen: true
    });
  };

  onDeleteQualityProfileModalClose = () => {
    this.setState({ isDeleteQualityProfileModalOpen: false });
  };

  onConfirmDeleteQualityProfile = () => {
    this.props.onConfirmDeleteQualityProfile(this.props.id);
  };

  //
  // Render

  render() {
    const {
      id,
      name,
      profileType,
      upgradeAllowed,
      convertToQualityId,
      cutoff,
      items,
      isDeleting
    } = this.props;
    const convertToQualityName = findQualityName(items, convertToQualityId);

    return (
      <Card
        className={styles.qualityProfile}
        overlayContent={true}
        onPress={this.onEditQualityProfilePress}
      >
        <div className={styles.nameContainer}>
          <div className={styles.name}>
            {name}
          </div>
        </div>

        <MediaTypeScope mediaType={profileType} />

        <div className={styles.qualities}>
          {
            // Display highest preference at the top: reverse rendered order
            items
              .filter((item) => item.allowed)
              .map((item) => {
                if (item.quality) {
                  const isCutoff = upgradeAllowed && item.quality.id === cutoff;

                  return (
                    <Label
                      key={item.quality.id}
                      kind={isCutoff ? kinds.INFO : kinds.DEFAULT}
                      title={isCutoff ? translate('IsCutoffUpgradeUntilThisQualityIsMetOrExceeded') : null}
                    >
                      {item.quality.name}
                    </Label>
                  );
                }

                const isCutoff = upgradeAllowed && item.id === cutoff;

                return (
                  <Tooltip
                    key={item.id}
                    className={styles.tooltipLabel}
                    anchor={
                      <Label
                        kind={isCutoff ? kinds.INFO : kinds.DEFAULT}
                        title={isCutoff ? translate('IsCutoffCutoff') : null}
                      >
                        {item.name}
                      </Label>
                    }
                    tooltip={
                      <div>
                        {
                          item.items.map((groupItem) => (
                            <Label
                              key={groupItem.quality.id}
                              kind={isCutoff ? kinds.INFO : kinds.DEFAULT}
                              title={isCutoff ? translate('IsCutoffCutoff') : null}
                            >
                              {groupItem.quality.name}
                            </Label>
                          ))
                        }
                      </div>
                    }
                    kind={kinds.INVERSE}
                    position={tooltipPositions.TOP}
                  />
                );
              })
              .reverse()
          }
        </div>

        {
          convertToQualityId ?
            <div className={styles.convertTo}>
              <Label kind={kinds.SUCCESS}>
                {translate('QualityProfileConvertTo', { quality: convertToQualityName || translate('Unknown') })}
              </Label>
            </div> :
            null
        }

        <EditQualityProfileModalConnector
          id={id}
          isOpen={this.state.isEditQualityProfileModalOpen}
          onModalClose={this.onEditQualityProfileModalClose}
          onDeleteQualityProfilePress={this.onDeleteQualityProfilePress}
        />

        <ConfirmModal
          isOpen={this.state.isDeleteQualityProfileModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteQualityProfile')}
          message={translate('DeleteQualityProfileMessageText', { name })}
          confirmLabel={translate('Delete')}
          isSpinning={isDeleting}
          onConfirm={this.onConfirmDeleteQualityProfile}
          onCancel={this.onDeleteQualityProfileModalClose}
        />
      </Card>
    );
  }
}

QualityProfile.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  profileType: PropTypes.oneOfType([PropTypes.string, PropTypes.number]),
  upgradeAllowed: PropTypes.bool.isRequired,
  convertToQualityId: PropTypes.number,
  cutoff: PropTypes.number.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isDeleting: PropTypes.bool.isRequired,
  onConfirmDeleteQualityProfile: PropTypes.func.isRequired
};

export default QualityProfile;
