import PropTypes from 'prop-types';
import React from 'react';
import titleCase from 'Utilities/String/titleCase';
import translate from 'Utilities/String/translate';

function TagDetailsDelayProfile(props) {
  const {
    preferredProtocol,
    enableUsenet,
    enableTorrent,
    usenetDelay,
    torrentDelay
  } = props;

  return (
    <div>
      <div>
        {translate('TagDetailsProtocol', { protocol: titleCase(preferredProtocol) })}
      </div>

      <div>
        {
          enableUsenet ?
            translate('TagDetailsUsenetDelay', { delay: usenetDelay }) :
            translate('TagDetailsUsenetDisabled')
        }
      </div>

      <div>
        {
          enableTorrent ?
            translate('TagDetailsTorrentDelay', { delay: torrentDelay }) :
            translate('TagDetailsTorrentsDisabled')
        }
      </div>
    </div>
  );
}

TagDetailsDelayProfile.propTypes = {
  preferredProtocol: PropTypes.string.isRequired,
  enableUsenet: PropTypes.bool.isRequired,
  enableTorrent: PropTypes.bool.isRequired,
  usenetDelay: PropTypes.number.isRequired,
  torrentDelay: PropTypes.number.isRequired
};

export default TagDetailsDelayProfile;
