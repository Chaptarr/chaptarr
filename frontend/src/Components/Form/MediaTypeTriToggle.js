import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import { align, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './MediaTypeTriToggle.css';

function MediaTypeTriToggle(props) {
  const { selectedMediaType, onMediaTypeChange, className } = props;

  return (
    <div className={`${styles.toggleContainer} ${className || ''}`}>
      <Button
        kind={selectedMediaType === 'audiobook' ? kinds.SUCCESS : kinds.DEFAULT}
        buttonGroupPosition={align.LEFT}
        onPress={() => onMediaTypeChange('audiobook')}
      >
        <span className={styles.icon}>{'🎧'}</span> {translate('Audiobooks')}
      </Button>

      <Button
        kind={selectedMediaType === 'ebook' ? kinds.SUCCESS : kinds.DEFAULT}
        buttonGroupPosition={align.CENTER}
        onPress={() => onMediaTypeChange('ebook')}
      >
        <span className={styles.icon}>{'📖'}</span> {translate('Ebooks')}
      </Button>

      <Button
        kind={selectedMediaType === 'both' ? kinds.SUCCESS : kinds.DEFAULT}
        buttonGroupPosition={align.RIGHT}
        onPress={() => onMediaTypeChange('both')}
      >
        <span className={styles.icon}>{'📚'}</span> {translate('Both')}
      </Button>
    </div>
  );
}

MediaTypeTriToggle.propTypes = {
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired,
  onMediaTypeChange: PropTypes.func.isRequired,
  className: PropTypes.string
};

export default MediaTypeTriToggle;
