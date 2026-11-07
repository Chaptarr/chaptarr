import find from 'lodash/find';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Alert from 'Components/Alert';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { kinds } from 'Helpers/Props';
import { fetchBookFiles } from 'Store/Actions/bookFileActions';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import FileDetails from './FileDetails';

function createMapStateToProps() {
  return createSelector(
    (state) => state.bookFiles,
    (bookFiles) => {
      return {
        ...bookFiles
      };
    }
  );
}

const mapDispatchToProps = {
  fetchBookFiles
};

class FileDetailsConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    this.props.fetchBookFiles({ id: this.props.id });
  }

  onRetryPress = () => {
    this.props.fetchBookFiles({ id: this.props.id });
  };

  //
  // Render

  render() {
    const {
      items,
      id,
      isFetching,
      error
    } = this.props;

    const item = find(items, { id });
    const errorMessage = getErrorMessage(error, translate('LoadingBookFilesFailed'));

    if (error && !isFetching) {
      return (
        <Alert kind={kinds.DANGER} role="alert">
          <div>{errorMessage}</div>
          <Button size="small" onPress={this.onRetryPress}>
            {translate('Retry')}
          </Button>
        </Alert>
      );
    } else if (isFetching || !item) {
      return (
        <LoadingIndicator />
      );
    }

    return (
      <FileDetails
        tags={item.tags}
        filename={item.path}
      />
    );

  }
}

FileDetailsConnector.propTypes = {
  fetchBookFiles: PropTypes.func.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  id: PropTypes.number.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object
};

export default connect(createMapStateToProps, mapDispatchToProps)(FileDetailsConnector);
