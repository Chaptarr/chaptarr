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

class QuickstartGrimmorySection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditNotificationModalOpen: false,
      isDeleteNotificationModalOpen: false,
      pendingOpenGrimmory: false,
      schemaSelectionError: false
    };
  }

  componentDidMount() {
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

    if (this.state.pendingOpenGrimmory && schemaJustBecameUsable) {
      this.openAddGrimmoryNotification();
    }

    if (this.state.pendingOpenGrimmory && schemaFetchJustFailed) {
      this.setState({ pendingOpenGrimmory: false });
    }
  }

  //
  // Listeners

  onButtonPress = () => {
    const {
      grimmoryNotification,
      notificationsState,
      fetchNotificationSchema
    } = this.props;

    if (grimmoryNotification) {
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
          pendingOpenGrimmory: true,
          schemaSelectionError: false
        });
        return;
      }

      this.openAddGrimmoryNotification();
    }
  };

  openAddGrimmoryNotification = () => {
    const schemaItems = Array.isArray(this.props.notificationsState?.schema) ? this.props.notificationsState.schema : [];
    const hasGrimmorySchema = schemaItems.some((schemaItem) => schemaItem.implementation === 'Grimmory');

    if (!hasGrimmorySchema) {
      this.setState({
        pendingOpenGrimmory: false,
        schemaSelectionError: true
      });
      return;
    }

    this.props.selectNotificationSchema({ implementation: 'Grimmory' });
    this.setState({
      isEditNotificationModalOpen: true,
      pendingOpenGrimmory: false,
      schemaSelectionError: false
    });
  };

  onEditNotificationModalClose = () => {
    this.setState({
      isEditNotificationModalOpen: false,
      pendingOpenGrimmory: false,
      schemaSelectionError: false
    });

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
    const { grimmoryNotification } = this.props;

    if (grimmoryNotification) {
      this.props.deleteNotification({ id: grimmoryNotification.id });
    }

    this.onDeleteNotificationModalClose();
  };

  onTestConnectionSuccess = () => {
    const { markSectionInteracted } = this.props;
    if (markSectionInteracted) {
      markSectionInteracted({ section: 'grimmory' });
    }
  };

  //
  // Render

  render() {
    const {
      hasActiveGrimmory,
      grimmoryNotification
    } = this.props;

    const {
      isEditNotificationModalOpen,
      isDeleteNotificationModalOpen,
      pendingOpenGrimmory,
      schemaSelectionError
    } = this.state;

    const buttonText = grimmoryNotification ?
      translate('ConfigureName', { name: 'Grimmory' }) :
      translate('AddName', { name: 'Grimmory' });
    const isAddSchemaLoading = !grimmoryNotification &&
      (this.props.notificationsState.isSchemaFetching || pendingOpenGrimmory);
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
            id={grimmoryNotification ? grimmoryNotification.id : 0}
            isOpen={isEditNotificationModalOpen}
            onModalClose={this.onEditNotificationModalClose}
            onDeleteNotificationPress={this.onDeleteNotificationPress}
            onTestConnectionSuccess={this.onTestConnectionSuccess}
          />

          <ConfirmModal
            isOpen={isDeleteNotificationModalOpen}
            kind={kinds.DANGER}
            title={translate('DeleteNotification')}
            message={translate('DeleteNotificationMessageText', { name: grimmoryNotification?.name || '' })}
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
          {translate('QuickstartGrimmoryConnectHeader')}
        </h2>
        {!hasActiveGrimmory && (
          <div className={styles.sectionDescription}>
            {translate('QuickstartGrimmoryConnectDescription')}
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
          id={grimmoryNotification ? grimmoryNotification.id : 0}
          isOpen={isEditNotificationModalOpen}
          onModalClose={this.onEditNotificationModalClose}
          onDeleteNotificationPress={this.onDeleteNotificationPress}
          onTestConnectionSuccess={this.onTestConnectionSuccess}
        />

        <ConfirmModal
          isOpen={isDeleteNotificationModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteNotification')}
          message={translate('DeleteNotificationMessageText', { name: grimmoryNotification?.name || '' })}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteNotification}
          onCancel={this.onDeleteNotificationModalClose}
        />
      </div>
    );
  }
}

QuickstartGrimmorySection.propTypes = {
  hasActiveGrimmory: PropTypes.bool,
  grimmoryNotification: PropTypes.object,
  compact: PropTypes.bool,
  notificationsState: PropTypes.object.isRequired,
  fetchNotificationSchema: PropTypes.func.isRequired,
  selectNotificationSchema: PropTypes.func.isRequired,
  deleteNotification: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func,
  fetchNotifications: PropTypes.func
};

export default connect(null, { deleteNotification })(QuickstartGrimmorySection);
