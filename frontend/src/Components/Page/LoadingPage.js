import React from 'react';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import LoadingMessage from 'Components/Loading/LoadingMessage';
import styles from './LoadingPage.css';

function LoadingPage() {
  return (
    <div className={styles.page}>
      <img
        className={styles.logoFull}
        src={`${window.Chaptarr.urlBase}/Content/Images/chaptarr-logo.svg`}
        alt="Chaptarr"
      />
      <LoadingMessage />
      <LoadingIndicator />
    </div>
  );
}

export default LoadingPage;
