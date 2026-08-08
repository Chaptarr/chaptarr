import { connect } from 'react-redux';
import { removeIgnoredItem } from 'Store/Actions/ignoredActions';
import IgnoredDownloadRow from './IgnoredDownloadRow';

function createMapDispatchToProps(dispatch, props) {
  return {
    onRemovePress() {
      dispatch(removeIgnoredItem({ id: props.id }));
    }
  };
}

export default connect(null, createMapDispatchToProps)(IgnoredDownloadRow);
