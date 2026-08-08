import PropTypes from 'prop-types';
import React, { useState } from 'react';
import translate from 'Utilities/String/translate';
import styles from './FlipCard.css';

function FlipCard({ front, back, initiallyFlipped = false, className }) {
  const [isFlipped, setIsFlipped] = useState(initiallyFlipped);

  const onToggle = () => setIsFlipped((f) => !f);

  return (
    <div className={className}>
      <div className={styles.flipCardContainer}>
        <div className={`${styles.flipCardInner} ${isFlipped ? styles.isFlipped : ''}`}>
          <div className={`${styles.flipCardFace} ${styles.flipCardFront}`}>
            {front}
            <div style={{ marginTop: 8 }}>
              <button type="button" className={styles.flipToggle}
                onClick={onToggle} aria-pressed={isFlipped}
                aria-label={translate('FlipCardShowDetails')}
              >
                {translate('Details')}
              </button>
            </div>
          </div>
          <div className={`${styles.flipCardFace} ${styles.flipCardBack}`}>
            {back}
            <div style={{ marginTop: 8 }}>
              <button type="button" className={styles.flipToggle}
                onClick={onToggle} aria-pressed={!isFlipped}
                aria-label={translate('FlipCardShowSummary')}
              >
                {translate('Back')}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

FlipCard.propTypes = {
  front: PropTypes.node.isRequired,
  back: PropTypes.node.isRequired,
  initiallyFlipped: PropTypes.bool,
  className: PropTypes.string
};

export default FlipCard;

