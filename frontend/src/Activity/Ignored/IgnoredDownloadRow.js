import PropTypes from 'prop-types';
import React, { Component } from 'react';
import IconButton from 'Components/Link/IconButton';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './IgnoredDownloadRow.css';

class IgnoredDownloadRow extends Component {

  //
  // Render

  render() {
    const {
      id,
      sourceTitle,
      downloadId,
      downloadClientName,
      date,
      isInClient,
      isSelected,
      columns,
      onSelectedChange,
      onRemovePress
    } = this.props;

    return (
      <TableRow>
        <TableSelectCell
          id={id}
          isSelected={isSelected}
          onSelectedChange={onSelectedChange}
        />

        {
          columns.map((column) => {
            const {
              name,
              isVisible
            } = column;

            if (!isVisible) {
              return null;
            }

            if (name === 'sourceTitle') {
              return (
                <TableRowCell key={name}>
                  {sourceTitle}
                </TableRowCell>
              );
            }

            if (name === 'downloadId') {
              return (
                <TableRowCell key={name}>
                  {downloadId}
                </TableRowCell>
              );
            }

            if (name === 'downloadClientName') {
              return (
                <TableRowCell key={name}>
                  {downloadClientName || translate('Unknown')}
                </TableRowCell>
              );
            }

            if (name === 'date') {
              return (
                <RelativeDateCellConnector
                  key={name}
                  date={date}
                />
              );
            }

            if (name === 'isInClient') {
              return (
                <TableRowCell key={name}>
                  {isInClient ? translate('Yes') : translate('No')}
                </TableRowCell>
              );
            }

            if (name === 'actions') {
              return (
                <TableRowCell
                  key={name}
                  className={styles.actions}
                >
                  <IconButton
                    title={isInClient ? translate('ReturnToQueue') : translate('RemoveFromIgnored')}
                    name={isInClient ? icons.RESTORE : icons.REMOVE}
                    kind={isInClient ? kinds.INFO : kinds.DANGER}
                    onPress={onRemovePress}
                  />
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

IgnoredDownloadRow.propTypes = {
  id: PropTypes.number.isRequired,
  sourceTitle: PropTypes.string.isRequired,
  downloadId: PropTypes.string.isRequired,
  downloadClientName: PropTypes.string,
  date: PropTypes.string.isRequired,
  isInClient: PropTypes.bool.isRequired,
  isSelected: PropTypes.bool.isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired,
  onSelectedChange: PropTypes.func.isRequired,
  onRemovePress: PropTypes.func.isRequired
};

export default IgnoredDownloadRow;
