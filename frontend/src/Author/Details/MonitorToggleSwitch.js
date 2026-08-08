import PropTypes from 'prop-types';
import React, { useRef, useEffect, useState } from 'react';
import { connect } from 'react-redux';
import { toggleHideUnmonitoredMissing } from 'Store/Actions/appActions';
import translate from 'Utilities/String/translate';
import styles from './MonitorToggleSwitch.css';

function MonitorToggleSwitch(props) {
  const {
    hideUnmonitoredMissing,
    onTogglePress,
    resizeKey
  } = props;

  const monitoredRef = useRef(null);
  const allRef = useRef(null);
  const switchRef = useRef(null);
  const [sliderStyle, setSliderStyle] = useState({});

  const updateSliderPosition = () => {
    if (!monitoredRef.current || !allRef.current || !switchRef.current) {
      return;
    }
    
    // Get element widths with fallback values
    const monitoredWidth = monitoredRef.current.offsetWidth || 82; // fallback to default
    const allWidth = allRef.current.offsetWidth || 60; // fallback to default
    
    // Ensure we have valid widths
    if (monitoredWidth === 0 || allWidth === 0) {
      // Retry after a short delay if elements aren't ready
      setTimeout(updateSliderPosition, 50);
      return;
    }
    
    if (hideUnmonitoredMissing) {
      // "Monitored" is active - slider should wrap it
      setSliderStyle({
        width: `${monitoredWidth + 11}px`,
        transform: 'translateX(0)'
      });
    } else {
      // "All" is active - mirror the perfect "Monitored" positioning but right-aligned
      // Position it flush to the right edge with same padding as "Monitored" has on left
      const containerWidth = monitoredWidth + allWidth + 32; // Total container width
      setSliderStyle({
        width: `${allWidth + 23}px`,
        transform: `translateX(${containerWidth - allWidth - 29}px)` // Shift left to grow left
      });
    }

    // Set CSS custom properties for the container to size properly
    switchRef.current.style.setProperty('--monitored-width', `${monitoredWidth}px`);
    switchRef.current.style.setProperty('--all-width', `${allWidth}px`);
  };

  useEffect(() => {
    // Initial calculation with delay to ensure elements are rendered
    const timer = setTimeout(updateSliderPosition, 0);
    
    // Debounced resize handler to prevent excessive recalculations
    let resizeTimeout;
    const handleResize = () => {
      clearTimeout(resizeTimeout);
      resizeTimeout = setTimeout(updateSliderPosition, 100);
    };
    
    window.addEventListener('resize', handleResize);
    
    // ResizeObserver to watch container changes
    let resizeObserver;
    if (typeof ResizeObserver !== 'undefined' && switchRef.current) {
      resizeObserver = new ResizeObserver(() => {
        updateSliderPosition();
      });
      // Watch the parent element that might change size
      if (switchRef.current.parentElement) {
        resizeObserver.observe(switchRef.current.parentElement);
      }
    }
    
    // Cleanup
    return () => {
      clearTimeout(timer);
      clearTimeout(resizeTimeout);
      window.removeEventListener('resize', handleResize);
      if (resizeObserver) {
        resizeObserver.disconnect();
      }
    };
  }, [hideUnmonitoredMissing, resizeKey]);

  return (
    <div className={styles.toggleContainer}>
      <div 
        ref={switchRef}
        className={styles.toggleSwitch}
        onClick={onTogglePress}
      >
        <div
          ref={monitoredRef}
          className={`${styles.toggleOption} ${hideUnmonitoredMissing ? styles.active : ''}`}
        >
          {translate('Monitored')}
        </div>
        <div
          ref={allRef}
          className={`${styles.toggleOption} ${!hideUnmonitoredMissing ? styles.active : ''}`}
        >
          {translate('All')}
        </div>
        <div 
          className={styles.toggleSlider} 
          style={sliderStyle}
        />
      </div>
    </div>
  );
}

MonitorToggleSwitch.propTypes = {
  hideUnmonitoredMissing: PropTypes.bool.isRequired,
  onTogglePress: PropTypes.func.isRequired,
  resizeKey: PropTypes.oneOfType([PropTypes.string, PropTypes.number])
};

function createMapStateToProps() {
  return (state) => {
    return {
      hideUnmonitoredMissing: state.app.hideUnmonitoredMissing
    };
  };
}

const mapDispatchToProps = {
  onTogglePress: toggleHideUnmonitoredMissing
};

export default connect(createMapStateToProps, mapDispatchToProps)(MonitorToggleSwitch);