import PropTypes from 'prop-types';
import React from 'react';
import SegmentedToggle from 'Components/Form/SegmentedToggle';
import styles from './InteractiveSearchMediaTypeToggle.css';

function getLabel(mediaType) {
  return mediaType === 'ebook' ? 'eBooks' : 'Audiobooks';
}

function getIcon(mediaType) {
  return mediaType === 'ebook' ?
    <span className={styles.icon}>📖</span> :
    <span className={styles.icon}>🎧</span>;
}

function InteractiveSearchMediaTypeToggle(props) {
  const {
    selectedMediaType,
    siblingMediaType,
    siblingToggleEnabled,
    siblingToggleDisabledReason,
    onMediaTypeChange
  } = props;

  const fallbackSiblingMediaType = selectedMediaType === 'ebook' ? 'audiobook' : 'ebook';
  const alternateMediaType = siblingMediaType || fallbackSiblingMediaType;

  const isAlternateDisabled = siblingToggleEnabled !== true;

  const options = ['audiobook', 'ebook'].map((mediaType) => {
    const isSelected = mediaType === selectedMediaType;
    const isAlternate = mediaType === alternateMediaType && !isSelected;

    return {
      key: mediaType,
      label: getLabel(mediaType),
      icon: getIcon(mediaType),
      isDisabled: isAlternate ? isAlternateDisabled : !isSelected,
      title: isAlternate && isAlternateDisabled ? siblingToggleDisabledReason : null
    };
  });

  return (
    <div className={styles.mediaToggleContainer}>
      <SegmentedToggle
        value={selectedMediaType}
        options={options}
        onChange={onMediaTypeChange}
        ariaLabel="Interactive search media type"
      />
    </div>
  );
}

InteractiveSearchMediaTypeToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  siblingMediaType: PropTypes.oneOf(['audiobook', 'ebook']),
  siblingToggleEnabled: PropTypes.bool,
  siblingToggleDisabledReason: PropTypes.string,
  onMediaTypeChange: PropTypes.func.isRequired
};

export default InteractiveSearchMediaTypeToggle;
