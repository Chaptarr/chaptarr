import PropTypes from 'prop-types';
import React from 'react';
import Button from 'Components/Link/Button';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './NoAuthor.css';

function NoAuthor(props) {
  const {
    totalItems,
    itemType,
    isFiltered
  } = props;

  if (isFiltered || totalItems > 0) {
    return (
      <div>
        <div className={styles.message}>
          {translate('AllResultsAreHiddenByTheAppliedFilter')}
        </div>
      </div>
    );
  }

  return (
    <div>
      <div className={styles.message}>
        {`No ${itemType} found. Let's get started!`}
      </div>

      <div className={styles.buttonContainer}>
        <Button
          to="/system/quickstart"
          kind={kinds.PRIMARY}
        >
          {translate('Quickstart')}
        </Button>
      </div>
    </div>
  );
}

NoAuthor.propTypes = {
  totalItems: PropTypes.number,
  isFiltered: PropTypes.bool,
  itemType: PropTypes.string.isRequired
};

NoAuthor.defaultProps = {
  totalItems: 0,
  isFiltered: false,
  itemType: 'authors'
};

export default NoAuthor;
