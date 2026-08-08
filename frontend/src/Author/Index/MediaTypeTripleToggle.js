import PropTypes from 'prop-types';
import React from 'react';
import SegmentedToggle from 'Components/Form/SegmentedToggle';
import styles from './MediaTypeTripleToggle.css';

function MediaTypeTripleToggle(props) {
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

MediaTypeTripleToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'all', 'ebook']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired
};

export default MediaTypeTripleToggle;
