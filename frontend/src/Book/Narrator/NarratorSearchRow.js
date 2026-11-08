import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import SpinnerButton from 'Components/Link/SpinnerButton';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { kinds, sizes } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import stripHtml from 'Utilities/String/stripHtml';
import translate from 'Utilities/String/translate';
import styles from './NarratorSearchRow.css';

const placeholderDataUrl = 'data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iMTUwIiBoZWlnaHQ9IjE1MCIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KICA8cmVjdCB3aWR0aD0iMTUwIiBoZWlnaHQ9IjE1MCIgZmlsbD0iIzMzMzMzMyIvPgogIDx0ZXh0IHg9IjUwJSIgeT0iNTAlIiBmb250LWZhbWlseT0iQXJpYWwsIHNhbnMtc2VyaWYiIGZvbnQtc2l6ZT0iMTQiIGZpbGw9IiM2NjY2NjYiIHRleHQtYW5jaG9yPSJtaWRkbGUiIGR5PSIuM2VtIj5ObyBDb3ZlcjwvdGV4dD4KPC9zdmc+';

class NarratorSearchRow extends Component {

  //
  // Lifecycle
  //

  constructor(props, context) {
    super(props, context);

    this.errorRef = React.createRef();
    this.detailsRef = React.createRef();
    this.state = {
      imageError: false
    };
  }

  componentDidUpdate(prevProps) {
    if (this.props.error && this.props.error !== prevProps.error) {
      this.errorRef.current?.scrollIntoView({ block: 'nearest' });
    }

    if (prevProps.isSelecting && !this.props.isSelecting && document.activeElement === this.detailsRef.current) {
      const target = this.props.error ? this.errorRef.current : this.detailsRef.current?.closest('tr')?.querySelector('button');
      target?.focus({ preventScroll: true });
    }

    const previousPhoto = typeof prevProps.narrator === 'object' ? prevProps.narrator.photo : null;
    const currentPhoto = typeof this.props.narrator === 'object' ? this.props.narrator.photo : null;

    if (previousPhoto !== currentPhoto && this.state.imageError) {
      this.setState({ imageError: false });
    }
  }

  //
  // Helpers
  //

  onImageError = () => {
    if (!this.state.imageError) {
      this.setState({
        imageError: true
      });
    }
  };

  //
  // Listeners

  onSelectPress = () => {
    const { narrator, onSelect } = this.props;
    this.detailsRef.current?.focus({ preventScroll: true });
    onSelect(narrator);
  };

  //
  // Render

  render() {
    const {
      narrator,
      status,
      onSelect,
      isSelecting,
      error
    } = this.props;

    const isAvailable = status === 'available';
    const isExisting = status === 'existing';

    const narratorName = typeof narrator === 'string' ? narrator : narrator.name;
    const editionTitle = typeof narrator === 'object' ? narrator.title : null;
    const editionSubtitle = typeof narrator === 'object' ? narrator.subtitle : null;
    const editionDisambiguation = typeof narrator === 'object' ? narrator.disambiguation : null;
    const narratorPhoto = typeof narrator === 'object' ? narrator.photo : null;
    const narratorRating = typeof narrator === 'object' ? narrator.rating : null;
    const narratorDuration = typeof narrator === 'object' ? narrator.duration : null;
    const publisher = typeof narrator === 'object' ? narrator.publisher : null;
    const releaseDate = typeof narrator === 'object' ? narrator.releaseDate : null;
    const overviewRaw = typeof narrator === 'object' ? narrator.overview : null;
    const overview = overviewRaw ? stripHtml(overviewRaw).trim() : null;
    const imageError = this.state.imageError;

    let imageUrl = imageError ? placeholderDataUrl : (narratorPhoto || placeholderDataUrl);

    if (imageUrl && imageUrl.startsWith('/') && window.Chaptarr && window.Chaptarr.urlBase) {
      imageUrl = window.Chaptarr.urlBase + imageUrl;
    }

    let releaseYear = null;
    if (releaseDate) {
      const year = new Date(releaseDate).getFullYear();
      if (!Number.isNaN(year)) {
        releaseYear = year;
      }
    }

    return (
      <TableRow>
        <TableRowCell className={styles.narrator}>
          <div className={styles.narratorContainer}>
            <div className={styles.bookCover}>
              <img
                src={imageUrl}
                alt={translate('NarratorEditionCoverAlt', { narratorName })}
                onError={this.onImageError}
                loading="lazy"
              />
            </div>
            <div
              ref={this.detailsRef}
              className={styles.narratorDetails}
              tabIndex={-1}
            >
              {!!editionTitle && (
                <div className={styles.editionTitle}>
                  {editionTitle}
                  {editionSubtitle ? `: ${editionSubtitle}` : null}
                  {editionDisambiguation ? ` (${editionDisambiguation})` : null}
                </div>
              )}

              <div className={styles.narratorName}>
                {translate('NarratedByWithName', { narratorName })}
              </div>

              {(publisher || releaseYear) && (
                <div className={styles.publisherRow}>
                  {publisher ? publisher : ' '}
                  {publisher && releaseYear ? <span className={styles.publisherDivider}> • </span> : null}
                  {releaseYear ? <span className={styles.releaseYear}>{releaseYear}</span> : null}
                </div>
              )}

              {!!overview && (
                <div className={styles.overview}>
                  {overview}
                </div>
              )}

              {narratorRating && (
                <div className={styles.rating}>
                  {'★'.repeat(Math.round(narratorRating))} {translate('NarratorRatingFraction', { rating: narratorRating.toFixed(1) })}
                </div>
              )}
              {narratorDuration && (
                <div className={styles.duration}>
                  {translate('NarratorDurationDisplay', { hours: Math.floor(narratorDuration / 60), minutes: narratorDuration % 60 })}
                </div>
              )}
              {error && (
                <div ref={this.errorRef} tabIndex={-1}>
                  <Alert kind={kinds.DANGER} role="alert">
                    {error.message || getErrorMessage(error, translate('WantedEditionAddFailed'))}
                  </Alert>
                </div>
              )}
            </div>
          </div>
        </TableRowCell>

        <TableRowCell className={styles.action}>
          {isExisting && (
            <span className={styles.inLibraryStatus}>
              <span className={styles.statusIcon}>✓</span>
              {translate('WantedEditionAlreadyHaveCopy')}
              {narrator.existingCopyMonitored === false ? ` (${translate('WantedEditionBookNotMonitored')})` : null}
            </span>
          )}
          {isAvailable && onSelect && (
            <SpinnerButton
              className={styles.addButton}
              kind={kinds.PRIMARY}
              size={sizes.SMALL}
              isSpinning={isSelecting}
              onPress={this.onSelectPress}
            >
              {translate('AddToLibrary')}
            </SpinnerButton>
          )}
        </TableRowCell>
      </TableRow>
    );
  }
}

NarratorSearchRow.propTypes = {
  narrator: PropTypes.oneOfType([
    PropTypes.string,
    PropTypes.shape({
      title: PropTypes.string,
      subtitle: PropTypes.string,
      disambiguation: PropTypes.string,
      name: PropTypes.string,
      photo: PropTypes.string,
      rating: PropTypes.number,
      duration: PropTypes.number,
      publisher: PropTypes.string,
      releaseDate: PropTypes.string,
      overview: PropTypes.string,
      source: PropTypes.string,
      editionId: PropTypes.number,
      narratorNames: PropTypes.arrayOf(PropTypes.string),
      monitored: PropTypes.bool,
      existingCopyMonitored: PropTypes.bool,
      status: PropTypes.string
    })
  ]).isRequired,
  status: PropTypes.oneOf(['available', 'existing']).isRequired,
  onSelect: PropTypes.func,
  error: PropTypes.object,
  isSelecting: PropTypes.bool.isRequired
};

export default NarratorSearchRow;
