import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function BookshelfFormatGroupingHelp(props) {
  const { className, selectedMediaType } = props;

  if (selectedMediaType !== 'both') {
    return null;
  }

  return (
    <Popover
      className={className}
      anchor={
        <Icon
          name={icons.INFO}
          kind={kinds.INFO}
          size={14}
        />
      }
      title={translate('BookshelfFormatGroupingHelpTitle')}
      body={translate('BookshelfFormatGroupingHelpText')}
      position={tooltipPositions.BOTTOM}
      isAnchorFocusable={true}
    />
  );
}

BookshelfFormatGroupingHelp.propTypes = {
  className: PropTypes.string,
  selectedMediaType: PropTypes.oneOf(['audiobook', 'ebook', 'both']).isRequired
};

export default BookshelfFormatGroupingHelp;
