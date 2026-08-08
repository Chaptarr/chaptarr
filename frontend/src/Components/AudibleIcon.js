import PropTypes from 'prop-types';
import React from 'react';

function AudibleIcon({ size = 16, title = 'Audible Match', className = '', style = {} }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24.073 15.277"
      xmlns="http://www.w3.org/2000/svg"
      title={title}
      className={className}
      style={style}
    >
      <g transform="matrix(1, 0, 0, 1, 0.02307, 0.02307)">
        <path d="M 0.005 5.484 L 0.005 7.564 L 11.999 15.263 L 23.974 7.564 L 23.974 5.484 L 11.999 13.152 L 0.005 5.484 Z" style={{ fill: 'rgb(255, 160, 0)' }} />
        <path d="M 16.712 8.23 L 18.468 7.076 C 17.077 4.981 14.688 3.535 11.999 3.535 C 9.292 3.535 6.919 4.932 5.558 7.092 C 5.669 6.978 5.733 6.912 5.843 6.833 C 9.214 3.957 14.069 4.607 16.712 8.23 Z" style={{ fill: 'rgb(255, 160, 0)' }} />
        <path d="M 8.454 8.961 C 9.072 8.494 9.821 8.244 10.588 8.247 C 11.887 8.247 13.042 8.929 13.816 10.082 L 15.493 9.01 C 14.765 7.823 13.469 7.092 11.997 7.092 C 10.527 7.092 9.228 7.856 8.454 8.961 Z M 3.896 3.974 C 8.833 -0.022 15.811 1.066 19.529 6.379 L 19.56 6.41 L 21.379 5.256 C 19.311 2.002 15.783 0.041 11.999 0.042 C 8.214 0.043 4.686 2.004 2.616 5.256 C 2.98 4.835 3.438 4.331 3.896 3.974 Z" style={{ fill: 'rgb(255, 160, 0)' }} />
      </g>
    </svg>
  );
}

AudibleIcon.propTypes = {
  size: PropTypes.number,
  title: PropTypes.string,
  className: PropTypes.string,
  style: PropTypes.object
};

export default AudibleIcon;
