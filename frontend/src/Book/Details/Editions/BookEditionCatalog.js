import PropTypes from 'prop-types';
import React, { useEffect, useState } from 'react';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './BookEditionCatalog.css';

const columns = [
  { name: 'title', label: () => translate('Title'), isVisible: true, isSortable: false },
  { name: 'narrators', label: () => translate('EditionNarrators'), isVisible: true, isSortable: false },
  { name: 'production', label: () => translate('EditionProduction'), isVisible: true, isSortable: false },
  { name: 'duration', label: () => translate('Duration'), isVisible: true, isSortable: false },
  { name: 'abridgement', label: () => translate('EditionAbridgement'), isVisible: true, isSortable: false },
  { name: 'language', label: () => translate('Language'), isVisible: true, isSortable: false },
  { name: 'publisher', label: () => translate('Publisher'), isVisible: true, isSortable: false },
  { name: 'status', label: () => translate('Status'), isVisible: true, isSortable: false }
];

export function formatDuration(seconds) {
  if (!Number.isFinite(seconds) || seconds <= 0) {
    return null;
  }

  const totalMinutes = Math.max(1, Math.round(seconds / 60));
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;

  return `${hours}h ${String(minutes).padStart(2, '0')}m`;
}

function UnknownValue({ notApplicable = false }) {
  return (
    <span className={styles.unknown}>
      {translate(notApplicable ? 'EditionNotApplicable' : 'EditionNotProvided')}
    </span>
  );
}

UnknownValue.propTypes = {
  notApplicable: PropTypes.bool
};

function productionLabel(production) {
  if (production === 'full-cast') {
    return translate('EditionFullCast');
  }

  return production === 'solo' ? translate('EditionSolo') : null;
}

function abridgementLabel(abridged) {
  if (abridged === 'abridged') {
    return translate('EditionAbridged');
  }

  return abridged === 'unabridged' ? translate('EditionUnabridged') : null;
}

function BookEditionCatalog({ bookId, mediaType }) {
  const [state, setState] = useState({ bookId, mediaType, isFetching: true, error: false, items: [] });
  const [retryCount, setRetryCount] = useState(0);

  useEffect(() => {
    let isCurrent = true;

    const { request, abortRequest } = createAjaxRequest({
      url: `/book/${bookId}/editions/catalog${mediaType ? `?mediaType=${encodeURIComponent(mediaType)}` : ''}`,
      dataType: 'json'
    });

    setState({ bookId, mediaType, isFetching: true, error: false, items: [] });
    request.done((items) => {
      if (!isCurrent) {
        return;
      }

      setState({ bookId, mediaType, isFetching: false, error: false, items: items || [] });
    });
    request.fail((xhr) => {
      if (!isCurrent || xhr.aborted) {
        return;
      }

      setState({ bookId, mediaType, isFetching: false, error: true, items: [] });
    });

    return () => {
      isCurrent = false;
      abortRequest();
    };
  }, [bookId, mediaType, retryCount]);

  const { isFetching, error, items } = state;

  if (state.bookId !== bookId || state.mediaType !== mediaType || isFetching) {
    return <LoadingIndicator />;
  }

  if (error) {
    return (
      <div className={styles.message} role="alert">
        <span>{translate('EditionCatalogFailed')}</span>
        <Button onPress={() => setRetryCount((count) => count + 1)}>
          {translate('Retry')}
        </Button>
      </div>
    );
  }

  if (!items.length) {
    return <div className={styles.message}>{translate('EditionCatalogEmpty')}</div>;
  }

  return (
    <div className={styles.container}>
      <div className={styles.help}>{translate('EditionCatalogHelp')}</div>
      <Table columns={columns}>
        <TableBody>
          {items.map((edition) => {
            const production = edition.isEbook ? null : productionLabel(edition.production);
            const duration = edition.isEbook ? null : formatDuration(edition.durationSeconds);
            const abridgement = edition.isEbook ? null : abridgementLabel(edition.abridged);

            return (
              <TableRow key={edition.editionId}>
                <TableRowCell className={styles.title}>
                  {edition.title || <UnknownValue />}
                </TableRowCell>
                <TableRowCell className={styles.narrators}>
                  {edition.narrators && edition.narrators.length ?
                    edition.narrators.join(', ') :
                    <UnknownValue />}
                </TableRowCell>
                <TableRowCell className={styles.production}>
                  {production || <UnknownValue notApplicable={edition.isEbook} />}
                </TableRowCell>
                <TableRowCell className={styles.duration}>
                  {duration || <UnknownValue notApplicable={edition.isEbook} />}
                </TableRowCell>
                <TableRowCell className={styles.abridgement}>
                  {abridgement || <UnknownValue notApplicable={edition.isEbook} />}
                </TableRowCell>
                <TableRowCell className={styles.language}>
                  {edition.language || <UnknownValue />}
                </TableRowCell>
                <TableRowCell className={styles.publisher}>
                  {edition.publisher || <UnknownValue />}
                </TableRowCell>
                <TableRowCell className={styles.status}>
                  {edition.monitored && <Label kind="primary">{translate('EditionSelected')}</Label>}
                  {edition.owned && <Label kind="success">{translate('EditionOwned')}</Label>}
                </TableRowCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

BookEditionCatalog.propTypes = {
  bookId: PropTypes.number.isRequired,
  mediaType: PropTypes.oneOf(['audiobook', 'ebook'])
};

export default BookEditionCatalog;
