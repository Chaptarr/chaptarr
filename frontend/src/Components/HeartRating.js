import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import styles from './HeartRating.css';

function HeartRating({ rating, iconSize }) {
  const hasRating = Number.isFinite(rating);
  const displayValue = hasRating ? rating.toFixed(1) : '—';
  
  return (
    <span className={styles.rating} title={hasRating ? displayValue : 'No rating'}>
      <Icon
        className={styles.heart}
        name={icons.HEART}
        size={iconSize}
      />

      {displayValue}
    </span>
  );
}

HeartRating.propTypes = {
  rating: PropTypes.number,
  iconSize: PropTypes.number.isRequired
};

HeartRating.defaultProps = {
  iconSize: 14
};

export default HeartRating;
