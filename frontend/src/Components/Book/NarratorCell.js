import PropTypes from 'prop-types';
import React, { Component } from 'react';
import NarratorSearchModal from 'Book/Narrator/NarratorSearchModal';
import Icon from 'Components/Icon';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Tooltip from 'Components/Tooltip/Tooltip';
import { icons, tooltipPositions } from 'Helpers/Props';
import getNarratorNamesForDisplay from 'Utilities/Book/getNarratorNamesForDisplay';
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
      narrators,
      availableNarratorCredits,
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
            const selectedNames = getNarratorNamesForDisplay(narrators, narratorNames);
            const availableNames = getNarratorNamesForDisplay(availableNarratorCredits, availableNarrators);
            const candidates = selectedNames.length ? selectedNames : availableNames;
            const names = suppressNarratorText ? [] : candidates;
            const narratorName = names[0] || (!suppressNarratorText &&
              (typeof narrator === 'object' ? narrator?.name || narrator?.narratorName : narrator));

            if (!narratorName) {
              return <span className={styles.narratorTextPlain} />;
            }

            const showMoreIndicator = names.length > 1;
            const title = showMoreIndicator ? names.join(', ') : narratorName;

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

            if (showMoreIndicator) {
              const tooltip = (
                <div>
                  {names.map((name) => (
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
            <Icon name={icons.NARRATOR_SEARCH} size={12} />
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
  narrators: PropTypes.arrayOf(PropTypes.object),
  availableNarratorCredits: PropTypes.arrayOf(PropTypes.object),
  availableNarrators: PropTypes.arrayOf(PropTypes.string),
  authorName: PropTypes.string.isRequired,
  bookTitle: PropTypes.string.isRequired,
  hasFiles: PropTypes.bool,
  hasPinnedEdition: PropTypes.bool,
  className: PropTypes.string
};

export default NarratorCell;
