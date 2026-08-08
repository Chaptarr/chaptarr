import PropTypes from 'prop-types';
import React, { Component } from 'react';
import classNames from 'classnames';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import formatBytes from 'Utilities/Number/formatBytes';
import translate from 'Utilities/String/translate';
import styles from './AuthorFolderPickerRow.css';

class AuthorFolderPickerRow extends Component {

  //
  // Listeners

  onPress = () => {
    const { path, onPress } = this.props;
    onPress(path);
  }

  //
  // Render

  render() {
    const {
      path,
      folderName,
      confidenceScore,
      matchReason,
      isSelected
    } = this.props;

    const confidenceClass = confidenceScore >= 0.98 ? styles.highConfidence :
                           confidenceScore >= 0.95 ? styles.mediumConfidence :
                           styles.lowConfidence;

    return (
      <div
        className={classNames(
          styles.row,
          isSelected && styles.selected
        )}
        onClick={this.onPress}
      >
        <div className={styles.folder}>
          <Icon
            name={icons.FOLDER}
            size={20}
          />
          <div className={styles.folderName}>
            {folderName}
          </div>
        </div>

        <div className={styles.details}>
          <div className={styles.path}>
            {path}
          </div>
          <div className={styles.confidence}>
            <span className={classNames(styles.confidenceScore, confidenceClass)}>
              {translate('AuthorFolderPickerMatchScore', { score: (confidenceScore * 100).toFixed(0) })}
            </span>
            {matchReason && <span className={styles.matchReason}> - {matchReason}</span>}
          </div>
        </div>

        {
          isSelected &&
          <Icon
            className={styles.checkIcon}
            name={icons.CHECK}
            size={18}
          />
        }
      </div>
    );
  }
}

AuthorFolderPickerRow.propTypes = {
  path: PropTypes.string.isRequired,
  folderName: PropTypes.string.isRequired,
  confidenceScore: PropTypes.number.isRequired,
  matchReason: PropTypes.string,
  isSelected: PropTypes.bool.isRequired,
  onPress: PropTypes.func.isRequired
};

export default AuthorFolderPickerRow;