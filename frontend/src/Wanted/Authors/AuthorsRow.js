import moment from 'moment';
import PropTypes from 'prop-types';
import React from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import AuthorInitialScanStatus from 'Author/Details/AuthorInitialScanStatus';
import SpinnerButton from 'Components/Link/SpinnerButton';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { sizes } from 'Helpers/Props';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import formatDateTime from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';
import missingStyles from '../Missing/MissingRow.css';

const statusKeys = {
  NotRequested: 'NotRequested',
  Pending: 'Pending',
  InProgress: 'InProgress',
  Retrying: 'Waiting',
  PartialSuccess: 'PartialSuccess',
  Succeeded: 'Succeeded',
  Failed: 'NotAvailable'
};

function AuthorsRow({ initialScan, id, authorName, providerId, createdAt, nextAttemptAt, shortDateFormat, timeFormat, overallStatus, audiobookStatus, ebookStatus, columns, pendingAction, onRetryPress, onCancelPress, onDeletePress }) {
  const isActive = ['Pending', 'InProgress', 'Retrying'].includes(overallStatus);
  const canRetry = overallStatus !== 'Succeeded' && [audiobookStatus, ebookStatus].some((status) => status && status !== 'NotRequested');

  return (
    <TableRow>
      {
        columns.filter((column) => column.isVisible).map(({ name }) => {
          if (name === 'createdAt') {
            return (
              <RelativeDateCellConnector
                key={name}
                date={createdAt}
                timeForToday={true}
              />
            );
          }

          if (name === 'actions') {
            return (
              <TableRowCell key={name}>
                {
                  canRetry &&
                    <SpinnerButton
                      size={sizes.SMALL}
                      aria-label={translate('Retry')}
                      isDisabled={!!pendingAction}
                      isSpinning={pendingAction?.id === id && pendingAction.name === 'retry'}
                      onPress={() => onRetryPress(id)}
                    >
                      {translate('Retry')}
                    </SpinnerButton>
                }
                {' '}
                <SpinnerButton
                  size={sizes.SMALL}
                  aria-label={translate('CancelImport')}
                  isDisabled={!isActive || !!pendingAction}
                  isSpinning={pendingAction?.id === id && pendingAction.name === 'cancel'}
                  onPress={() => onCancelPress(id)}
                >
                  {translate('CancelImport')}
                </SpinnerButton>
                {' '}
                <SpinnerButton
                  size={sizes.SMALL}
                  aria-label={translate('Delete')}
                  isDisabled={!!pendingAction}
                  isSpinning={pendingAction?.id === id && pendingAction.name === 'delete'}
                  onPress={() => onDeletePress(id)}
                >
                  {translate('Delete')}
                </SpinnerButton>
              </TableRowCell>
            );
          }

          if (name === 'overallStatus' && initialScan) {
            return <TableRowCell key={name}><AuthorInitialScanStatus {...initialScan} /></TableRowCell>;
          }
          let value = translate(statusKeys[overallStatus] || 'Unknown');
          if (name === 'authorName') {
            value = authorName || '';
          } else if (name === 'providerId') {
            value = providerId;
          }

          if (name === 'overallStatus' && overallStatus === 'Retrying' && nextAttemptAt && moment(nextAttemptAt, moment.ISO_8601, true).isValid()) {
            return (
              <TableRowCell key={name}>
                <div>{value}</div>
                <div>{translate('NextCheckAfter', { time: formatDateTime(nextAttemptAt, shortDateFormat, timeFormat) })}</div>
              </TableRowCell>
            );
          }

          return <TableRowCell key={name} className={name === 'authorName' ? missingStyles.author : undefined}>{value}</TableRowCell>;
        })
      }
    </TableRow>
  );
}

AuthorsRow.propTypes = {
  initialScan: PropTypes.object,
  id: PropTypes.number.isRequired,
  authorName: PropTypes.string,
  providerId: PropTypes.string.isRequired,
  createdAt: PropTypes.string,
  nextAttemptAt: PropTypes.string,
  shortDateFormat: PropTypes.string.isRequired,
  timeFormat: PropTypes.string.isRequired,
  overallStatus: PropTypes.string.isRequired,
  audiobookStatus: PropTypes.string,
  ebookStatus: PropTypes.string,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  pendingAction: PropTypes.object,
  onRetryPress: PropTypes.func.isRequired,
  onCancelPress: PropTypes.func.isRequired,
  onDeletePress: PropTypes.func.isRequired
};

const createMapStateToProps = () => createSelector(
  createUISettingsSelector(),
  ({ shortDateFormat, timeFormat }) => ({ shortDateFormat, timeFormat })
);

export default connect(createMapStateToProps)(AuthorsRow);
