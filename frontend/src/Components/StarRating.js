import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import styles from './StarRating.css';

function StarRating({ rating, votes, iconSize }) {
  const hasRating = Number.isFinite(rating);
  const safeVotes = Number.isFinite(votes) ? votes : 0;
  const safeRating = hasRating ? Math.max(0, Math.min(5, rating)) : 0;
  
  const starWidth = {
    width: `${safeRating * 20}%`
  };

  const helpText = hasRating ? `${safeRating.toFixed(1)} (${safeVotes} Votes)` : 'No rating';

  return (
    <span className={styles.starRating} title={helpText}>
      <div className={styles.backStar}>
        <Icon name={icons.STAR_FULL} size={iconSize} />
        <Icon name={icons.STAR_FULL} size={iconSize} />
        <Icon name={icons.STAR_FULL} size={iconSize} />
        <Icon name={icons.STAR_FULL} size={iconSize} />
        <Icon name={icons.STAR_FULL} size={iconSize} />
        <div className={styles.frontStar} style={starWidth}>
          <Icon name={icons.STAR_FULL} size={iconSize} />
          <Icon name={icons.STAR_FULL} size={iconSize} />
          <Icon name={icons.STAR_FULL} size={iconSize} />
          <Icon name={icons.STAR_FULL} size={iconSize} />
          <Icon name={icons.STAR_FULL} size={iconSize} />
        </div>
      </div>
    </span>
  );
}

StarRating.propTypes = {
  rating: PropTypes.number,
  votes: PropTypes.number,
  iconSize: PropTypes.number.isRequired
};

StarRating.defaultProps = {
  iconSize: 14
};

export default StarRating;
