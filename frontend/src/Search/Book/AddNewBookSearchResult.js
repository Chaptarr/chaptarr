import dayjs from 'Utilities/Date/dayjsSetup';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import TextTruncate from 'react-text-truncate';
import BookCover from 'Book/BookCover';
import BookFormatActions from 'Components/Book/BookFormatActions';
import HeartRating from 'Components/HeartRating';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import Link from 'Components/Link/Link';
import { icons, sizes } from 'Helpers/Props';
import dimensions from 'Styles/Variables/dimensions';
import fonts from 'Styles/Variables/fonts';
import stripHtml from 'Utilities/String/stripHtml';
import translate from 'Utilities/String/translate';
import AddNewBookModal from './AddNewBookModal';
import styles from './AddNewBookSearchResult.css';

const columnPadding = parseInt(dimensions.authorIndexColumnPadding);
const columnPaddingSmallScreen = parseInt(dimensions.authorIndexColumnPaddingSmallScreen);
const defaultFontSize = parseInt(fonts.defaultFontSize);
const lineHeight = parseFloat(fonts.lineHeight);

function calculateHeight(rowHeight, isSmallScreen) {
  let height = rowHeight - 70;

  if (isSmallScreen) {
    height -= columnPaddingSmallScreen;
  } else {
    height -= columnPadding;
  }

  return height;
}

function getRowLinkProps(hasLocalBook, localInstances, canAdd, onPress) {
  if (!hasLocalBook) {
    return canAdd ? { onPress } : { isDisabled: true };
  }

  if (localInstances.length === 1) {
    return { to: `/book/${localInstances[0].id}` };
  }

  return { isDisabled: true };
}

function getPreferredLinkNames(providerContext) {
  if (providerContext === 'gr' || providerContext === 'goodreads') {
    return ['goodreads', 'hardcover', 'audible', 'amazon'];
  }

  if (providerContext === 'hc' || providerContext === 'hardcover') {
    return ['hardcover', 'goodreads', 'audible', 'amazon'];
  }

  if (providerContext === 'az' || providerContext === 'audible') {
    return ['audible', 'amazon', 'hardcover', 'goodreads'];
  }

  return ['audible', 'hardcover', 'goodreads', 'amazon'];
}

class AddNewBookSearchResult extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isNewAddBookModalOpen: false,
      modalSnapshot: null
    };
  }

  onPress = () => {
    this.openAddBookModal(null);
  };

  onAddFormatPress = (mediaType) => {
    this.openAddBookModal(mediaType);
  };

  openAddBookModal = (mediaType) => {
    if (!this.canAddBook()) {
      return;
    }

    this.setState({
      isNewAddBookModalOpen: true,
      modalSnapshot: this.getModalSnapshot(mediaType)
    });
  };

  canAddBook = () => {
    const {
      foreignBookId,
      author
    } = this.props;

    const foreignBookIdString = String(foreignBookId ?? '');
    const foreignAuthorIdString = String(author?.foreignAuthorId ?? '');

    return foreignBookIdString.trim().length > 0 && foreignAuthorIdString.trim().length > 0;
  };

  getModalSnapshot = (mediaType) => {
    const {
      isSmallScreen,
      searchProvider,
      isExistingAuthor,
      ...book
    } = this.props;

    const {
      foreignBookId,
      title,
      seriesTitle,
      disambiguation,
      overview,
      images,
      author,
      localAudiobookBooks,
      localEbookBooks
    } = book;

    return {
      initialMediaType: mediaType,
      foreignBookId,
      foreignAuthorId: author.foreignAuthorId,
      book,
      bookTitle: title,
      seriesTitle,
      disambiguation,
      authorName: author.authorName,
      overview,
      folder: author.folder,
      images,
      localAudiobookBooks,
      localEbookBooks,
      isExistingAuthor
    };
  };

  onAddBookModalClose = () => {
    this.setState({
      isNewAddBookModalOpen: false,
      modalSnapshot: null
    });
  };

  onMBLinkPress = (event) => {
    event.stopPropagation();
  };

  //
  // Render

  render() {
    const {
      foreignBookId,
      title,
      seriesTitle,
      releaseDate,
      disambiguation,
      overview,
      ratings,
      images,
      links,
      narrator,
      durationMinutes,
      searchProvider,
      localAudiobookBooks,
      localEbookBooks,
      isSmallScreen
    } = this.props;

    const {
      isNewAddBookModalOpen,
      modalSnapshot
    } = this.state;

    const foreignBookIdString = String(foreignBookId ?? '');
    const foreignBookIdLower = foreignBookIdString.toLowerCase();

    const canAdd = this.canAddBook();
    const audiobookInstances = localAudiobookBooks || [];
    const ebookInstances = localEbookBooks || [];
    const localInstances = [
      ...audiobookInstances,
      ...ebookInstances
    ];
    const hasLocalBook = localInstances.length > 0;

    const linkProps = getRowLinkProps(hasLocalBook, localInstances, canAdd, this.onPress);

    const foreignProvider = foreignBookIdLower.includes(':') ?
      foreignBookIdLower.split(':')[0] :
      '';

    // Prefer the provider the user searched against (UI context) for the external-link button.
    // Search results often include multiple provider IDs; ForeignBookId may not reflect the
    // selected provider (it prefers Hardcover when available).
    const providerContext = (searchProvider || '').toLowerCase() || foreignProvider;
    const preferredLinkNames = getPreferredLinkNames(providerContext);
    const preferredLink = preferredLinkNames
      .map((name) => links?.find((l) => (l?.name || '').toLowerCase() === name))
      .find(Boolean) || links?.[0];

    const height = calculateHeight(230, isSmallScreen);

    return (
      <div className={styles.searchResult}>
        <Link
          className={styles.underlay}
          {...linkProps}
        />

        <div className={styles.overlay}>
          {
            !isSmallScreen &&
              <BookCover
                className={styles.poster}
                images={images}
                size={250}
                lazy={false}
              />
          }

          <div className={styles.content}>
            <div className={styles.titleRow}>
              <div className={styles.titleContainer}>
                <div className={styles.title}>
                  {title}

                  {
                    !!disambiguation &&
                      <span className={styles.year}>({disambiguation})</span>
                  }
                </div>
              </div>

              <div className={styles.icons}>
                <BookFormatActions
                  title={title}
                  localAudiobookBooks={audiobookInstances}
                  localEbookBooks={ebookInstances}
                  canAdd={canAdd}
                  size={28}
                  onAdd={this.onAddFormatPress}
                />

                {
                  !canAdd ?
                    <Icon
                      className={styles.notAddableIcon}
                      name={icons.BAN}
                      size={28}
                      title="Not addable: missing upstream provider book/work ID or author ID"
                    /> :
                    null
                }

                {
                  preferredLink ?
                    <Link
                      className={styles.mbLink}
                      to={preferredLink.url}
                      onPress={this.onMBLinkPress}
                    >
                      <Icon
                        className={styles.mbLinkIcon}
                        name={icons.EXTERNAL_LINK}
                        size={28}
                      />
                    </Link> : null
                }
              </div>
            </div>

            {
              narrator &&
                <div className={styles.narrator}>
                  {translate('NarratedBy', { narrator: Array.isArray(narrator) ? narrator.join(', ') : narrator })}
                </div>
            }

            {
              seriesTitle &&
                <div className={styles.series}>
                  {seriesTitle}
                </div>
            }

            <div>
              {
                ratings && (ratings.votes > 0 || ratings.value > 0) ?
                  <Label size={sizes.LARGE}>
                    <HeartRating
                      rating={ratings.value}
                      iconSize={13}
                    />
                  </Label> :
                  null
              }

              {
                durationMinutes > 0 &&
                  <Label size={sizes.LARGE}>
                    {Math.floor(durationMinutes / 60)}h {Math.round(durationMinutes % 60)}m
                  </Label>
              }

              {
                !!releaseDate && dayjs(releaseDate).year() > 1 &&
                  <Label size={sizes.LARGE}>
                    {dayjs(releaseDate).format('YYYY')}
                  </Label>
              }

            </div>

            <div
              className={styles.overview}
              style={{
                maxHeight: `${height}px`
              }}
            >
              <TextTruncate
                truncateText="…"
                line={Math.floor(height / (defaultFontSize * lineHeight))}
                text={stripHtml(overview)}
              />
            </div>
          </div>
        </div>

        {
          modalSnapshot ?
            <AddNewBookModal
              isOpen={isNewAddBookModalOpen}
              {...modalSnapshot}
              onModalClose={this.onAddBookModalClose}
            /> :
            null
        }
      </div>
    );
  }
}

AddNewBookSearchResult.propTypes = {
  foreignBookId: PropTypes.string,
  title: PropTypes.string.isRequired,
  seriesTitle: PropTypes.string,
  releaseDate: PropTypes.string,
  disambiguation: PropTypes.string,
  overview: PropTypes.string,
  ratings: PropTypes.object.isRequired,
  author: PropTypes.object,
  images: PropTypes.arrayOf(PropTypes.object).isRequired,
  links: PropTypes.arrayOf(PropTypes.object),
  narrator: PropTypes.oneOfType([PropTypes.string, PropTypes.arrayOf(PropTypes.string)]),
  durationMinutes: PropTypes.number,
  searchProvider: PropTypes.string,
  isExistingAuthor: PropTypes.bool.isRequired,
  localAudiobookBooks: PropTypes.arrayOf(PropTypes.object),
  localEbookBooks: PropTypes.arrayOf(PropTypes.object),
  isSmallScreen: PropTypes.bool.isRequired
};

export default AddNewBookSearchResult;
