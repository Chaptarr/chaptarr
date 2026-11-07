import PropTypes from 'prop-types';
import React from 'react';
import SegmentedToggle from 'Components/Form/SegmentedToggle';
import translate from 'Utilities/String/translate';
import styles from './MediaTypeToggle.css';

function MediaTypeToggle(props) {
  const {
    selectedMediaType,
    onMediaTypeChange,
    hasAudiobookRootFolder = true,
    hasEbookRootFolder = true,
    includeBoth = false
  } = props;

  const options = [
    {
      key: 'audiobook',
      label: translate('Audiobooks'),
      icon: <span className={styles.icon} aria-hidden="true">🎧</span>,
      isDisabled: !hasAudiobookRootFolder
    }
  ];

  if (includeBoth) {
    options.push({
      key: 'both',
      label: translate('Both'),
      icon: <span className={styles.icon} aria-hidden="true">{'🎧📖'}</span>,
      title: translate('AudiobooksAndEbooks'),
      isDisabled: !hasAudiobookRootFolder && !hasEbookRootFolder
    });
  }

  options.push({
    key: 'ebook',
    label: translate('Ebooks'),
    icon: <span className={styles.icon} aria-hidden="true">📖</span>,
    isDisabled: !hasEbookRootFolder
  });

  return (
    <div className={styles.mediaToggleContainer}>
      <SegmentedToggle
        value={selectedMediaType}
        options={options}
        ariaLabel={translate('Format')}
        onChange={onMediaTypeChange}
      />
    </div>
  );
}

MediaTypeToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  hasAudiobookRootFolder: PropTypes.bool,
  hasEbookRootFolder: PropTypes.bool,
  includeBoth: PropTypes.bool
};

export default MediaTypeToggle;
