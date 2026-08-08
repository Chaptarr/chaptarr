import PropTypes from 'prop-types';
import React from 'react';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import translate from 'Utilities/String/translate';
import styles from './BookChaptersTable.css';

const columns = [
  {
    name: 'number',
    label: '#',
    isVisible: true,
    isSortable: false,
    className: styles.number
  },
  {
    name: 'time',
    label: () => translate('Time'),
    isVisible: true,
    isSortable: false,
    className: styles.time
  },
  {
    name: 'duration',
    label: () => translate('Duration'),
    isVisible: true,
    isSortable: false,
    className: styles.duration
  },
  {
    name: 'title',
    label: () => translate('Title'),
    isVisible: true,
    isSortable: false,
    className: styles.title
  }
];

function getStartOffsetMs(chapter) {
  const milliseconds = Number(chapter.startOffsetMs);
  const seconds = Number(chapter.startOffsetSec);

  if (Number.isFinite(milliseconds) && (milliseconds > 0 || !seconds)) {
    return Math.max(0, milliseconds);
  }

  return Number.isFinite(seconds) ? Math.max(0, seconds * 1000) : 0;
}

function getLengthMs(chapter) {
  const length = Number(chapter.lengthMs);
  return Number.isFinite(length) && length > 0 ? length : 0;
}

function formatTime(milliseconds) {
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours) {
    return `${hours}:${String(minutes).padStart(2, '0')}:${String(
      seconds
    ).padStart(2, '0')}`;
  }

  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

function BookChaptersTable({ chapters }) {
  return (
    <div className={styles.container}>
      <Table columns={columns}>
        <TableBody>
          {chapters.map((chapter, index) => {
            const start = getStartOffsetMs(chapter);
            const length = getLengthMs(chapter);
            const title = chapter.title?.trim() ||
              `${translate('Chapter')} ${index + 1}`;

            return (
              <TableRow key={`${start}-${title}-${index}`}>
                <TableRowCell className={styles.number}>
                  {index + 1}
                </TableRowCell>
                <TableRowCell className={styles.time}>
                  {length ?
                    `${formatTime(start)} – ${formatTime(start + length)}` :
                    formatTime(start)}
                </TableRowCell>
                <TableRowCell className={styles.duration}>
                  {length ? formatTime(length) : '—'}
                </TableRowCell>
                <TableRowCell className={styles.title}>
                  {title}
                </TableRowCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </div>
  );
}

BookChaptersTable.propTypes = {
  chapters: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default BookChaptersTable;
