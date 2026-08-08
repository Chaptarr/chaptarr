import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import NarratorSearchRow from './NarratorSearchRow';
import styles from './NarratorSearchModalContent.css';

const columns = [
  {
    name: 'narrator',
    label: '',
    isVisible: true
  },
  {
    name: 'action',
    label: '',
    isVisible: true
  }
];

class NarratorSearchModalContent extends Component {

  //
  // Lifecycle
  //

  constructor(props, context) {
    super(props, context);

    this.state = {
      searchForNewBook: false
    };
  }

  //
  // Listeners

  onSearchForNewBookChange = ({ value }) => {
    this.setState({ searchForNewBook: value });
  };

  //
  // Render

  render() {
    const {
      bookTitle,
      discovery,
      isSearching,
      isSettingPreferred,
      error,
      onNarratorSelect,
      onModalClose
    } = this.props;

    const {
      success,
      filteredNarrators = [],
      existingCopyNarrators = [],
      totalFiltered = 0,
      recommendedAction = '',
      errorMessage
    } = discovery;

    const { searchForNewBook } = this.state;

    const hasResults = success && (filteredNarrators.length > 0 || existingCopyNarrators.length > 0);
    const hasError = error || errorMessage;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {bookTitle}
        </ModalHeader>

        <ModalBody>

          {recommendedAction && (
            <Alert kind={totalFiltered > 0 ? kinds.INFO : kinds.WARNING}>
              {recommendedAction}
            </Alert>
          )}

          {isSearching && <LoadingIndicator />}

          {hasError && (
            <Alert kind={kinds.DANGER}>
              {errorMessage || error.message || translate('NarratorSearchFailed')}
            </Alert>
          )}

          {hasResults && !isSearching && (
            <div className={styles.narratorSection}>
              <Table columns={columns}>
                <TableBody>
                  {/* Show existing narrators first */}
                  {existingCopyNarrators.map((narrator) => {
                    const narratorName = typeof narrator === 'string' ? narrator : narrator.name;
                    const editionId = typeof narrator === 'object' ? narrator.editionId : null;
                    const status = typeof narrator === 'object' ? narrator.status || 'existing' : 'existing';
                    return (
                      <NarratorSearchRow
                        key={`existing-${editionId || narratorName}`}
                        narrator={narrator}
                        status={status}
                        onSelect={null}
                        isSelecting={false}
                      />
                    );
                  })}
                  {/* Then show available narrators */}
                  {filteredNarrators.map((narrator) => {
                    const narratorName = typeof narrator === 'string' ? narrator : narrator.name;
                    const editionId = typeof narrator === 'object' ? narrator.editionId : null;
                    return (
                      <NarratorSearchRow
                        key={`available-${editionId || narratorName}`}
                        narrator={narrator}
                        status="available"
                        onSelect={(selectedNarrator) => onNarratorSelect(selectedNarrator, searchForNewBook)}
                        isSelecting={isSettingPreferred}
                      />
                    );
                  })}
                </TableBody>
              </Table>
            </div>
          )}

          {success && !isSearching && filteredNarrators.length === 0 && existingCopyNarrators.length === 0 && (
            <Alert kind={kinds.INFO}>
              {translate('NarratorSearchNoResults')}
            </Alert>
          )}
        </ModalBody>

        <ModalFooter className={styles.modalFooter}>
          <label className={styles.searchForNewBookLabelContainer}>
            <span className={styles.searchForNewBookLabel}>
              {translate('NarratorSearchStartAfterAdding')}
            </span>

            <CheckInput
              containerClassName={styles.searchForNewBookContainer}
              className={styles.searchForNewBookInput}
              name="searchForNewBook"
              value={searchForNewBook}
              onChange={this.onSearchForNewBookChange}
            />
          </label>

          <Button
            onPress={onModalClose}
            kind={kinds.PRIMARY}
          >
            {translate('Close')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

NarratorSearchModalContent.propTypes = {
  bookId: PropTypes.number.isRequired,
  bookTitle: PropTypes.string.isRequired,
  authorName: PropTypes.string.isRequired,
  discovery: PropTypes.object.isRequired,
  isSearching: PropTypes.bool.isRequired,
  isSettingPreferred: PropTypes.bool.isRequired,
  error: PropTypes.object,
  onNarratorSelect: PropTypes.func.isRequired,
  onRefreshPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default NarratorSearchModalContent;
