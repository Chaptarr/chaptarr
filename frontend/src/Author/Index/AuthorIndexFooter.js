import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { PureComponent } from 'react';
import { ColorImpairedConsumer } from 'App/ColorImpairedContext';
import DescriptionList from 'Components/DescriptionList/DescriptionList';
import DescriptionListItem from 'Components/DescriptionList/DescriptionListItem';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './AuthorIndexFooter.css';

class AuthorIndexFooter extends PureComponent {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      books: 0,
      bookFiles: 0,
      totalFileSize: 0
    };
  }

  componentDidMount() {
    this.fetchAggregateStatistics();
  }

  componentDidUpdate(prevProps) {
    const { author, mediaType } = this.props;
    const { author: prevAuthor, mediaType: prevMediaType } = prevProps;

    // Update if authors changed or mediaType changed
    if (author !== prevAuthor || mediaType !== prevMediaType) {
      this.fetchAggregateStatistics();
    }
  }

  //
  // Listeners

  fetchAggregateStatistics = () => {
    const { author, mediaType } = this.props;

    if (!author || author.length === 0) {
      this.setState({
        books: 0,
        bookFiles: 0,
        totalFileSize: 0
      });
      return;
    }

    const authorIds = author.map(a => a.id);

    fetch('/api/v1/author/statistics/aggregate', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Api-Key': window.Chaptarr.apiKey
      },
      body: JSON.stringify({
        authorIds,
        mediaType: mediaType || 'all'
      })
    })
      .then(response => response.json())
      .then(data => {
        this.setState({
          books: data.bookCount || 0,
          bookFiles: data.fileCount || 0,
          totalFileSize: data.totalFileSize || 0
        });
      })
      .catch(error => {
        console.error('Failed to fetch aggregate statistics:', error);
        // Fallback to client-side calculation on error
        this.calculateClientSideStats();
      });
  }

  calculateClientSideStats = () => {
    const { author } = this.props;
    let books = 0;
    let bookFiles = 0;
    let totalFileSize = 0;

    author.forEach((s) => {
      const { statistics = {} } = s;

      const {
        bookCount = 0,
        bookFileCount = 0,
        sizeOnDisk = 0
      } = statistics;

      books += bookCount;
      bookFiles += bookFileCount;
      totalFileSize += sizeOnDisk;
    });

    this.setState({
      books,
      bookFiles,
      totalFileSize
    });
  }

  //
  // Render

  render() {
    const { author } = this.props;
    const { books, bookFiles, totalFileSize } = this.state;
    const count = author.length;
    let ended = 0;
    let continuing = 0;
    let monitored = 0;

    author.forEach((s) => {
      if (s.status === 'ended') {
        ended++;
      } else {
        continuing++;
      }

      if (s.monitored) {
        monitored++;
      }
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
                    {translate('ContinuingAllBooksDownloaded')}
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
                    {translate('EndedAllBooksDownloaded')}
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
                    title={translate('Ended')}
                    data={ended}
                  />

                  <DescriptionListItem
                    title={translate('Continuing')}
                    data={continuing}
                  />
                </DescriptionList>

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
