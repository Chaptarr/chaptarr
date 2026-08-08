import PropTypes from 'prop-types';
import React, { Component } from 'react';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageToolbar from 'Components/Page/Toolbar/PageToolbar';
import PageToolbarSection from 'Components/Page/Toolbar/PageToolbarSection';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import Label from 'Components/Label';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './SeriesDetails.css';

class SeriesDetails extends Component {
  render() {
    const {
      title,
      description,
      workCount,
      primaryWorkCount,
      numbered
    } = this.props;

    return (
      <PageContent title={title}>
        <PageToolbar>
          <PageToolbarSection>
            <PageToolbarButton
              label={translate('RefreshMonitored')}
              iconName={icons.REFRESH}
              spinningName={icons.REFRESH}
              onPress={this.onRefreshPress}
            />
          </PageToolbarSection>
        </PageToolbar>

        <PageContentBody>
          <div className={styles.contentContainer}>
            <div className={styles.header}>
              <h1 className={styles.title}>{title}</h1>
              <div className={styles.details}>
                <Label>{workCount} {translate('Books')}</Label>
                {primaryWorkCount > 0 && (
                  <Label kind="primary">{primaryWorkCount} {translate('Primary')}</Label>
                )}
                {numbered && (
                  <Label kind="info">{translate('Numbered')}</Label>
                )}
              </div>
            </div>

            {description && (
              <div className={styles.description}>
                <h3>{translate('Description')}</h3>
                <p>{description}</p>
              </div>
            )}

            <div className={styles.books}>
              <h3>{translate('BooksInSeries')}</h3>
              {/* Book list will be implemented here */}
            </div>
          </div>
        </PageContentBody>
      </PageContent>
    );
  }

  onRefreshPress = () => {
    // Implement refresh logic
  };
}

SeriesDetails.propTypes = {
  id: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  description: PropTypes.string,
  workCount: PropTypes.number,
  primaryWorkCount: PropTypes.number,
  numbered: PropTypes.bool
};

SeriesDetails.defaultProps = {
  workCount: 0,
  primaryWorkCount: 0,
  numbered: false
};

export default SeriesDetails;