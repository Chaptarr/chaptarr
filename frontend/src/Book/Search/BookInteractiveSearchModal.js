import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import { sizes } from 'Helpers/Props';
import BookInteractiveSearchModalContent from './BookInteractiveSearchModalContent';

function BookInteractiveSearchModal(props) {
  const {
    isOpen,
    bookId,
    seriesId,
    authorId,
    bookTitle,
    seriesTitle,
    authorName,
    selectedMediaType,
    onModalClose
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      size={sizes.EXTRA_EXTRA_LARGE}
      closeOnBackgroundClick={false}
      onModalClose={onModalClose}
    >
      <BookInteractiveSearchModalContent
        bookId={bookId}
        seriesId={seriesId}
        authorId={authorId}
        bookTitle={bookTitle}
        seriesTitle={seriesTitle}
        authorName={authorName}
        selectedMediaType={selectedMediaType}
        onModalClose={onModalClose}
      />
    </Modal>
  );
}

BookInteractiveSearchModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  bookId: PropTypes.number,
  seriesId: PropTypes.number,
  authorId: PropTypes.number,
  bookTitle: PropTypes.string,
  seriesTitle: PropTypes.string,
  authorName: PropTypes.string.isRequired,
  selectedMediaType: PropTypes.string,
  onModalClose: PropTypes.func.isRequired
};

BookInteractiveSearchModal.defaultProps = {
  bookId: null,
  bookTitle: ''
};

export default BookInteractiveSearchModal;
