import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { PureComponent } from 'react';
import { ColorImpairedConsumer } from 'App/ColorImpairedContext';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './BookIndexFooter.css';

class BookIndexFooter extends PureComponent {
  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._footerRef = React.createRef();
    this._lastPinnedHeight = null;
  }

  componentDidMount() {
    window.addEventListener('resize', this.updatePinnedHeight);
    this.updatePinnedHeight();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isFetchingStatistics !== this.props.isFetchingStatistics ||
        prevProps.isSticky !== this.props.isSticky ||
        prevProps.statistics !== this.props.statistics) {
      this.updatePinnedHeight();
    }
  }

  componentWillUnmount() {
    window.removeEventListener('resize', this.updatePinnedHeight);

    // Reset pinned height for next page/view
    if (typeof document !== 'undefined') {
      document.documentElement.style.setProperty('--bookIndexFooterPinnedHeight', '0px');
    }
  }

  updatePinnedHeight = () => {
    if (typeof document === 'undefined') {
      return;
    }

    if (!this.props.isSticky) {
      this._lastPinnedHeight = 0;
      document.documentElement.style.setProperty('--bookIndexFooterPinnedHeight', '0px');
      return;
    }

    const el = this._footerRef?.current;
    if (!el) {
      return;
    }

    const height = Math.ceil(el.getBoundingClientRect().height);
    if (height === this._lastPinnedHeight) {
      return;
    }

    this._lastPinnedHeight = height;
    document.documentElement.style.setProperty('--bookIndexFooterPinnedHeight', `${height}px`);
  };

  //
  // Render

  render() {
    const {
      statistics,
      isFetchingStatistics,
      isSticky
    } = this.props;

    const {
      totalBooks = 0,
      monitoredBooks = 0,
      fileCount = 0,
      totalFileSize = 0,
      authorCount = 0
    } = statistics || {};

    return (
      <ColorImpairedConsumer>
        {(enableColorImpairedMode) => {
          return (
            <div ref={this._footerRef} className={classNames(styles.footer, isSticky && styles.sticky)}>
              <div className={styles.legend}>
                <div className={styles.legendItem}>
                  <div
                    className={classNames(
                      styles.filesAvailable,
                      enableColorImpairedMode && 'colorImpaired'
                    )}
                  />
                  <div>
                    {translate('BookFilesAvailable')}
                  </div>
                </div>

                <div className={styles.legendItem}>
                  <div
                    className={classNames(
                      styles.missingMonitored,
                      enableColorImpairedMode && 'colorImpaired'
                    )}
                  />
                  <div>
                    {translate('MissingBookMonitored')}
                  </div>
                </div>

                <div className={styles.legendItem}>
                  <div
                    className={classNames(
                      styles.unreleasedMonitored,
                      enableColorImpairedMode && 'colorImpaired'
                    )}
                  />
                  <div>
                    {translate('UnreleasedOrUnknownBookMonitored')}
                  </div>
                </div>

                <div className={styles.legendItem}>
                  <div
                    className={classNames(
                      styles.missingUnmonitored,
                      enableColorImpairedMode && 'colorImpaired'
                    )}
                  />
                  <div>
                    {translate('BookNoFilesUnmonitored')}
                  </div>
                </div>
              </div>

              <div className={styles.statistics}>
                {isFetchingStatistics ? (
                  <LoadingIndicator />
                ) : (
                  <>
                    <DescriptionList>
                      <DescriptionListItem
                        title={translate('Monitored')}
                        data={monitoredBooks}
                      />

                      <DescriptionListItem
                        title={translate('Unmonitored')}
                        data={totalBooks - monitoredBooks}
                      />
                    </DescriptionList>

                    <DescriptionList>
                      <DescriptionListItem
                        title={translate('Authors')}
                        data={authorCount}
                      />

                      <DescriptionListItem
                        title={translate('Books')}
                        data={totalBooks}
                      />

                      <DescriptionListItem
                        title={translate('Files')}
                        data={fileCount}
                      />
                    </DescriptionList>

                    <DescriptionList>
                      <DescriptionListItem
                        title={translate('TotalFileSize')}
                        data={formatBytes(totalFileSize)}
                      />
                    </DescriptionList>
                  </>
                )}
              </div>
            </div>
          );
        }}
      </ColorImpairedConsumer>
    );
  }
}

BookIndexFooter.propTypes = {
  statistics: PropTypes.object,
  isFetchingStatistics: PropTypes.bool.isRequired,
  isSticky: PropTypes.bool
};

BookIndexFooter.defaultProps = {
  isSticky: false
};

export default BookIndexFooter;
