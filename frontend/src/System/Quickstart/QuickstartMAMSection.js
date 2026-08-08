import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { kinds } from 'Helpers/Props';
import AddIndexerModal from 'Settings/Indexers/Indexers/AddIndexerModal';
import EditIndexerModalConnector from 'Settings/Indexers/Indexers/EditIndexerModalConnector';
import { deleteIndexer } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import styles from './Quickstart.css';

class QuickstartMAMSection extends Component {

  constructor(props, context) {
    super(props, context);

    this.state = {
      isEditIndexerModalOpen: false,
      isAddIndexerModalOpen: false,
      editIndexerId: null,
      isDeleteIndexerModalOpen: false,
      deleteIndexerId: null,
      pendingOpenMam: false,
      schemaSelectionError: false
    };
  }

  componentDidMount() {
    if (!this.props.indexersState.isSchemaPopulated) {
      this.props.fetchIndexerSchema();
    }
  }

  componentDidUpdate(prevProps) {
    const previousSchemaWasUsable = prevProps.indexersState.isSchemaPopulated &&
      !prevProps.indexersState.schemaError;
    const schemaIsUsable = this.props.indexersState.isSchemaPopulated &&
      !this.props.indexersState.schemaError;
    const schemaJustBecameUsable = !previousSchemaWasUsable && schemaIsUsable;
    const schemaFetchJustFailed = prevProps.indexersState.isSchemaFetching &&
      !this.props.indexersState.isSchemaFetching &&
      this.props.indexersState.schemaError;

    if (this.state.pendingOpenMam && schemaJustBecameUsable) {
      this.openAddMamIndexer();
    }

    if (this.state.pendingOpenMam && schemaFetchJustFailed) {
      this.setState({ pendingOpenMam: false });
    }
  }

  onMAMButtonPress = () => {
    const {
      mamIndexer,
      indexersState,
      fetchIndexerSchema
    } = this.props;

    if (mamIndexer) {
      this.setState({
        isEditIndexerModalOpen: true,
        editIndexerId: mamIndexer.id,
        schemaSelectionError: false
      });
      return;
    }

    const hasUsableSchema = indexersState.isSchemaPopulated && !indexersState.schemaError;

    if (!hasUsableSchema) {
      if (!indexersState.isSchemaFetching && fetchIndexerSchema) {
        fetchIndexerSchema();
      }

      this.setState({
        pendingOpenMam: true,
        schemaSelectionError: false
      });
      return;
    }

    this.openAddMamIndexer();
  };

  openAddMamIndexer = () => {
    const { indexersState, selectIndexerSchema, proxies } = this.props;
    const schemaItems = Array.isArray(indexersState?.schema) ? indexersState.schema : [];
    const hasMamSchema = schemaItems.some((schemaItem) => schemaItem.implementation === 'MyAnonaMouse');

    if (!hasMamSchema) {
      this.setState({
        pendingOpenMam: false,
        schemaSelectionError: true
      });
      return;
    }

    const defaultProxy = proxies ? proxies.find((proxy) => proxy.name === 'Default Proxy') : null;

    if (defaultProxy) {
      selectIndexerSchema({
        implementation: 'MyAnonaMouse',
        pendingChanges: { proxyId: defaultProxy.id }
      });
    } else {
      selectIndexerSchema({ implementation: 'MyAnonaMouse' });
    }

    this.setState({
      isEditIndexerModalOpen: true,
      editIndexerId: 0,
      pendingOpenMam: false,
      schemaSelectionError: false
    });
  };

  onEditIndexerPress = (indexerId) => {
    this.setState({ isEditIndexerModalOpen: true, editIndexerId: indexerId });
  };

  onAddIndexerPress = () => {
    this.setState({ isAddIndexerModalOpen: true });
  };

  onEditIndexerModalClose = () => {
    this.setState({ isEditIndexerModalOpen: false, editIndexerId: null, pendingOpenMam: false, schemaSelectionError: false });
  };

  onAddIndexerModalClose = ({ indexerSelected = false } = {}) => {
    this.setState({
      isAddIndexerModalOpen: false,
      isEditIndexerModalOpen: indexerSelected
    });
  };

  onDeleteIndexerPress = () => {
    this.setState({
      isEditIndexerModalOpen: false,
      isDeleteIndexerModalOpen: true,
      deleteIndexerId: this.state.editIndexerId
    });
  };

  onDeleteIndexerModalClose = () => {
    this.setState({
      isDeleteIndexerModalOpen: false,
      deleteIndexerId: null
    });
  };

  onConfirmDeleteIndexer = () => {
    const { deleteIndexerId } = this.state;

    this.props.deleteIndexer({ id: deleteIndexerId });
    this.onDeleteIndexerModalClose();
  };

  onIndexerSaveSuccess = () => {
    // Progress is calculated from actual enabled indexers.
  };

  getIndexerButtonLabel(indexer) {
    const isMAM = indexer.implementationName && indexer.implementationName.toLowerCase().includes('myanona');

    if (!isMAM) {
      return translate('ConfigureName', { name: indexer.name });
    }

    let userClass = '';

    if (Array.isArray(indexer.fields)) {
      const classField = indexer.fields.find((field) => String(field.name || '').toLowerCase().includes('userclass'));

      if (classField && classField.value) {
        userClass = String(classField.value);
      }
    }

    const messageText = (indexer.message && (indexer.message.message || (indexer.message.value && indexer.message.value.message))) ?
      String(indexer.message.message || indexer.message.value.message) : '';

    if (!userClass) {
      const match = messageText.match(/class:\s*([^)]+)/i);

      if (match && match[1]) {
        userClass = match[1].trim();
      }
    }

    const msgLower = messageText.toLowerCase();
    const vipDetected = msgLower.includes('vip: true') ||
      msgLower.includes('elite vip') ||
      msgLower.includes(' evip') ||
      msgLower.includes('[vip]') ||
      msgLower.includes(' vip ');

    const lc = (userClass || '').toLowerCase();
    let status = '';

    if (lc.includes('elite vip')) {
      status = 'EVIP';
    } else if (lc.includes('vip') || vipDetected) {
      status = 'VIP';
    } else if (indexer.enable) {
      status = translate('Member');
    }

    return status ? translate('MyAnonaMouseStatus', { status }) : translate('ConfigureName', { name: 'MyAnonaMouse' });
  }

  render() {
    const {
      indexersState
    } = this.props;

    const {
      isEditIndexerModalOpen,
      isAddIndexerModalOpen,
      editIndexerId,
      isDeleteIndexerModalOpen,
      deleteIndexerId,
      pendingOpenMam,
      schemaSelectionError
    } = this.state;

    const {
      isFetching,
      isSchemaFetching,
      schemaError,
      items: indexers = []
    } = indexersState;

    const enabledIndexers = indexers.filter((indexer) => indexer.enable);
    const hasEnabledIndexers = enabledIndexers.length > 0;
    const mamIndexer = indexers.find((indexer) => indexer.implementationName && indexer.implementationName.toLowerCase().includes('myanona'));
    const deleteIndexerItem = indexers.find((indexer) => indexer.id === deleteIndexerId);

    if (isFetching) {
      return (
        <QuickstartSection
          sectionKey="indexers"
          title={translate('QuickstartConnectIndexersTitle')}
        >
          <LoadingIndicator />
        </QuickstartSection>
      );
    }

    return (
      <QuickstartSection
        sectionKey="indexers"
        title={translate('QuickstartAddIndexersTitle')}
        isComplete={hasEnabledIndexers}
      >
        <div className={styles.sectionDescription}>
          {translate('QuickstartMamSectionDescription')}
        </div>

        <div className={`${styles.quickstartCardActions} ${styles.cardActionsWrap}`}>
          {!mamIndexer && (
            <button
              className={styles.quickstartCardButton}
              onClick={this.onMAMButtonPress}
              disabled={isSchemaFetching || pendingOpenMam}
            >
              {translate('QuickstartMamAddMyAnonaMouse')}
            </button>
          )}

          <button
            className={styles.quickstartCardButton}
            onClick={this.onAddIndexerPress}
          >
            {translate('AddIndexer')}
          </button>

          {indexers.map((indexer) => (
            <div
              key={indexer.id}
              className={styles.inlineRow}
            >
              <button
                className={styles.quickstartCardButton}
                onClick={() => this.onEditIndexerPress(indexer.id)}
              >
                {this.getIndexerButtonLabel(indexer)}
              </button>
            </div>
          ))}
        </div>

        {
          !isSchemaFetching && (schemaError || schemaSelectionError) &&
            <Alert kind={kinds.DANGER}>
              {translate('QuickstartUnableToLoadIndexerOptions')}
            </Alert>
        }

        <EditIndexerModalConnector
          id={editIndexerId}
          isOpen={isEditIndexerModalOpen}
          onModalClose={this.onEditIndexerModalClose}
          onDeleteIndexerPress={this.onDeleteIndexerPress}
          onSaveSuccess={this.onIndexerSaveSuccess}
        />

        <AddIndexerModal
          isOpen={isAddIndexerModalOpen}
          onModalClose={this.onAddIndexerModalClose}
        />

        <ConfirmModal
          isOpen={isDeleteIndexerModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteIndexer')}
          message={translate('DeleteIndexerMessageText', { name: deleteIndexerItem?.name || '' })}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteIndexer}
          onCancel={this.onDeleteIndexerModalClose}
        />
      </QuickstartSection>
    );
  }
}

QuickstartMAMSection.propTypes = {
  indexersState: PropTypes.object.isRequired,
  mamIndexer: PropTypes.object,
  fetchIndexerSchema: PropTypes.func.isRequired,
  selectIndexerSchema: PropTypes.func.isRequired,
  deleteIndexer: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func,
  proxies: PropTypes.array
};

export default connect(null, { deleteIndexer })(QuickstartMAMSection);
