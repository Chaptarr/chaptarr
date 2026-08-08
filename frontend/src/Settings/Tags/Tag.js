import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Card from 'Components/Card';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import TagDetailsModal from './Details/TagDetailsModal';
import styles from './Tag.css';

class Tag extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isDetailsModalOpen: false,
      isDeleteTagModalOpen: false
    };
  }

  //
  // Listeners

  onShowDetailsPress = () => {
    this.setState({ isDetailsModalOpen: true });
  };

  onDetailsModalClose = () => {
    this.setState({ isDetailsModalOpen: false });
  };

  onDeleteTagPress = () => {
    this.setState({
      isDetailsModalOpen: false,
      isDeleteTagModalOpen: true
    });
  };

  onDeleteTagModalClose= () => {
    this.setState({ isDeleteTagModalOpen: false });
  };

  onConfirmDeleteTag = () => {
    this.props.onConfirmDeleteTag({ id: this.props.id });
  };

  //
  // Render

  render() {
    const {
      label,
      delayProfileIds,
      importListIds,
      notificationIds,
      restrictionIds,
      indexerIds,
      downloadClientIds,
      authorIds
    } = this.props;

    const {
      isDetailsModalOpen,
      isDeleteTagModalOpen
    } = this.state;

    const isTagUsed = !!(
      delayProfileIds.length ||
      importListIds.length ||
      notificationIds.length ||
      restrictionIds.length ||
      indexerIds.length ||
      downloadClientIds.length ||
      authorIds.length
    );

    return (
      <Card
        className={styles.tag}
        overlayContent={true}
        onPress={this.onShowDetailsPress}
      >
        <div className={styles.label}>
          {label}
        </div>

        {
          isTagUsed &&
            <div>
              {
                !!authorIds.length &&
                  <div>
                    {translate('TagDetailsAuthorsCount', { count: authorIds.length })}
                  </div>
              }

              {
                !!delayProfileIds.length &&
                  <div>
                    {translate('TagDetailsDelayProfilesCount', { count: delayProfileIds.length })}
                  </div>
              }

              {
                !!importListIds.length &&
                  <div>
                    {translate('TagDetailsImportListsCount', { count: importListIds.length })}
                  </div>
              }

              {
                !!notificationIds.length &&
                  <div>
                    {translate('TagDetailsConnectionsCount', { count: notificationIds.length })}
                  </div>
              }

              {
                !!restrictionIds.length &&
                  <div>
                    {translate('TagDetailsRestrictionsCount', { count: restrictionIds.length })}
                  </div>
              }

              {
                indexerIds.length ?
                  <div>
                    {translate('TagDetailsIndexersCount', { count: indexerIds.length })}
                  </div> :
                  null
              }

              {
                downloadClientIds.length ?
                  <div>
                    {translate('TagDetailsDownloadClientsCount', { count: downloadClientIds.length })}
                  </div> :
                  null
              }
            </div>
        }

        {
          !isTagUsed &&
            <div>
              {translate('NoLinks')}
            </div>
        }

        <TagDetailsModal
          label={label}
          isTagUsed={isTagUsed}
          authorIds={authorIds}
          delayProfileIds={delayProfileIds}
          importListIds={importListIds}
          notificationIds={notificationIds}
          restrictionIds={restrictionIds}
          indexerIds={indexerIds}
          downloadClientIds={downloadClientIds}
          isOpen={isDetailsModalOpen}
          onModalClose={this.onDetailsModalClose}
          onDeleteTagPress={this.onDeleteTagPress}
        />

        <ConfirmModal
          isOpen={isDeleteTagModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteTag')}
          message={translate('DeleteTagMessageText', [label])}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteTag}
          onCancel={this.onDeleteTagModalClose}
        />
      </Card>
    );
  }
}

Tag.propTypes = {
  id: PropTypes.number.isRequired,
  label: PropTypes.string.isRequired,
  delayProfileIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  importListIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  notificationIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  restrictionIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  indexerIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  downloadClientIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  authorIds: PropTypes.arrayOf(PropTypes.number).isRequired,
  onConfirmDeleteTag: PropTypes.func.isRequired
};

Tag.defaultProps = {
  delayProfileIds: [],
  importListIds: [],
  notificationIds: [],
  restrictionIds: [],
  indexerIds: [],
  downloadClientIds: [],
  authorIds: []
};

export default Tag;
