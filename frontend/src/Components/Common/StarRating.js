import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';
import styles from './StarRating.css';

function StarRating({ rating, voteCount, showNumeric = true, maxRating = 5, size = 'medium' }) {
  if (!rating) {
    return null;
  }

  // Calculate number of full stars, half stars, and empty stars
  const fullStars = Math.floor(rating);
  const hasHalfStar = rating - fullStars >= 0.5;
  const emptyStars = maxRating - fullStars - (hasHalfStar ? 1 : 0);

  const sizeClass = styles[`size-${size}`] || styles.sizeMedium;

  return (
    <div className={`${styles.starRating} ${sizeClass}`}>
      <div className={styles.stars}>
        {/* Full stars */}
        {Array(fullStars).fill().map((_, index) => (
          <span key={`full-${index}`} className={`${styles.star} ${styles.full}`}>
            ★
          </span>
        ))}
        
        {/* Half star */}
        {hasHalfStar && (
          <span className={`${styles.star} ${styles.half}`}>
            ★
          </span>
        )}
        
        {/* Empty stars */}
        {Array(emptyStars).fill().map((_, index) => (
          <span key={`empty-${index}`} className={`${styles.star} ${styles.empty}`}>
            ☆
          </span>
        ))}
      </div>
      
      {showNumeric && (
        <span className={styles.rating}>
          {rating.toFixed(1)}/{maxRating}
          {voteCount && (
            <span className={styles.voteCount}>
              {translate('StarRatingsCount', { count: voteCount.toLocaleString() })}
            </span>
          )}
        </span>
      )}
    </div>
  );
}

StarRating.propTypes = {
  rating: PropTypes.number,
  voteCount: PropTypes.number,
  showNumeric: PropTypes.bool,
  maxRating: PropTypes.number,
  size: PropTypes.oneOf(['small', 'medium', 'large'])
};

export default StarRating;