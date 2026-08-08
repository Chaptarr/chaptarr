import classNames from 'classnames';
import PropTypes from 'prop-types';
import React from 'react';
import { getMediaTypeScopeLabel } from 'Helpers/Props/mediaTypeScopes';
import styles from './MediaTypeScope.css';

function MediaTypeScope({ mediaType, className }) {
  return (
    <div className={classNames(styles.scope, className)}>
      {getMediaTypeScopeLabel(mediaType)}
    </div>
  );
}

MediaTypeScope.propTypes = {
  mediaType: PropTypes.oneOfType([PropTypes.string, PropTypes.number]),
  className: PropTypes.string
};

MediaTypeScope.defaultProps = {
  mediaType: null,
  className: undefined
};

export default MediaTypeScope;
