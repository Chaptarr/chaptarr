import _ from 'lodash';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function formatBookFiles(items, book) {

  return (
    <div key={book.id}>
      <b> {book.title} </b>
      <ul>
        {
          _.sortBy(_.uniq(items.flatMap((item) => [item.path, ...(item.associatedPaths || [])]))).map((path) => {
            return (
              <li key={path}>
                {path}
              </li>
            );
          })
        }
      </ul>
    </div>
  );

}

class ConfirmImportModalContent extends Component {

  //
  // Lifecycle

  componentDidUpdate(prevProps) {
    const {
      items,
      isFetching,
      isPopulated
    } = this.props;

    if (!isFetching && isPopulated && !items.length) {
      this.props.onModalClose();
      this.props.onConfirmImportPress([]);
    }
  }

  onConfirmImportPress = () => {
    this.props.onConfirmImportPress(this.props.items.map(({ id, bookId, path, associatedPaths }) => ({ id, bookId, path, associatedPaths })));
  };

  //
  // Render

  render() {
    const {
      books,
      items,
      onModalClose,
      isFetching,
      isPopulated,
      error
    } = this.props;

    // don't render if nothing to do
    if (!isFetching && isPopulated && !items.length) {
      return null;
    }

    return (
      <ModalContent onModalClose={onModalClose}>

        {
          !isFetching && isPopulated &&
            <ModalHeader>
              {translate('AreYouSure')}
            </ModalHeader>
        }

        <ModalBody>
          {
            error &&
              <Alert kind={kinds.DANGER}>
                {translate('UnableToLoadReplacementFiles')}
              </Alert>
          }
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && isPopulated &&
              <div>
                <Alert>
                  {translate('ConfirmImportExistingPrefix')} <b>{translate('ConfirmImportWillBeDeleted')}</b> {translate('ConfirmImportExistingSuffix')}
                </Alert>

                { _.chain(items)
                  .groupBy('bookId')
                  .mapValues((value, key) => formatBookFiles(value, _.find(books, (a) => a.id === parseInt(key))))
                  .values()
                  .value() }
              </div>
          }
        </ModalBody>

        {
          !isFetching && isPopulated &&
            <ModalFooter>
              <Button onPress={onModalClose}>
                {translate('Cancel')}
              </Button>

              <Button
                kind={kinds.DANGER}
                onPress={this.onConfirmImportPress}
              >
                {translate('Proceed')}
              </Button>

            </ModalFooter>
        }

      </ModalContent>
    );
  }
}

ConfirmImportModalContent.propTypes = {
  books: PropTypes.arrayOf(PropTypes.object).isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  onConfirmImportPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ConfirmImportModalContent;
