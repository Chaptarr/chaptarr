import PropTypes from 'prop-types';
import React, { Component } from 'react';
import CheckInput from 'Components/Form/CheckInput';
import Icon from 'Components/Icon';
import { icons, kinds } from 'Helpers/Props';
import styles from './OrganizePreviewRow.css';

class OrganizePreviewRow extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      id,
      canOrganize,
      isSelected,
      onSelectedChange
    } = this.props;

    // A preview refetch temporarily unmounts the rows. Only default genuinely
    // new rows to selected; preserve an explicit selection when they return.
    if (canOrganize && isSelected == null) {
      onSelectedChange({ id, value: true });
    }
  }

  //
  // Listeners

  onSelectedChange = ({ value, shiftKey }) => {
    const {
      id,
      onSelectedChange
    } = this.props;

    onSelectedChange({ id, value, shiftKey });
  };

  //
  // Render

  render() {
    const {
      id,
      existingPath,
      newPath,
      canOrganize,
      reason,
      isSelected
    } = this.props;

    return (
      <div className={styles.row}>
        <CheckInput
          containerClassName={styles.selectedContainer}
          name={id.toString()}
          value={isSelected}
          isDisabled={!canOrganize}
          onChange={this.onSelectedChange}
        />

        <div>
          <div>
            <Icon
              name={icons.SUBTRACT}
              kind={kinds.DANGER}
            />

            <span className={styles.path}>
              {existingPath}
            </span>
          </div>

          {
            canOrganize &&
              <div>
                <Icon
                  name={icons.ADD}
                  kind={kinds.SUCCESS}
                />

                <span className={styles.path}>
                  {newPath}
                </span>
              </div>
          }

          {
            !canOrganize &&
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

OrganizePreviewRow.propTypes = {
  id: PropTypes.number.isRequired,
  existingPath: PropTypes.string.isRequired,
  newPath: PropTypes.string.isRequired,
  canOrganize: PropTypes.bool.isRequired,
  reason: PropTypes.string,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired
};

export default OrganizePreviewRow;
