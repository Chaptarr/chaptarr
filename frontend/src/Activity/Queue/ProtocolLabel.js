import PropTypes from 'prop-types';
import React from 'react';
import Label from 'Components/Label';
import styles from './ProtocolLabel.css';

function ProtocolLabel({ protocol }) {
  const protocolName = protocol === 'usenet' ? 'nzb' : protocol;
  const protocolClassName = styles[protocol] || styles.defaultProtocol;

  return (
    <Label className={protocolClassName}>
      {protocolName}
    </Label>
  );
}

ProtocolLabel.propTypes = {
  protocol: PropTypes.string.isRequired
};

export default ProtocolLabel;
