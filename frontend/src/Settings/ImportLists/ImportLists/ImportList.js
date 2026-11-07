import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Card from 'Components/Card';
import Button from 'Components/Link/Button';
import Label from 'Components/Label';
import MediaTypeScope from 'Components/MediaTypeScope';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { kinds, sizes } from 'Helpers/Props';
import { getImportListMediaTypeScope } from 'Helpers/Props/mediaTypeScopes';
import formatShortTimeSpan from 'Utilities/Date/formatShortTimeSpan';
import translate from 'Utilities/String/translate';
import EditImportListModalConnector from './EditImportListModalConnector';
import styles from './ImportList.css';
import ImportListSyncModal from './ImportListSyncModal';

class ImportList extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditImportListModalOpen: false,
      isDeleteImportListModalOpen: false,
      isSyncModalOpen: false
    };
  }

  //
  // Listeners

  onEditImportListPress = () => {
    this.setState({ isEditImportListModalOpen: true });
  };

  onEditImportListModalClose = () => {
    this.setState({ isEditImportListModalOpen: false });
  };

  onDeleteImportListPress = () => {
    this.setState({
      isEditImportListModalOpen: false,
      isDeleteImportListModalOpen: true
    });
  };

  onDeleteImportListModalClose= () => {
    this.setState({ isDeleteImportListModalOpen: false });
  };

  onConfirmDeleteImportList = () => {
    this.props.onConfirmDeleteImportList(this.props.id);
  };

  onSyncHistoryPress = (event) => {
    event.stopPropagation();
    this.setState({ isSyncModalOpen: true });
  };

  onSyncModalClose = () => {
    this.setState({ isSyncModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      id,
      name,
      enableAutomaticAdd,
      minRefreshInterval,
      shouldSearch,
      implementation,
      fields,
      hardcoverUsername,
      hardcoverAvatarUrl
    } = this.props;

    const showHardcoverAccount = implementation === 'HardcoverLibraryImportList' && hardcoverUsername;
    const mediaTypeScope = getImportListMediaTypeScope(fields);

    return (
      <Card
        className={styles.list}
        overlayContent={true}
        onPress={this.onEditImportListPress}
      >
        <div className={showHardcoverAccount ? styles.nameWithHardcoverAccount : styles.name}>
          {name}
        </div>

        <MediaTypeScope mediaType={mediaTypeScope} />

        {
          showHardcoverAccount ?
            <div className={styles.hardcoverAccount}>
              {hardcoverAvatarUrl ? (
                <img
                  className={styles.hardcoverAvatar}
                  src={hardcoverAvatarUrl}
                  alt=""
                />
              ) : null}
              <div className={styles.hardcoverUsername}>
                {hardcoverUsername}
              </div>
            </div> : null
        }

        <div className={styles.enabled}>
          {
            enableAutomaticAdd &&
              <Label kind={kinds.SUCCESS}>
                {translate('AutomaticAdd')}
              </Label>
          }

          {
            shouldSearch &&
              <Label kind={kinds.SUCCESS}>
                {translate('AutomaticSearch')}
              </Label>
          }
        </div>

        <div className={styles.enabled}>
          <Label
            kind={kinds.INFO}
            title={translate('ListRefreshInterval')}
          >
            {`${translate('Refresh')}: ${formatShortTimeSpan(minRefreshInterval)}`}
          </Label>
        </div>

        <div className={styles.actions}>
          <Button
            size={sizes.SMALL}
            onPress={this.onSyncHistoryPress}
          >
            {translate('PreviewNextSync')}
          </Button>
        </div>

        <ImportListSyncModal
          id={id}
          name={name}
          isOpen={this.state.isSyncModalOpen}
          onModalClose={this.onSyncModalClose}
        />

        <EditImportListModalConnector
          id={id}
          isOpen={this.state.isEditImportListModalOpen}
          onModalClose={this.onEditImportListModalClose}
          onDeleteImportListPress={this.onDeleteImportListPress}
        />

        <ConfirmModal
          isOpen={this.state.isDeleteImportListModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteImportList')}
          message={translate('DeleteImportListMessageText', { name })}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteImportList}
          onCancel={this.onDeleteImportListModalClose}
        />
      </Card>
    );
  }
}

ImportList.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  enableAutomaticAdd: PropTypes.bool.isRequired,
  minRefreshInterval: PropTypes.string.isRequired,
  shouldSearch: PropTypes.bool.isRequired,
  implementation: PropTypes.string.isRequired,
  fields: PropTypes.arrayOf(PropTypes.object),
  hardcoverUsername: PropTypes.string,
  hardcoverAvatarUrl: PropTypes.string,
  onConfirmDeleteImportList: PropTypes.func.isRequired
};

ImportList.defaultProps = {
  fields: []
};

export default ImportList;
