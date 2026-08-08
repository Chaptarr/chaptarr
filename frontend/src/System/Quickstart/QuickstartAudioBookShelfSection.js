import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { kinds } from 'Helpers/Props';
import EditNotificationModalConnector from 'Settings/Notifications/Notifications/EditNotificationModalConnector';
import { deleteNotification } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import styles from './Quickstart.css';

class QuickstartAudioBookShelfSection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditNotificationModalOpen: false,
      isDeleteNotificationModalOpen: false,
      pendingOpenAudioBookShelf: false,
      schemaSelectionError: false
    };
  }

  componentDidMount() {
    // Pre-fetch the schema so it's ready when user clicks
    if (!this.props.notificationsState.isSchemaPopulated) {
      this.props.fetchNotificationSchema();
    }
  }

  componentDidUpdate(prevProps) {
    const previousSchemaWasUsable = prevProps.notificationsState.isSchemaPopulated &&
      !prevProps.notificationsState.schemaError;
    const schemaIsUsable = this.props.notificationsState.isSchemaPopulated &&
      !this.props.notificationsState.schemaError;
    const schemaJustBecameUsable = !previousSchemaWasUsable && schemaIsUsable;
    const schemaFetchJustFailed = prevProps.notificationsState.isSchemaFetching &&
      !this.props.notificationsState.isSchemaFetching &&
      this.props.notificationsState.schemaError;

    if (this.state.pendingOpenAudioBookShelf && schemaJustBecameUsable) {
      this.openAddAudioBookShelfNotification();
    }

    if (this.state.pendingOpenAudioBookShelf && schemaFetchJustFailed) {
      this.setState({ pendingOpenAudioBookShelf: false });
    }
  }

  //
  // Listeners

  onButtonPress = () => {
    const {
      audioBookShelfNotification,
      notificationsState,
      fetchNotificationSchema
    } = this.props;

    if (audioBookShelfNotification) {
      // Edit existing AudioBookShelf notification
      this.setState({
        isEditNotificationModalOpen: true,
        schemaSelectionError: false
      });
    } else {
      const hasUsableSchema = notificationsState.isSchemaPopulated && !notificationsState.schemaError;

      if (!hasUsableSchema) {
        if (!notificationsState.isSchemaFetching && fetchNotificationSchema) {
          fetchNotificationSchema();
        }

        this.setState({
          pendingOpenAudioBookShelf: true,
          schemaSelectionError: false
        });
        return;
      }

      this.openAddAudioBookShelfNotification();
    }
  };

  openAddAudioBookShelfNotification = () => {
    const schemaItems = Array.isArray(this.props.notificationsState?.schema) ? this.props.notificationsState.schema : [];
    const hasAudioBookShelfSchema = schemaItems.some((schemaItem) => schemaItem.implementation === 'AudioBookShelf');

    if (!hasAudioBookShelfSchema) {
      this.setState({
        pendingOpenAudioBookShelf: false,
        schemaSelectionError: true
      });
      return;
    }

    this.props.selectNotificationSchema({ implementation: 'AudioBookShelf' });
    this.setState({
      isEditNotificationModalOpen: true,
      pendingOpenAudioBookShelf: false,
      schemaSelectionError: false
    });
  };

  onEditNotificationModalClose = () => {
    this.setState({
      isEditNotificationModalOpen: false,
      pendingOpenAudioBookShelf: false,
      schemaSelectionError: false
    });

    // Refresh notifications to ensure we have the latest state
    // This will update the button text and state after deletion
    if (this.props.fetchNotifications) {
      this.props.fetchNotifications();
    }
  };

  onDeleteNotificationPress = () => {
    this.setState({
      isEditNotificationModalOpen: false,
      isDeleteNotificationModalOpen: true
    });
  };

  onDeleteNotificationModalClose = () => {
    this.setState({ isDeleteNotificationModalOpen: false });
  };

  onConfirmDeleteNotification = () => {
    const { audioBookShelfNotification } = this.props;

    if (audioBookShelfNotification) {
      this.props.deleteNotification({ id: audioBookShelfNotification.id });
    }

    this.onDeleteNotificationModalClose();
  };

  onTestConnectionSuccess = () => {
    // Mark this section as interacted when test connection succeeds
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'audioBookShelf' });
    }
  };

  //
  // Render

  render() {
    const {
      hasActiveAudioBookShelf,
      audioBookShelfNotification
    } = this.props;

    const {
      isEditNotificationModalOpen,
      isDeleteNotificationModalOpen,
      pendingOpenAudioBookShelf,
      schemaSelectionError
    } = this.state;

    const buttonText = audioBookShelfNotification ?
      translate('ConfigureName', { name: 'AudioBookShelf' }) :
      translate('AddName', { name: 'AudioBookShelf' });
    const isAddSchemaLoading = !audioBookShelfNotification &&
      (this.props.notificationsState.isSchemaFetching || pendingOpenAudioBookShelf);
    const schemaError = !this.props.notificationsState.isSchemaFetching &&
      (this.props.notificationsState.schemaError || schemaSelectionError);

    if (this.props.compact) {
      return (
        <>
          <div className={styles.quickstartCardActions}>
            <button
              className={styles.quickstartCardButton}
              onClick={this.onButtonPress}
              disabled={isAddSchemaLoading}
            >
              {buttonText}
            </button>
          </div>

          {
            schemaError &&
              <Alert kind={kinds.DANGER}>
                {translate('QuickstartUnableToLoadNotificationOptions')}
              </Alert>
          }

          <EditNotificationModalConnector
            id={audioBookShelfNotification ? audioBookShelfNotification.id : 0}
            isOpen={isEditNotificationModalOpen}
            onModalClose={this.onEditNotificationModalClose}
            onDeleteNotificationPress={this.onDeleteNotificationPress}
            onTestConnectionSuccess={this.onTestConnectionSuccess}
          />

          <ConfirmModal
            isOpen={isDeleteNotificationModalOpen}
            kind={kinds.DANGER}
            title={translate('DeleteNotification')}
            message={translate('DeleteNotificationMessageText', { name: audioBookShelfNotification?.name || '' })}
            confirmLabel={translate('Delete')}
            onConfirm={this.onConfirmDeleteNotification}
            onCancel={this.onDeleteNotificationModalClose}
          />
        </>
      );
    }

    return (
      <div className={styles.section}>
        <h2 className={styles.sectionHeader}>
          {translate('QuickstartAbsConnectHeader')}
        </h2>
        {!hasActiveAudioBookShelf && (
          <div className={styles.sectionDescription}>
            {translate('QuickstartAbsConnectDescription')}
          </div>
        )}

        <div className={styles.quickstartCardActions}>
          <button
            className={styles.quickstartCardButton}
            onClick={this.onButtonPress}
            disabled={isAddSchemaLoading}
          >
            {buttonText}
          </button>
        </div>

        {
          schemaError &&
            <Alert kind={kinds.DANGER}>
              {translate('QuickstartUnableToLoadNotificationOptions')}
            </Alert>
        }

        <EditNotificationModalConnector
          id={audioBookShelfNotification ? audioBookShelfNotification.id : 0}
          isOpen={isEditNotificationModalOpen}
          onModalClose={this.onEditNotificationModalClose}
          onDeleteNotificationPress={this.onDeleteNotificationPress}
          onTestConnectionSuccess={this.onTestConnectionSuccess}
        />

        <ConfirmModal
          isOpen={isDeleteNotificationModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteNotification')}
          message={translate('DeleteNotificationMessageText', { name: audioBookShelfNotification?.name || '' })}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteNotification}
          onCancel={this.onDeleteNotificationModalClose}
        />
      </div>
    );
  }
}

QuickstartAudioBookShelfSection.propTypes = {
  hasActiveAudioBookShelf: PropTypes.bool,
  audioBookShelfNotification: PropTypes.object,
  compact: PropTypes.bool,
  notificationsState: PropTypes.object.isRequired,
  fetchNotificationSchema: PropTypes.func.isRequired,
  selectNotificationSchema: PropTypes.func.isRequired,
  deleteNotification: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func,
  fetchNotifications: PropTypes.func
};

export default connect(null, { deleteNotification })(QuickstartAudioBookShelfSection);
