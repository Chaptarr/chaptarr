import find from 'lodash/find';
import map from 'lodash/map';
import orderBy from 'lodash/orderBy';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormInputGroup from 'Components/Form/FormInputGroup';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableRow from 'Components/Table/TableRow';
import { inputTypes } from 'Helpers/Props';
import formatDurationMinutes from 'Utilities/Date/formatDurationMinutes';
import titleCase from 'Utilities/String/titleCase';
import translate from 'Utilities/String/translate';

function getNarratorDisplay(bookEdition) {
  const narratorNames = bookEdition.narratorNames || [];

  if (narratorNames.length > 1) {
    return `${narratorNames[0]}, +${narratorNames.length - 1}`;
  }

  if (narratorNames.length === 1) {
    return narratorNames[0];
  }

  return bookEdition.narrator;
}

function getReleaseYear(releaseDate) {
  if (!releaseDate) {
    return null;
  }

  const year = new Date(releaseDate).getFullYear();

  return Number.isNaN(year) ? null : year.toString();
}

function getAudiobookExtras(bookEdition) {
  const extras = [];
  const narrator = getNarratorDisplay(bookEdition);
  const releaseYear = getReleaseYear(bookEdition.releaseDate);

  if (narrator) {
    extras.push(narrator);
  }

  if (bookEdition.durationSeconds > 0) {
    extras.push(formatDurationMinutes(bookEdition.durationSeconds / 60));
  }

  if (releaseYear) {
    extras.push(releaseYear);
  }

  if (bookEdition.publisher) {
    extras.push(bookEdition.publisher);
  }

  if (!extras.length && bookEdition.format) {
    extras.push(bookEdition.format);
  }

  return extras;
}

function getEbookExtras(bookEdition) {
  const extras = [];

  if (bookEdition.language) {
    extras.push(bookEdition.language);
  }
  if (bookEdition.publisher) {
    extras.push(bookEdition.publisher);
  }
  if (bookEdition.isbn13) {
    extras.push(bookEdition.isbn13);
  }
  if (bookEdition.asin) {
    extras.push(bookEdition.asin);
  }
  if (bookEdition.format) {
    extras.push(bookEdition.format);
  }
  if (bookEdition.pageCount > 0) {
    extras.push(`${bookEdition.pageCount}p`);
  }

  return extras;
}

class SelectEditionRow extends Component {

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    const editionId = parseInt(value);
    const edition = find(this.props.editions, { id: editionId });

    this.props.onEditionSelect(parseInt(name), editionId, edition ? edition.foreignEditionId : undefined);
  };

  //
  // Render

  render() {
    const {
      id,
      matchedEditionId,
      title,
      disambiguation,
      editions,
      columns
    } = this.props;

    const extendedTitle = disambiguation ? `${title} (${disambiguation})` : title;

    const values = map(editions, (bookEdition) => {

      let value = `${bookEdition.title}`;

      if (bookEdition.disambiguation) {
        value = `${value} (${titleCase(bookEdition.disambiguation)})`;
      }

      const extras = bookEdition.isEbook ? getEbookExtras(bookEdition) : getAudiobookExtras(bookEdition);

      if (extras.length) {
        value = `${value} [${extras.join(', ')}]`;
      }

      return {
        key: bookEdition.id.toString(),
        value
      };
    });

    const sortedValues = orderBy(values, ['value']);

    return (
      <TableRow>
        {
          columns.map((column) => {
            const {
              name,
              isVisible
            } = column;

            if (!isVisible) {
              return null;
            }

            if (name === 'book') {
              return (
                <TableRowCell key={name}>
                  {extendedTitle}
                </TableRowCell>
              );
            }

            if (name === 'edition') {
              return (
                <TableRowCell key={name}>
                  {
                    sortedValues.length ?
                      <FormInputGroup
                        type={inputTypes.SELECT}
                        name={id.toString()}
                        values={sortedValues}
                        value={matchedEditionId ? matchedEditionId.toString() : undefined}
                        onChange={this.onInputChange}
                      /> :
                      translate('NoEditionsAvailableForSelectedBook')
                  }
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

SelectEditionRow.propTypes = {
  id: PropTypes.number.isRequired,
  matchedEditionId: PropTypes.number,
  title: PropTypes.string.isRequired,
  disambiguation: PropTypes.string,
  editions: PropTypes.arrayOf(PropTypes.object).isRequired,
  onEditionSelect: PropTypes.func.isRequired,
  columns: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default SelectEditionRow;
