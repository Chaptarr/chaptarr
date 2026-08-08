import React from 'react';
import PropTypes from 'prop-types';
import FlipCard from 'Components/FlipCard/FlipCard';
import translate from 'Utilities/String/translate';
import styles from './ImportProgressCard.css';

function ProgressBar({ percent }) {
  const safe = Math.max(0, Math.min(100, percent || 0));
  return (
    <div className={styles.progressBar}>
      <div className={styles.progressFill} style={{ width: `${safe}%` }} />
    </div>
  );
}

ProgressBar.propTypes = { percent: PropTypes.number };

function FrontFace({ stage, percent, subtitle }) {
  return (
    <div className={styles.card}>
      <div className={styles.row}>
        <div className={styles.stage}>{stage}</div>
        {subtitle ? <div>{subtitle}</div> : null}
      </div>
      <div style={{ marginTop: 8 }}>
        <ProgressBar percent={percent} />
      </div>
    </div>
  );
}

FrontFace.propTypes = {
  stage: PropTypes.string.isRequired,
  percent: PropTypes.number,
  subtitle: PropTypes.string
};

function BackFace({ authors, books }) {
  const { discovered, staged, imported, local } = authors || {};
  const { possible, matched, filesImported } = books || {};
  return (
    <div className={styles.card}>
      <div className={styles.statsGrid}>
        <div className={styles.statLabel}>{translate('ImportProgressAuthorsDiscovered')}</div>
        <div className={styles.statValue}>{discovered ?? 0}</div>

        <div className={styles.statLabel}>{translate('ImportProgressAuthorsStaged')}</div>
        <div className={styles.statValue}>{staged ?? 0}</div>

        <div className={styles.statLabel}>{translate('ImportProgressAuthorsImported')}</div>
        <div className={styles.statValue}>{imported ?? 0}</div>

        <div className={styles.statLabel}>{translate('ImportProgressLocalAuthors')}</div>
        <div className={styles.statValue}>{local ?? 0}</div>

        <div className={styles.statLabel}>{translate('ImportProgressBooksPossible')}</div>
        <div className={styles.statValue}>{possible ?? 0}</div>

        <div className={styles.statLabel}>{translate('ImportProgressBooksMatched')}</div>
        <div className={styles.statValue}>{matched ?? 0}</div>

        <div className={styles.statLabel}>{translate('ImportProgressFilesImported')}</div>
        <div className={styles.statValue}>{filesImported ?? 0}</div>
      </div>
    </div>
  );
}

BackFace.propTypes = {
  authors: PropTypes.shape({
    discovered: PropTypes.number,
    staged: PropTypes.number,
    imported: PropTypes.number,
    local: PropTypes.number
  }),
  books: PropTypes.shape({
    possible: PropTypes.number,
    matched: PropTypes.number,
    filesImported: PropTypes.number
  })
};

// ImportProgressCard: presentational component. Container is expected to provide live data via props.
function ImportProgressCard({
  stageLabel,
  percent,
  subtitle,
  authors,
  books,
  initiallyFlipped
}) {
  return (
    <FlipCard
      initiallyFlipped={initiallyFlipped}
      front={<FrontFace stage={stageLabel} percent={percent} subtitle={subtitle} />}
      back={<BackFace authors={authors} books={books} />}
    />
  );
}

ImportProgressCard.propTypes = {
  stageLabel: PropTypes.string.isRequired,
  percent: PropTypes.number,
  subtitle: PropTypes.string,
  authors: PropTypes.shape({
    discovered: PropTypes.number,
    staged: PropTypes.number,
    imported: PropTypes.number,
    local: PropTypes.number
  }),
  books: PropTypes.shape({
    possible: PropTypes.number,
    matched: PropTypes.number,
    filesImported: PropTypes.number
  }),
  initiallyFlipped: PropTypes.bool
};

export default ImportProgressCard;

