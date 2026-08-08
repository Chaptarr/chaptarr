import PropTypes from 'prop-types';
import React from 'react';
import SegmentedToggle from 'Components/Form/SegmentedToggle';
import styles from './MediaTypeToggle.css';

function MediaTypeToggle(props) {
  const {
    selectedMediaType,
    onMediaTypeChange,
    hasAudiobookRootFolder = true,
    hasEbookRootFolder = true
  } = props;

  const options = [
    {
      key: 'audiobook',
      label: 'Audiobooks',
      icon: <span className={styles.icon}>🎧</span>,
      isDisabled: !hasAudiobookRootFolder
    },
    {
      key: 'ebook',
      label: 'eBooks',
      icon: <span className={styles.icon}>📖</span>,
      isDisabled: !hasEbookRootFolder
    }
  ];

  return (
    <div className={styles.mediaToggleContainer}>
      <SegmentedToggle
        value={selectedMediaType}
        options={options}
        onChange={onMediaTypeChange}
      />
    </div>
  );
}

MediaTypeToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  hasAudiobookRootFolder: PropTypes.bool,
  hasEbookRootFolder: PropTypes.bool
};

export default MediaTypeToggle;
