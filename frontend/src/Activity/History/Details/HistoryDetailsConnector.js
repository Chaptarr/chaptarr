import pick from 'lodash/pick';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createUISettingsSelector from 'Store/Selectors/createUISettingsSelector';
import HistoryDetails from './HistoryDetails';

function createMapStateToProps() {
  return createSelector(
    createUISettingsSelector(),
    (uiSettings) => {
      return pick(uiSettings, [
        'shortDateFormat',
        'timeFormat'
      ]);
    }
  );
}

export default connect(createMapStateToProps)(HistoryDetails);
