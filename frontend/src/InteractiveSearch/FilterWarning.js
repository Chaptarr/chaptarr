import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './FilterWarning.css';

function FilterWarning(props) {
  const {
    filterSummary,
    bypassFilters,
    onToggleBypass
  } = props;

  // Only show when there are soft filters but no displayed results
  if (!filterSummary || !filterSummary.hasSoftFilters || filterSummary.displayedCount > 0) {
    return null;
  }

  const {
    softFilteredCount,
    filterWarnings,
    summaryText
  } = filterSummary;

  if (bypassFilters) {
    return (
      <Alert kind={kinds.INFO}>
        <div className={styles.bypassModeAlert}>
          <Icon name={icons.INFO} />
          <div className={styles.alertContent}>
            <div className={styles.alertTitle}>
              {translate('FilterWarningShowingHiddenResults')}
            </div>
            <div className={styles.alertText}>
              {summaryText}
            </div>
          </div>
          <Button
            kind={kinds.PRIMARY}
            size="small"
            onPress={() => onToggleBypass(false)}
          >
            {translate('FilterWarningHideHiddenResults')}
          </Button>
        </div>
      </Alert>
    );
  }

  return (
    <Alert kind={kinds.WARNING}>
      <div className={styles.filterWarningAlert}>
        <Icon name={icons.WARNING} />
        <div className={styles.alertContent}>
          <div className={styles.alertTitle}>
            {translate('FilterWarningNoVisibleResults')}
          </div>
          <div className={styles.alertText}>
            {filterWarnings && filterWarnings.length > 0 ?
              filterWarnings.join('. ') :
              translate('FilterWarningSoftFilteredCount', { count: softFilteredCount })
            }
          </div>
        </div>
        <Button
          kind={kinds.WARNING}
          size="small"
          onPress={() => onToggleBypass(true)}
        >
          {translate('FilterWarningShowHiddenCount', { count: softFilteredCount })}
        </Button>
      </div>
    </Alert>
  );
}

FilterWarning.propTypes = {
  filterSummary: PropTypes.shape({
    hasSoftFilters: PropTypes.bool,
    displayedCount: PropTypes.number,
    softFilteredCount: PropTypes.number,
    filterWarnings: PropTypes.arrayOf(PropTypes.string),
    summaryText: PropTypes.string
  }),
  bypassFilters: PropTypes.bool.isRequired,
  onToggleBypass: PropTypes.func.isRequired
};

export default FilterWarning;
