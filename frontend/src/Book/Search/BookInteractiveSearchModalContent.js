import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Button from 'Components/Link/Button';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { scrollDirections } from 'Helpers/Props';
import InteractiveSearchConnector from 'InteractiveSearch/InteractiveSearchConnector';
import translate from 'Utilities/String/translate';

function normalizeMediaType(mediaType) {
  return mediaType === 'ebook' ? 'ebook' : 'audiobook';
}

class BookInteractiveSearchModalContent extends Component {
  constructor(props) {
    super(props);

    this.state = {
      activeBookId: props.bookId,
      activeMediaType: normalizeMediaType(props.selectedMediaType)
    };
  }

  componentDidUpdate(prevProps) {
    if (prevProps.bookId !== this.props.bookId || prevProps.selectedMediaType !== this.props.selectedMediaType) {
      this.setState({
        activeBookId: this.props.bookId,
        activeMediaType: normalizeMediaType(this.props.selectedMediaType)
      });
    }
  }

  onMediaTypeChange = ({ bookId, mediaType }) => {
    if (!bookId) {
      return;
    }

    this.setState({
      activeBookId: bookId,
      activeMediaType: normalizeMediaType(mediaType)
    });
  };

  render() {
    const {
      bookId,
      seriesId,
      authorId,
      bookTitle,
      seriesTitle,
      authorName,
      onModalClose
    } = this.props;

    const {
      activeBookId,
      activeMediaType
    } = this.state;
    const hasSeries = seriesId !== null && seriesId !== undefined;
    const hasBook = bookId !== null;
    const searchPayload = hasSeries ?
      { seriesId, authorId } :
      { bookId: activeBookId, initialMediaType: activeMediaType };

    let modalHeader = translate('InteractiveSearchModalHeader');

    if (hasSeries) {
      modalHeader = translate('InteractiveSearchModalHeaderSeriesAuthor', { seriesTitle, authorName });
    } else if (hasBook) {
      modalHeader = translate('InteractiveSearchModalHeaderBookAuthor', { bookTitle, authorName });
    }

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {modalHeader}
        </ModalHeader>

        <ModalBody scrollDirection={scrollDirections.BOTH}>
          <InteractiveSearchConnector
            type="book"
            searchPayload={searchPayload}
            onMediaTypeChange={this.onMediaTypeChange}
          />
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {translate('Close')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

BookInteractiveSearchModalContent.propTypes = {
  bookId: PropTypes.number,
  seriesId: PropTypes.number,
  authorId: PropTypes.number,
  bookTitle: PropTypes.string,
  seriesTitle: PropTypes.string,
  authorName: PropTypes.string.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  onModalClose: PropTypes.func.isRequired
};

BookInteractiveSearchModalContent.defaultProps = {
  bookId: null
};

export default BookInteractiveSearchModalContent;
