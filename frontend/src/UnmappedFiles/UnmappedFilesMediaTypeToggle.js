import PropTypes from 'prop-types';
import React from 'react';
import SegmentedToggle from 'Components/Form/SegmentedToggle';
import styles from './UnmappedFilesMediaTypeToggle.css';

function UnmappedFilesMediaTypeToggle(props) {
  const {
    selectedMediaType,
    onMediaTypeChange
  } = props;

  const options = [
    {
      key: 'audiobook',
      label: 'Audiobooks',
      icon: <span className={styles.icon}>🎧</span>
    },
    {
      key: 'all',
      label: 'All'
    },
    {
      key: 'ebook',
      label: 'eBooks',
      icon: <span className={styles.icon}>📖</span>
    }
  ];

  return (
    <div className={styles.toggleContainer}>
      <SegmentedToggle
        value={selectedMediaType}
        options={options}
        onChange={onMediaTypeChange}
      />
    </div>
  );
}

UnmappedFilesMediaTypeToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['all', 'audiobook', 'ebook']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired
};

export default UnmappedFilesMediaTypeToggle;
