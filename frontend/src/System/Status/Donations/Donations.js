import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import Link from 'Components/Link/Link';
import translate from 'Utilities/String/translate';
import styles from '../styles.css';

class Donations extends Component {

  //
  // Render

  render() {
    return (
      <FieldSet legend={translate('DonationsLegend')} id="donations">
        <div className={styles.donationContainer}>
          <div className={styles.donationText}>
            <p>{translate('DonationsIntro')}</p>
          </div>

          <div className={styles.donationButtons}>
            <Link
              to="https://ko-fi.com/chaptarr"
              className={styles.donationButton}
              title={translate('DonationsKofiTitle')}
            >
              <div className={styles.buttonContent}>
                <span className={styles.buttonIcon}>{'☕'}</span>
                <span className={styles.buttonText}>{translate('DonationsKofiLabel')}</span>
                <span className={styles.buttonSubtext}>{translate('DonationsKofiSubtext')}</span>
              </div>
            </Link>
          </div>

          <div className={styles.donationFooter}>
            <p><small>{translate('DonationsFooter')}</small></p>
          </div>
        </div>
      </FieldSet>
    );
  }
}

Donations.propTypes = {

};

export default Donations;
