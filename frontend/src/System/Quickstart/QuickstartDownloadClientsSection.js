import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ConfirmModal from 'Components/Modal/ConfirmModal';
import { kinds } from 'Helpers/Props';
import AddDownloadClientModal from 'Settings/DownloadClients/DownloadClients/AddDownloadClientModal';
import EditDownloadClientModalConnector from 'Settings/DownloadClients/DownloadClients/EditDownloadClientModalConnector';
import { deleteDownloadClient } from 'Store/Actions/settingsActions';
import translate from 'Utilities/String/translate';
import QuickstartSection from './QuickstartSection';
import styles from './Quickstart.css';

class QuickstartDownloadClientsSection extends Component {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isAddModalOpen: false,
      isEditModalOpen: false,
      editClientId: null,
      isDeleteDownloadClientModalOpen: false,
      deleteClientId: null
    };
  }

  componentDidMount() {
    // Pre-fetch the schema so it's ready when user clicks
    if (!this.props.downloadClientsState.isSchemaPopulated) {
      this.props.fetchDownloadClientSchema();
    }
  }

  //
  // Listeners

  onAddPress = () => {
    this.setState({ isAddModalOpen: true });
  };

  onEditPress = (clientId) => {
    this.setState({ isEditModalOpen: true, editClientId: clientId });
  };

  onAddModalClose = ({ downloadClientSelected = false } = {}) => {
    this.setState({
      isAddModalOpen: false,
      isEditModalOpen: downloadClientSelected
    });
  };

  onEditModalClose = () => {
    this.setState({ isEditModalOpen: false, editClientId: null });
  };

  onDeleteDownloadClientPress = () => {
    this.setState({
      isEditModalOpen: false,
      isDeleteDownloadClientModalOpen: true,
      deleteClientId: this.state.editClientId
    });
  };

  onDeleteDownloadClientModalClose = () => {
    this.setState({
      isDeleteDownloadClientModalOpen: false,
      deleteClientId: null
    });
  };

  onConfirmDeleteDownloadClient = () => {
    const { deleteClientId } = this.state;

    this.props.deleteDownloadClient({ id: deleteClientId });
    this.onDeleteDownloadClientModalClose();
  };

  onSaveSuccess = () => {
    // No longer tracking interactions here - progress is calculated from actual enabled download clients
  };

  //
  // Render

  render() {
    const {
      downloadClientsState
    } = this.props;

    const {
      isFetching,
      isPopulated,
      items: downloadClients
    } = downloadClientsState;

    const {
      isAddModalOpen,
      isEditModalOpen,
      editClientId,
      isDeleteDownloadClientModalOpen,
      deleteClientId
    } = this.state;

    if (isFetching) {
      return (
        <QuickstartSection
          sectionKey="downloadClients"
          title={translate('QuickstartDownloadClientsTitle')}
        >
          <LoadingIndicator />
        </QuickstartSection>
      );
    }

    if (!isPopulated) {
      return null;
    }

    const hasDownloadClients = downloadClients && downloadClients.length > 0;
    const enabledClients = downloadClients ? downloadClients.filter((c) => c.enable) : [];
    const deleteClient = downloadClients?.find((client) => client.id === deleteClientId);

    return (
      <QuickstartSection
        sectionKey="downloadClients"
        title={translate('QuickstartDownloadClientsTitle')}
        isComplete={enabledClients.length > 0}
      >
        {(!hasDownloadClients || enabledClients.length === 0) && (
          <div className={styles.sectionDescription}>
            {translate('QuickstartDownloadClientsDescription')}
          </div>
        )}
        <div className={styles.quickstartCardActions}>
          <button
            className={styles.quickstartCardButton}
            onClick={this.onAddPress}
          >
            {downloadClients && downloadClients.length > 0 ? translate('AddAnotherDownloadClient') : translate('AddDownloadClient')}
          </button>
          {downloadClients && downloadClients.length > 0 && (
            downloadClients.map((client) => (
              <button
                key={client.id}
                className={styles.quickstartCardButton}
                onClick={() => this.onEditPress(client.id)}
              >
                {translate('ConfigureName', { name: client.name })}
              </button>
            ))
          )}
        </div>

        <AddDownloadClientModal
          isOpen={isAddModalOpen}
          onModalClose={this.onAddModalClose}
        />

        <EditDownloadClientModalConnector
          id={editClientId}
          isOpen={isEditModalOpen}
          onModalClose={this.onEditModalClose}
          onDeleteDownloadClientPress={this.onDeleteDownloadClientPress}
          onSaveSuccess={this.onSaveSuccess}
        />

        <ConfirmModal
          isOpen={isDeleteDownloadClientModalOpen}
          kind={kinds.DANGER}
          title={translate('DeleteDownloadClient')}
          message={translate('DeleteDownloadClientMessageText', { name: deleteClient?.name || '' })}
          confirmLabel={translate('Delete')}
          onConfirm={this.onConfirmDeleteDownloadClient}
          onCancel={this.onDeleteDownloadClientModalClose}
        />
      </QuickstartSection>
    );
  }
}

QuickstartDownloadClientsSection.propTypes = {
  downloadClientsState: PropTypes.object.isRequired,
  fetchDownloadClientSchema: PropTypes.func.isRequired,
  selectDownloadClientSchema: PropTypes.func.isRequired,
  deleteDownloadClient: PropTypes.func.isRequired,
  markSectionInteracted: PropTypes.func
};

export default connect(null, { deleteDownloadClient })(QuickstartDownloadClientsSection);
