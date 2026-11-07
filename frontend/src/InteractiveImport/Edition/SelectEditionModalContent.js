import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds, scrollDirections } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import SelectEditionRowConnector from './SelectEditionRowConnector';
import styles from './SelectEditionModalContent.css';

const columns = [
  {
    name: 'book',
    label: () => translate('Book'),
    isVisible: true
  },
  {
    name: 'edition',
    label: () => translate('Edition'),
    isVisible: true
  }
];

class SelectEditionModalContent extends Component {

  //
  // Render

  render() {
    const {
      books,
      importIdsByBook,
      isPopulated,
      isFetching,
      error,
      onEditionSelect,
      onRetryPress,
      onModalClose,
      ...otherProps
    } = this.props;

    const isLoading = isFetching || (!isPopulated && !error);
    const isReady = isPopulated && !isFetching && !error;

    const appliesToMultipleFiles = Object.values(importIdsByBook).some((ids) => ids.length > 1);

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('ManualImportSelectEdition')}
        </ModalHeader>

        <ModalBody
          className={styles.modalBody}
          scrollDirection={scrollDirections.VERTICAL}
        >
          {
            isLoading &&
              <LoadingIndicator />
          }

          {
            !isLoading && error &&
              <Alert kind={kinds.DANGER} role="alert">
                <div>{getErrorMessage(error, translate('LoadingEditionsFailed'))}</div>
                <Button size="small" onPress={onRetryPress}>
                  {translate('Retry')}
                </Button>
              </Alert>
          }

          {
            isReady && !books.length &&
              <Alert kind={kinds.INFO} role="status">
                {translate('ManualImportNoBooksForEditionSelection')}
              </Alert>
          }

          {
            isReady && !!books.length &&
              <>
                <Alert>
                  {translate('SelectEditionOverridePrefix')} <b>{translate('SelectEditionDisableAutoBold')}</b> {translate('SelectEditionOverrideSuffix')}
                  {
                    appliesToMultipleFiles &&
                      <>
                        <br />
                        {translate('SelectEditionBulkScope')}
                      </>
                  }
                </Alert>

                <Table
                  columns={columns}
                  {...otherProps}
                >
                  <TableBody>
                    {
                      books.map((item) => (
                        <SelectEditionRowConnector
                          key={item.book.id}
                          matchedEditionId={item.matchedEditionId}
                          columns={columns}
                          onEditionSelect={onEditionSelect}
                          {...item.book}
                        />
                      ))
                    }
                  </TableBody>
                </Table>
              </>
          }
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Cancel')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

SelectEditionModalContent.propTypes = {
  books: PropTypes.arrayOf(PropTypes.object).isRequired,
  importIdsByBook: PropTypes.object.isRequired,
  isFetching: PropTypes.bool,
  isPopulated: PropTypes.bool,
  error: PropTypes.object,
  onEditionSelect: PropTypes.func.isRequired,
  onRetryPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default SelectEditionModalContent;
