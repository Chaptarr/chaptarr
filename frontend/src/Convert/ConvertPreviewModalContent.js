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
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import ConvertPreviewRow from './ConvertPreviewRow';
import styles from './ConvertPreviewModalContent.css';

// Every convertible file of a book becomes one output file, so a book is the smallest thing
// that can be converted. Selection is per book; the rows underneath are there to show what
// each one is made of and why anything is being left behind.
function groupByBook(items) {
  const books = [];
  const byEdition = new Map();

  items.forEach((item) => {
    let book = byEdition.get(item.editionId);

    if (!book) {
      book = {
        editionId: item.editionId,
        title: item.bookTitle,
        items: [],
        fileIds: []
      };

      byEdition.set(item.editionId, book);
      books.push(book);
    }

    book.items.push(item);

    if (item.canConvert) {
      book.fileIds.push(item.bookFileId);
    }
  });

  return books;
}

function getSelectAllValue(selectedCount, convertibleCount) {
  if (selectedCount === 0) {
    return false;
  }

  if (convertibleCount > 0 && selectedCount === convertibleCount) {
    return true;
  }

  return null;
}

class ConvertPreviewModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      selectedEditions: {},
      hasInitialized: false
    };
  }

  static getDerivedStateFromProps(props, state) {
    if (state.hasInitialized || !props.isPopulated || props.isFetching) {
      return null;
    }

    // Default to converting everything that can be converted.
    const selectedEditions = {};

    groupByBook(props.items).forEach((book) => {
      if (book.fileIds.length) {
        selectedEditions[book.editionId] = true;
      }
    });

    return {
      selectedEditions,
      hasInitialized: true
    };
  }

  //
  // Control

  getSelectedBooks = () => {
    return groupByBook(this.props.items)
      .filter((book) => book.fileIds.length && this.state.selectedEditions[book.editionId]);
  };

  //
  // Listeners

  onSelectAllChange = ({ value }) => {
    const selectedEditions = {};

    groupByBook(this.props.items).forEach((book) => {
      if (book.fileIds.length) {
        selectedEditions[book.editionId] = value;
      }
    });

    this.setState({ selectedEditions });
  };

  onBookSelectedChange = ({ id, value }) => {
    this.setState((state) => {
      return {
        selectedEditions: {
          ...state.selectedEditions,
          [id]: value
        }
      };
    });
  };

  onConvertPress = () => {
    const fileIds = this.getSelectedBooks().reduce((result, book) => {
      return result.concat(book.fileIds);
    }, []);

    this.props.onConvertPress(fileIds);
  };

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      items,
      onModalClose
    } = this.props;

    const { selectedEditions } = this.state;

    const books = groupByBook(items);
    const convertibleBooks = books.filter((book) => book.fileIds.length);
    const selectedCount = this.getSelectedBooks().length;
    const selectAllValue = getSelectAllValue(selectedCount, convertibleBooks.length);

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('ConvertBookFiles')}
        </ModalHeader>

        <ModalBody>
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && error &&
              <div>
                {translate('ErrorLoadingPreviews')}
              </div>
          }

          {
            !isFetching && isPopulated && !items.length &&
              <div>
                {translate('NoFilesNeedConverting')}
              </div>
          }

          {
            !isFetching && isPopulated && !!items.length &&
              <div>
                <Alert kind={kinds.WARNING}>
                  {translate('ConvertReplacesSourceFilesWarning')}
                </Alert>

                {
                  !convertibleBooks.length &&
                    <Alert kind={kinds.INFO}>
                      {translate('ConvertNoEligibleFiles')}
                    </Alert>
                }

                <div className={styles.previews}>
                  {
                    books.map((book) => {
                      const isConvertible = !!book.fileIds.length;

                      return (
                        <div
                          key={book.editionId}
                          className={styles.book}
                        >
                          <div className={styles.bookHeader}>
                            <CheckInput
                              containerClassName={styles.bookSelectedContainer}
                              name={`edition-${book.editionId}`}
                              value={isConvertible && !!selectedEditions[book.editionId]}
                              isDisabled={!isConvertible}
                              onChange={({ value }) => this.onBookSelectedChange({ id: book.editionId, value })}
                            />

                            <span className={styles.bookTitle}>
                              {book.title}
                            </span>
                          </div>

                          {
                            book.items.map((item) => {
                              return (
                                <ConvertPreviewRow
                                  key={item.bookFileId}
                                  path={item.path}
                                  sourceQuality={item.sourceQuality}
                                  targetQuality={item.targetQuality}
                                  canConvert={item.canConvert}
                                  reason={item.reason}
                                />
                              );
                            })
                          }
                        </div>
                      );
                    })
                  }
                </div>
              </div>
          }
        </ModalBody>

        <ModalFooter>
          {
            isPopulated && !!convertibleBooks.length &&
              <CheckInput
                className={styles.selectAllInput}
                containerClassName={styles.selectAllInputContainer}
                name="selectAll"
                value={selectAllValue}
                onChange={this.onSelectAllChange}
              />
          }

          <Button
            onPress={onModalClose}
          >
            {translate('Cancel')}
          </Button>

          <Button
            kind={kinds.PRIMARY}
            isDisabled={!selectedCount}
            onPress={this.onConvertPress}
          >
            {translate('Convert')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

ConvertPreviewModalContent.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onConvertPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ConvertPreviewModalContent;
