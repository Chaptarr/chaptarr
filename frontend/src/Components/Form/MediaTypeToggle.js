import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';
import SegmentedToggle from './SegmentedToggle';
import styles from './MediaTypeToggle.css';

function MediaTypeToggle(props) {
  const {
    selectedMediaType,
    onMediaTypeChange,
    hasAudiobookRootFolder = true,
    hasEbookRootFolder = true,
    includeBoth = false,
    className,
    children
  } = props;

  const audiobookDisabled = !hasAudiobookRootFolder;
  const ebookDisabled = !hasEbookRootFolder;
  const bothDisabled = includeBoth && (audiobookDisabled || ebookDisabled);

  const effectiveMediaType = (!includeBoth && selectedMediaType === 'both') ?
    'audiobook' :
    selectedMediaType;

  const options = [
    {
      key: 'audiobook',
      label: translate('Audiobooks'),
      icon: <span className={styles.icon} aria-hidden="true">🎧</span>,
      isDisabled: audiobookDisabled
    },
    includeBoth ? {
      key: 'both',
      label: translate('Both'),
      icon: null,
      isDisabled: bothDisabled
    } : null,
    {
      key: 'ebook',
      label: translate('Ebooks'),
      icon: <span className={styles.icon} aria-hidden="true">📖</span>,
      isDisabled: ebookDisabled
    }
  ].filter(Boolean);

  return (
    <div className={`${styles.toggleContainer} ${className || ''}`}>
      <SegmentedToggle
        value={effectiveMediaType}
        options={options}
        ariaLabel={translate('Format')}
        onChange={onMediaTypeChange}
      />
      {children}
    </div>
  );
}

MediaTypeToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  hasAudiobookRootFolder: PropTypes.bool,
  hasEbookRootFolder: PropTypes.bool,
  includeBoth: PropTypes.bool,
  className: PropTypes.string,
  children: PropTypes.node
};

export default MediaTypeToggle;
