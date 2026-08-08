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
      bookTitle,
      authorName,
      onModalClose
    } = this.props;

    const {
      activeBookId,
      activeMediaType
    } = this.state;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {bookId === null ?
            translate('InteractiveSearchModalHeader') :
            translate('InteractiveSearchModalHeaderBookAuthor', { bookTitle, authorName })
          }
        </ModalHeader>

        <ModalBody scrollDirection={scrollDirections.BOTH}>
          <InteractiveSearchConnector
            type="book"
            searchPayload={{
              bookId: activeBookId,
              initialMediaType: activeMediaType
            }}
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
  bookId: PropTypes.number.isRequired,
  bookTitle: PropTypes.string.isRequired,
  authorName: PropTypes.string.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  onModalClose: PropTypes.func.isRequired
};

export default BookInteractiveSearchModalContent;
