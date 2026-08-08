import PropTypes from 'prop-types';
import React, { Component } from 'react';
import TextTruncate from 'react-text-truncate';
import AuthorPoster from 'Author/AuthorPoster';
import CheckInput from 'Components/Form/CheckInput';
import SpinnerButton from 'Components/Link/SpinnerButton';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AddAuthorOptionsForm from '../Common/AddAuthorOptionsForm.js';
import styles from './AddNewAuthorModalContent.css';

class AddNewAuthorModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      searchForMissingBooks: false
    };
  }

  //
  // Listeners

  onSearchForMissingBooksChange = ({ value }) => {
    this.setState({ searchForMissingBooks: value });
  };

  onAddAuthorPress = () => {
    this.props.onAddAuthorPress(this.state.searchForMissingBooks);
  };

  //
  // Render

  render() {
    const {
      authorName,
      disambiguation,
      overview,
      images,
      isAdding,
      isSmallScreen,
      selectedMediaType,
      onMediaTypeChange,
      defaultMediaType,
      onSetDefaultMediaType,
      isSavingDefaultMediaType,
      rootFoldersPopulated,
      audiobookRootFolderPath,
      ebookRootFolderPath,
      audiobookQualityProfileId,
      ebookQualityProfileId,
      onModalClose,
      ...otherProps
    } = this.props;

    let addLabel = 'Add Audiobooks';
    if (selectedMediaType === 'ebook') {
      addLabel = 'Add Ebooks';
    } else if (selectedMediaType === 'both') {
      addLabel = 'Add Audiobooks + Ebooks';
    }

    const audiobookRoot = audiobookRootFolderPath?.value;
    const ebookRoot = ebookRootFolderPath?.value;
    const audiobookProfile = audiobookQualityProfileId?.value;
    const ebookProfile = ebookQualityProfileId?.value;

    const needsAudiobook = selectedMediaType === 'audiobook' || selectedMediaType === 'both';
    const needsEbook = selectedMediaType === 'ebook' || selectedMediaType === 'both';

    const isAddDisabled = !rootFoldersPopulated ||
      (needsAudiobook && (!audiobookRoot || !audiobookProfile || audiobookProfile === 0 || audiobookProfile === 'none')) ||
      (needsEbook && (!ebookRoot || !ebookProfile || ebookProfile === 0 || ebookProfile === 'none'));

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('AddNewAuthor')}
        </ModalHeader>

        <ModalBody>
          <div className={styles.container}>
            {
              isSmallScreen ?
                null:
                <div className={styles.poster}>
                  <AuthorPoster
                    className={styles.poster}
                    images={images}
                    size={250}
                  />
                </div>
            }

            <div className={styles.info}>
              <div className={styles.name}>
                {authorName}
              </div>

              {
                !!disambiguation &&
                  <span className={styles.disambiguation}>({disambiguation})</span>
              }

              {
                overview ?
                  <div className={styles.overview}>
                    <TextTruncate
                      truncateText="…"
                      line={8}
                      text={overview}
                    />
                  </div> :
                  null
              }

              <AddAuthorOptionsForm
                includeNoneMetadataProfile={false}
                includeBothMediaType={true}
                selectedMediaType={selectedMediaType}
                onMediaTypeChange={onMediaTypeChange}
                defaultMediaType={defaultMediaType}
                onSetDefaultMediaType={onSetDefaultMediaType}
                isSavingDefaultMediaType={isSavingDefaultMediaType}
                audiobookRootFolderPath={audiobookRootFolderPath}
                ebookRootFolderPath={ebookRootFolderPath}
                audiobookQualityProfileId={audiobookQualityProfileId}
                ebookQualityProfileId={ebookQualityProfileId}
                {...otherProps}
              />

            </div>
          </div>
        </ModalBody>

        <ModalFooter className={styles.modalFooter}>
          <label className={styles.searchForMissingBooksLabelContainer}>
            <span className={styles.searchForMissingBooksLabel}>
              {translate('StartSearchForMissingBooks')}
            </span>

            <CheckInput
              containerClassName={styles.searchForMissingBooksContainer}
              className={styles.searchForMissingBooksInput}
              name="searchForMissingBooks"
              value={this.state.searchForMissingBooks}
              onChange={this.onSearchForMissingBooksChange}
            />
          </label>

          <SpinnerButton
            className={styles.addButton}
            kind={kinds.SUCCESS}
            isSpinning={isAdding}
            isDisabled={isAddDisabled}
            onPress={this.onAddAuthorPress}
          >
            {addLabel}
          </SpinnerButton>
        </ModalFooter>
      </ModalContent>
    );
  }
}

AddNewAuthorModalContent.propTypes = {
  authorName: PropTypes.string.isRequired,
  disambiguation: PropTypes.string,
  overview: PropTypes.string,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  isAdding: PropTypes.bool.isRequired,
  addError: PropTypes.object,
  isSmallScreen: PropTypes.bool.isRequired,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  defaultMediaType: PropTypes.string,
  onSetDefaultMediaType: PropTypes.func,
  isSavingDefaultMediaType: PropTypes.bool,
  rootFoldersPopulated: PropTypes.bool.isRequired,
  audiobookRootFolderPath: PropTypes.object,
  ebookRootFolderPath: PropTypes.object,
  audiobookQualityProfileId: PropTypes.object,
  ebookQualityProfileId: PropTypes.object,
  onModalClose: PropTypes.func.isRequired,
  onAddAuthorPress: PropTypes.func.isRequired
};

export default AddNewAuthorModalContent;
