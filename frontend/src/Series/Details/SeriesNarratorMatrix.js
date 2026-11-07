import PropTypes from 'prop-types';
import React, { useEffect, useState } from 'react';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './SeriesNarratorMatrix.css';

function SeriesNarratorMatrix({ title, authorId }) {
  const [state, setState] = useState({
    title,
    authorId,
    isFetching: true,
    error: false,
    matrix: null
  });
  const [retryCount, setRetryCount] = useState(0);

  useEffect(() => {
    let isCurrent = true;
    const query = authorId ? `&authorId=${authorId}` : '';
    const { request, abortRequest } = createAjaxRequest({
      url: `/series/narrators/matrix?title=${encodeURIComponent(title)}${query}`,
      dataType: 'json'
    });

    setState({ title, authorId, isFetching: true, error: false, matrix: null });
    request.done((matrix) => {
      if (!isCurrent) {
        return;
      }

      setState({ title, authorId, isFetching: false, error: false, matrix });
    });
    request.fail((xhr) => {
      if (!isCurrent || xhr.aborted) {
        return;
      }

      // A series with no audiobook editions is not an error worth showing.
      setState({ title, authorId, isFetching: false, error: xhr.status !== 404, matrix: null });
    });

    return () => {
      isCurrent = false;
      abortRequest();
    };
  }, [title, authorId, retryCount]);

  const { isFetching, error, matrix } = state;

  if (state.title !== title || state.authorId !== authorId || isFetching) {
    return <LoadingIndicator />;
  }

  if (error) {
    return (
      <div className={styles.message} role="alert">
        <span>{translate('NarratorComparisonFailed')}</span>
        <Button onPress={() => setRetryCount((count) => count + 1)}>
          {translate('Retry')}
        </Button>
      </div>
    );
  }

  if (!matrix || !matrix.narrators.length) {
    return null;
  }

  const titles = new Map(matrix.books.map((book) => [book.bookId, book.title]));

  return (
    <section className={styles.container} aria-labelledby="series-narrator-matrix">
      <h3 id="series-narrator-matrix">{translate('NarratorComparison')}</h3>
      <p className={styles.help}>{translate('NarratorComparisonHelp')}</p>
      <ul className={styles.list}>
        {matrix.narrators.map((narrator) => (
          <li key={narrator.name} className={styles.narrator}>
            <p className={styles.name}>
              {narrator.name}{' '}
              {narrator.production === 'full-cast' && <Label kind="primary">{translate('EditionFullCast')}</Label>}
              {!narrator.missingBookIds.length && <Label kind="success">{translate('NarratorComplete')}</Label>}
            </p>
            <div className={styles.row}>
              {translate('NarratorCoverage')}: {narrator.coveredBookIds.length} / {matrix.totalBooks}
            </div>
            {narrator.ownedBookIds.length > 0 && (
              <div className={styles.row}>
                {translate('NarratorOwned')}: {narrator.ownedBookIds.length}
              </div>
            )}
            {narrator.missingBookIds.length > 0 && (
              <div className={styles.row}>
                {translate('NarratorMissing')}:{' '}
                <span className={styles.volumes}>
                  {narrator.missingBookIds.map((id) => titles.get(id)).filter(Boolean).join(' · ')}
                </span>
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

SeriesNarratorMatrix.propTypes = {
  title: PropTypes.string.isRequired,
  authorId: PropTypes.number
};

export default SeriesNarratorMatrix;
