import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { PureComponent } from 'react';
import { ColorImpairedConsumer } from 'App/ColorImpairedContext';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import { isAuthorMonitoredForSelection } from 'Utilities/Author/getAuthorMediaTypeMonitoringStatus';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './AuthorIndexFooter.css';

class AuthorIndexFooter extends PureComponent {
  //
  // Render

  render() {
    const { author, mediaType } = this.props;
    const count = author.length;
    let ended = 0;
    let continuing = 0;
    let monitored = 0;
    let audiobookConfigured = 0;
    let audiobookMonitored = 0;
    let ebookConfigured = 0;
    let ebookMonitored = 0;
    let books = 0;
    let bookFiles = 0;
    let totalFileSize = 0;

    author.forEach((s) => {
      if (s.status === 'ended') {
        ended++;
      } else {
        continuing++;
      }

      if (isAuthorMonitoredForSelection(s, mediaType)) {
        monitored++;
      }

      if (s.audiobookMonitoring?.isConfigured) {
        audiobookConfigured++;

        if (s.audiobookMonitoring.monitored) {
          audiobookMonitored++;
        }
      }

      if (s.ebookMonitoring?.isConfigured) {
        ebookConfigured++;

        if (s.ebookMonitoring.monitored) {
          ebookMonitored++;
        }
      }

      const {
        bookCount = 0,
        bookFileCount = 0,
        sizeOnDisk = 0
      } = s.statistics || {};

      books += bookCount;
      bookFiles += bookFileCount;
      totalFileSize += sizeOnDisk;
    });

    return (
      <ColorImpairedConsumer>
        {(enableColorImpairedMode) => {
          return (
            <div className={styles.footer}>
              <div>
                <div className={styles.legendItem}>
                  <div
                    className={classNames(
                      styles.continuing,
                      enableColorImpairedMode && 'colorImpaired'
                    )}
                  />
                  <div>
                    {translate('Active')}
                  </div>
                </div>

                <div className={styles.legendItem}>
                  <div
                    className={classNames(
                      styles.ended,
                      enableColorImpairedMode && 'colorImpaired'
                    )}
                  />
                  <div>
                    {translate('Dead')}
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
                    {translate('MissingBooksAuthorMonitored')}
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
                    {translate('MissingBooksAuthorNotMonitored')}
                  </div>
                </div>
              </div>

              <div className={styles.statistics}>
                <DescriptionList>
                  <DescriptionListItem
                    title={translate('Authors')}
                    data={count}
                  />

                  <DescriptionListItem
                    title={translate('Dead')}
                    data={ended}
                  />

                  <DescriptionListItem
                    title={translate('Active')}
                    data={continuing}
                  />
                </DescriptionList>

                {
                  mediaType === 'all' ?
                    <DescriptionList>
                      <DescriptionListItem
                        title={`${translate('Audiobooks')} ${translate('Monitored')}`}
                        data={`${audiobookMonitored} / ${audiobookConfigured}`}
                      />

                      <DescriptionListItem
                        title={`${translate('Ebooks')} ${translate('Monitored')}`}
                        data={`${ebookMonitored} / ${ebookConfigured}`}
                      />
                    </DescriptionList> :
                    <DescriptionList>
                      <DescriptionListItem
                        title={translate('Monitored')}
                        data={monitored}
                      />

                      <DescriptionListItem
                        title={translate('Unmonitored')}
                        data={count - monitored}
                      />
                    </DescriptionList>
                }

                <DescriptionList>
                  <DescriptionListItem
                    title={translate('Books')}
                    data={books}
                  />

                  <DescriptionListItem
                    title={translate('Files')}
                    data={bookFiles}
                  />
                </DescriptionList>

                <DescriptionList>
                  <DescriptionListItem
                    title={translate('TotalFileSize')}
                    data={formatBytes(totalFileSize)}
                  />
                </DescriptionList>
              </div>
            </div>
          );
        }}
      </ColorImpairedConsumer>
    );
  }
}

AuthorIndexFooter.propTypes = {
  author: PropTypes.arrayOf(PropTypes.object).isRequired,
  mediaType: PropTypes.string
};

export default AuthorIndexFooter;
