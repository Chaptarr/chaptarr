import PropTypes from 'prop-types';
import React, { Component } from 'react';
import NarratorSearchModal from 'Book/Narrator/NarratorSearchModal';
import MetadataSearchIcon from 'Components/Icon/Icons/MetadataSearchIcon';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Tooltip from 'Components/Tooltip/Tooltip';
import { tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './NarratorCell.css';

class NarratorCell extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      isNarratorSearchModalOpen: false
    };
  }

  //
  // Listeners

  onNarratorSearchPress = () => {
    this.setState({ isNarratorSearchModalOpen: true });
  };

  onNarratorSearchModalClose = () => {
    this.setState({ isNarratorSearchModalOpen: false });
  };

  //
  // Render

  render() {
    const {
      bookId,
      narrator,
      narratorNames,
      availableNarrators,
      authorName,
      bookTitle,
      hasFiles,
      hasPinnedEdition,
      className
    } = this.props;

    const {
      isNarratorSearchModalOpen
    } = this.state;

    // Only show narrator text when it is tied to either:
    // - a physical copy on disk (hasFiles), or
    // - a user-pinned edition/narrator (manualAdd)
    // Otherwise we risk showing a narrator for an arbitrary edition.
    const suppressNarratorText = hasFiles === false && hasPinnedEdition !== true;

    return (
      <div className={className}>
        <div className={styles.narratorDisplay}>
          {(() => {
            const narratorNameCandidates = (() => {
              if (suppressNarratorText) {
                return [];
              }

              if (Array.isArray(narratorNames) && narratorNames.length > 0) {
                return narratorNames;
              }

              if (Array.isArray(availableNarrators) && availableNarrators.length > 0) {
                return availableNarrators;
              }

              return [];
            })();

            const cleanNarratorNames = narratorNameCandidates
              .map((name) => (typeof name === 'string' ? name.trim() : ''))
              .filter((name) => name)
              // Treat "Full Cast" as a display label, not a real narrator name in the list.
              .filter((name) => name.toLowerCase() !== 'full cast');

            const tooltipNames = (() => {
              if (cleanNarratorNames.length > 0) {
                return cleanNarratorNames;
              }

              if (!Array.isArray(availableNarrators)) {
                return [];
              }

              return availableNarrators
                .map((name) => (typeof name === 'string' ? name.trim() : ''))
                .filter((name) => name)
                .filter((name) => name.toLowerCase() !== 'full cast');
            })();

            const isMultiNarratorFromList = cleanNarratorNames.length > 1;
            const isSingleNarratorFromList = cleanNarratorNames.length === 1;
            const narratorIsObject = narrator && typeof narrator === 'object';

            const narratorOverride = (() => {
              if (isMultiNarratorFromList) {
                return cleanNarratorNames[0];
              }

              if (isSingleNarratorFromList) {
                if (narratorIsObject) {
                  return null;
                }

                return cleanNarratorNames[0];
              }

              return null;
            })();

            const effectiveNarrator = suppressNarratorText ? null : (narratorOverride ?? narrator);
            const showMoreIndicator = isMultiNarratorFromList;

            if (!effectiveNarrator) {
              return (
                <span className={styles.narratorTextPlain}>
                  {/* No narrator */}
                </span>
              );
            }

            const isObject = typeof effectiveNarrator === 'object';
            const narratorName = isObject ? (effectiveNarrator.name || effectiveNarrator.narratorName) : effectiveNarrator;
            const normalized = typeof narratorName === 'string' ? narratorName.trim().toLowerCase() : '';

            const isFullCast = normalized === 'full cast';

            const title = (showMoreIndicator && tooltipNames.length > 1) ?
              tooltipNames.join(', ') :
              narratorName;

            const nameElement = (
              <span
                className={styles.narratorTextPlain}
                title={title}
              >
                {narratorName}
              </span>
            );

            const anchor = (
              <span className={styles.narratorComposite}>
                {nameElement}

                {
                  showMoreIndicator &&
                    <span className={styles.moreIndicator}>
                      {', …'}
                    </span>
                }
              </span>
            );

            if ((isFullCast || showMoreIndicator) && tooltipNames.length > 1) {
              const tooltip = (
                <div>
                  {tooltipNames.map((name) => (
                    <div key={name}>
                      {name}
                    </div>
                  ))}
                </div>
              );

              return (
                <Tooltip
                  anchor={anchor}
                  tooltip={tooltip}
                  position={tooltipPositions.TOP}
                />
              );
            }

            return anchor;
          })()}

          <SpinnerButton
            className={styles.searchIcon}
            title={translate('NarratorSearchTooltip')}
            isSpinning={false}
            onPress={this.onNarratorSearchPress}
          >
            <MetadataSearchIcon size={12} />
          </SpinnerButton>
        </div>

        <NarratorSearchModal
          isOpen={isNarratorSearchModalOpen}
          bookId={bookId}
          currentNarrator={narrator}
          authorName={authorName}
          bookTitle={bookTitle}
          onModalClose={this.onNarratorSearchModalClose}
        />
      </div>
    );
  }
}

NarratorCell.propTypes = {
  bookId: PropTypes.number.isRequired,
  narrator: PropTypes.oneOfType([
    PropTypes.string,
    PropTypes.shape({
      id: PropTypes.number,
      titleSlug: PropTypes.string,
      name: PropTypes.string,
      narratorName: PropTypes.string
    })
  ]),
  narratorNames: PropTypes.arrayOf(PropTypes.string),
  availableNarrators: PropTypes.arrayOf(PropTypes.string),
  authorName: PropTypes.string.isRequired,
  bookTitle: PropTypes.string.isRequired,
  hasFiles: PropTypes.bool,
  hasPinnedEdition: PropTypes.bool,
  className: PropTypes.string
};

export default NarratorCell;
