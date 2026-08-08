import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './SelectBookRow.css';

function getBookCountKind(monitored, bookFileCount, bookCount) {
  if (bookFileCount === bookCount && bookCount > 0) {
    return kinds.SUCCESS;
  }

  if (!monitored) {
    return kinds.WARNING;
  }

  return kinds.DANGER;
}

class SelectBookRow extends Component {

  //
  // Listeners

  onPress = () => {
    this.props.onBookSelect(this.props.id);
  };

  //
  // Render

  render() {
    const {
      title,
      releaseDate,
      statistics,
      audiobookMonitored,
      ebookMonitored,
      mediaType,
      columns
    } = this.props;

    const {
      bookCount,
      bookFileCount,
      totalBookCount
    } = statistics;

    return (
      <TableRow
        onClick={this.onPress}
        className={styles.bookRow}
      >
        {
          columns.map((column) => {
            const {
              name,
              isVisible
            } = column;

            if (!isVisible) {
              return null;
            }

            if (name === 'mediaType') {
              // Normalize to lowercase for consistent comparison
              const normalizedType = String(mediaType || '').toLowerCase();
              const isAudiobook = normalizedType === 'audiobook';
              const iconName = isAudiobook ? icons.HEADPHONES :  // Headphones icon for audiobooks
                               normalizedType === 'ebook' ? icons.BOOK : 
                               icons.UNKNOWN; // Unknown/undefined
              const iconTitle = isAudiobook ? 'Audiobook' : 
                               normalizedType === 'ebook' ? 'Ebook' : 
                               'Unknown';
              
              return (
                <TableRowCell key={name}>
                  <Icon 
                    name={iconName}
                    title={iconTitle}
                  />
                </TableRowCell>
              );
            }

            if (name === 'title') {
              return (
                <TableRowCell key={name}>
                  {title}
                </TableRowCell>
              );
            }

            if (name === 'releaseDate') {
              return (
                <RelativeDateCellConnector
                  key={name}
                  date={releaseDate}
                  timeForToday={false}
                />
              );
            }

            if (name === 'status') {
              return (
                <TableRowCell
                  key={name}
                >
                  <Label
                    title={translate('TotalBookCountBooksTotalBookFileCountBooksWithFilesInterp', [totalBookCount, bookFileCount])}
                    kind={getBookCountKind(audiobookMonitored || ebookMonitored, bookFileCount, bookCount)}
                    size={sizes.MEDIUM}
                  >
                    {
                      <span>{bookFileCount} / {bookCount}</span>
                    }
                  </Label>
                </TableRowCell>
              );
            }

            return null;
          })
        }
      </TableRow>

    );
  }
}

SelectBookRow.propTypes = {
  id: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  releaseDate: PropTypes.string,
  mediaType: PropTypes.string,
  onBookSelect: PropTypes.func.isRequired,
  statistics: PropTypes.object.isRequired,
  audiobookMonitored: PropTypes.bool,
  ebookMonitored: PropTypes.bool,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired
};

SelectBookRow.defaultProps = {
  releaseDate: '',
  statistics: {
    bookCount: 0,
    bookFileCount: 0,
    totalBookCount: 0
  }
};

export default SelectBookRow;
