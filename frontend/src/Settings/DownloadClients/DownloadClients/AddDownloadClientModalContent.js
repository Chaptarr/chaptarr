import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import titleCase from 'Utilities/String/titleCase';
import translate from 'Utilities/String/translate';
import AddDownloadClientItem from './AddDownloadClientItem';
import styles from './AddDownloadClientModalContent.css';

class AddDownloadClientModalContent extends Component {

  //
  // Render

  render() {
    const {
      isSchemaFetching,
      isSchemaPopulated,
      schemaError,
      usenetDownloadClients,
      torrentDownloadClients,
      onDownloadClientSelect,
      onModalClose
    } = this.props;

    const implementationNameCounts = [...usenetDownloadClients, ...torrentDownloadClients].reduce((counts, client) => {
      const key = (client.implementationName || '').toLowerCase();
      counts[key] = (counts[key] || 0) + 1;
      return counts;
    }, {});

    const getDisplayName = (client) => {
      const key = (client.implementationName || '').toLowerCase();
      if (implementationNameCounts[key] > 1) {
        return `${client.implementationName} (${titleCase(client.protocol)})`;
      }

      return client.implementationName;
    };

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('AddDownloadClient')}
        </ModalHeader>

        <ModalBody>
          {
            isSchemaFetching &&
              <LoadingIndicator />
          }

          {
            !isSchemaFetching && !!schemaError &&
              <div>
                {translate('UnableToAddANewDownloadClientPleaseTryAgain')}
              </div>
          }

          {
            isSchemaPopulated && !schemaError &&
              <div>

                <Alert kind={kinds.INFO}>
                  <div>
                    {translate('ChaptarrSupportsAnyDownloadClient')}
                  </div>
                  <div>
                    {translate('ForMoreInformationOnTheIndividualDownloadClientsClickOnTheInfoButtons')}
                  </div>
                </Alert>

                <FieldSet legend={translate('Usenet')}>
                  <div className={styles.downloadClients}>
                    {
                      usenetDownloadClients.map((downloadClient) => {
                        return (
                          <AddDownloadClientItem
                            key={downloadClient.implementation}
                            implementation={downloadClient.implementation}
                            {...downloadClient}
                            implementationName={getDisplayName(downloadClient)}
                            onDownloadClientSelect={onDownloadClientSelect}
                          />
                        );
                      })
                    }
                  </div>
                </FieldSet>

                <FieldSet legend={translate('Torrents')}>
                  <div className={styles.downloadClients}>
                    {
                      torrentDownloadClients.map((downloadClient) => {
                        return (
                          <AddDownloadClientItem
                            key={downloadClient.implementation}
                            implementation={downloadClient.implementation}
                            {...downloadClient}
                            implementationName={getDisplayName(downloadClient)}
                            onDownloadClientSelect={onDownloadClientSelect}
                          />
                        );
                      })
                    }
                  </div>
                </FieldSet>
              </div>
          }
        </ModalBody>
        <ModalFooter>
          <Button
            onPress={onModalClose}
          >
            {translate('Close')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

AddDownloadClientModalContent.propTypes = {
  isSchemaFetching: PropTypes.bool.isRequired,
  isSchemaPopulated: PropTypes.bool.isRequired,
  schemaError: PropTypes.object,
  usenetDownloadClients: PropTypes.arrayOf(PropTypes.object).isRequired,
  torrentDownloadClients: PropTypes.arrayOf(PropTypes.object).isRequired,
  onDownloadClientSelect: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default AddDownloadClientModalContent;
