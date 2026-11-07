import PropTypes from 'prop-types';
import React from 'react';
import translate from 'Utilities/String/translate';

function GraphicAudioIcon({ size = 16, title = translate('GraphicAudioProductionTitle'), className = '', style = {} }) {
  return (
    <img
      src={`${window.Chaptarr.urlBase}/Content/Images/Icons/graphic-audio-logo.svg`}
      alt="GraphicAudio"
      title={title}
      className={className}
      style={{
        width: size,
        height: size,
        ...style
      }}
    />
  );
}

GraphicAudioIcon.propTypes = {
  size: PropTypes.number,
  title: PropTypes.string,
  className: PropTypes.string,
  style: PropTypes.object
};

export default GraphicAudioIcon;
