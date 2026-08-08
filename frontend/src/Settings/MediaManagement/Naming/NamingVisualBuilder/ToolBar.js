import PropTypes from 'prop-types';
import React from 'react';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import { icons, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './ToolBar.css';

function ToolBar({ onClear }) {
  return (
    <div className={styles.toolbar}>
      <div className={styles.toolGroup}>
        <Button
          className={styles.tool}
          size={sizes.SMALL}
          onPress={onClear}
          title={translate('NamingBuilderClearAllTokens')}
        >
          <Icon name={icons.CLEAR} size={14} />
          {translate('Clear')}
        </Button>
      </div>
    </div>
  );
}

ToolBar.propTypes = {
  onClear: PropTypes.func.isRequired
};

export default ToolBar;
