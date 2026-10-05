import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Icon from 'Components/Icon';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './ConvertPreviewRow.css';

class ConvertPreviewRow extends Component {

  //
  // Render

  render() {
    const {
      path,
      sourceQuality,
      targetQuality,
      canConvert,
      reason
    } = this.props;

    return (
      <div className={styles.row}>
        <div className={styles.details}>
          <div>
            <Icon
              name={icons.FILE}
              kind={canConvert ? kinds.DEFAULT : kinds.DISABLED}
            />

            <span className={styles.path}>
              {path}
            </span>
          </div>

          {
            canConvert &&
              <div className={styles.quality}>
                {translate('ConvertQualityChange', { sourceQuality, targetQuality })}
              </div>
          }

          {
            !canConvert && !!reason &&
              <div className={styles.reason}>
                <Icon
                  name={icons.WARNING}
                  kind={kinds.WARNING}
                />

                <span className={styles.path}>
                  {reason}
                </span>
              </div>
          }
        </div>
      </div>
    );
  }
}

ConvertPreviewRow.propTypes = {
  path: PropTypes.string.isRequired,
  sourceQuality: PropTypes.string,
  targetQuality: PropTypes.string,
  canConvert: PropTypes.bool.isRequired,
  reason: PropTypes.string
};

export default ConvertPreviewRow;
