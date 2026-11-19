import PropTypes from 'prop-types';
import React, { useEffect, useLayoutEffect, useState } from 'react';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';

export function deletionSelection(fileIds = [], bookIds = []) {
  return `${bookIds.length ? 'books' : 'files'}:${[...new Set(bookIds.length ? bookIds : fileIds)].sort((a, b) => a - b).join(',')}`;
}

function BookFileDeletionPreview({ fileIds = [], bookIds = [], onReadyChange }) {
  const selection = deletionSelection(fileIds, bookIds);
  const [preview, setPreview] = useState(null);

  useEffect(() => {
    let active = true;
    const { request, abortRequest } = createAjaxRequest({
      url: '/bookfile/deletepreview',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({ [selection.startsWith('books:') ? 'bookIds' : 'bookFileIds']: selection.split(':')[1].split(',').filter(Boolean).map(Number) })
    });

    request.done((result) => {
      if (active) {
        setPreview({ selection, ...result });
      }
    }).fail((xhr) => {
      if (active && !xhr.aborted) {
        setPreview({ selection, error: true });
      }
    });

    return () => {
      active = false;
      abortRequest();
    };
  }, [selection]);

  const current = preview?.selection === selection ? preview : null;
  const ready = Array.isArray(current?.files) && !current.error;

  useLayoutEffect(() => {
    onReadyChange(ready ? selection : null);
    return () => onReadyChange(null);
  }, [ready, selection, onReadyChange]);
  const paths = ready ? [...new Set(current.files.flatMap((item) => [item.path, ...(item.associatedPaths || [])]))] : [];

  const retentionReasons = new Map((current?.files || []).map((file) => [file.path, file.retentionReason]));

  let disposalMessage = 'ConfirmImportRecyclingBin';
  if (current?.files?.some((item) => item.isPermanentDelete)) {
    disposalMessage = 'ConfirmImportPermanentDeletion';
  }
  if (current?.hasCalibreFiles) {
    disposalMessage = current.files.some((item) => item.isPermanentDelete) ?
      'DeleteBookFilesCalibrePermanentWarning' : 'DeleteBookFilesCalibreRecyclingWarning';
  }

  return (
    <>
      {!current && <LoadingIndicator />}
      {current?.error && <Alert kind={kinds.DANGER}>{translate('UnableToLoadDeletionFiles')}</Alert>}
      {ready &&
        <>
          <Alert kind={current.hasCalibreFiles || current.files.some((item) => item.isPermanentDelete) ? kinds.WARNING : kinds.INFO}>
            {translate(disposalMessage)}
          </Alert>
          <ul>{paths.map((path) => (
            <li key={path}>
              {path}
              {retentionReasons.get(path) &&
                <div>{translate('AuthorDeleteKeptFile', { reason: retentionReasons.get(path) })}</div>
              }
            </li>
          ))}</ul>
        </>
      }
    </>
  );
}

BookFileDeletionPreview.propTypes = {
  fileIds: PropTypes.arrayOf(PropTypes.number),
  bookIds: PropTypes.arrayOf(PropTypes.number),
  onReadyChange: PropTypes.func.isRequired
};

export default BookFileDeletionPreview;
