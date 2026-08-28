import PropTypes from 'prop-types';
import React, { Component } from 'react';
import MonitorBooksSelectInput from 'Components/Form/MonitorBooksSelectInput';
import Icon from 'Components/Icon';
import SpinnerButton from 'Components/Link/SpinnerButton';
import PageContentFooter from 'Components/Page/PageContentFooter';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './BookshelfFooter.css';

const NO_CHANGE = 'noChange';

class BookshelfFooter extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      monitor: NO_CHANGE
    };
  }

  componentDidUpdate(prevProps) {
    const {
      isSaving,
      saveError
    } = this.props;

    if (prevProps.isSaving && !isSaving && !saveError) {
      this.setState({
        monitor: NO_CHANGE
      });
    }
  }

  //
  // Listeners

  onInputChange = ({ name, value }) => {
    this.setState({ [name]: value });
  };

  onUpdateSelectedPress = () => {
    this.props.onUpdateSelectedPress({ monitor: this.state.monitor });
  };

  //
  // Render

  render() {
    const {
      selectedCount,
      isSaving
    } = this.props;

    const { monitor } = this.state;

    return (
      <PageContentFooter>
        <div className={styles.inputContainer}>
          <div className={styles.label}>
            {translate('BookMonitoring')}
            <Popover
              anchor={<Icon className={styles.labelIcon} name={icons.INFO} />}
              title={translate('BookMonitoring')}
              body={<div>{translate('BookshelfBookMonitoringHelpText')}</div>}
              position={tooltipPositions.RIGHT}
            />
          </div>

          <MonitorBooksSelectInput
            name="monitor"
            value={monitor}
            includeNoChange={true}
            isDisabled={!selectedCount}
            onChange={this.onInputChange}
          />
        </div>

        <div>
          <div className={styles.label}>
            {translate('CountAuthorsSelected', { selectedCount })}
          </div>

          <SpinnerButton
            className={styles.updateSelectedButton}
            kind={kinds.PRIMARY}
            isSpinning={isSaving}
            isDisabled={!selectedCount || monitor === NO_CHANGE}
            onPress={this.onUpdateSelectedPress}
          >
            {translate('UpdateSelected')}
          </SpinnerButton>
        </div>
      </PageContentFooter>
    );
  }
}

BookshelfFooter.propTypes = {
  selectedCount: PropTypes.number.isRequired,
  isSaving: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  onUpdateSelectedPress: PropTypes.func.isRequired
};

export default BookshelfFooter;
