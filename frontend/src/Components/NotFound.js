import PropTypes from 'prop-types';
import React from 'react';
import PageContent from 'Components/Page/PageContent';
import translate, { translateWithFallback } from 'Utilities/String/translate';
import styles from './NotFound.css';

function NotFound({
  message = translateWithFallback('NotFoundMessage', 'You must be lost, nothing to see here.')
}) {
  return (
    <PageContent title={translate('MIA')}>
      <div className={styles.container}>
        <div className={styles.message}>
          {message}
        </div>

        <img
          className={styles.image}
          src={`${window.Chaptarr.urlBase}/Content/Images/404.png`}
        />
      </div>
    </PageContent>
  );
}

NotFound.propTypes = {
  message: PropTypes.string
};

export default NotFound;
