import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Measure from 'Components/Measure';
import { isMobile as isMobileUtil } from 'Utilities/browser';
import styles from './SwipeHeader.css';

function cursorPoint(event) {
  if (event.touches && event.touches.length) {
    return {
      x: event.touches[0].clientX,
      y: event.touches[0].clientY
    };
  }

  return {
    x: event.clientX,
    y: event.clientY
  };
}

class SwipeHeader extends Component {

  //
  // Lifecycle

  constructor(props) {
    super(props);

    this._isMobile = isMobileUtil();
    this._isMounted = false;
    this._resizeTimeout = null;
    this._gestureAxis = null;

    this.state = {
      containerWidth: 0,
      touching: null,
      translate: 0,
      stage: 'init',
      url: null
    };
  }

  componentDidMount() {
    // Mark as mounted
    this._isMounted = true;
    
    // Add resize listener to recalculate container width
    this.handleResize = () => {
      if (this._resizeTimeout) {
        clearTimeout(this._resizeTimeout);
      }
      this._resizeTimeout = setTimeout(() => {
        // Check if component is still mounted
        if (this._isMounted) {
          // Trigger measure to get the new width
          if (this._measureRef && this._measureRef.measure) {
            this._measureRef.measure();
          }
        }
      }, 100);
    };
    window.addEventListener('resize', this.handleResize);
  }

  componentWillUnmount() {
    // Mark as unmounted
    this._isMounted = false;
    
    // Clean up all event listeners
    this.removeEventListeners();
    
    // Clean up resize listener
    if (this.handleResize) {
      window.removeEventListener('resize', this.handleResize);
    }
    
    // Clear any timeouts
    if (this._resizeTimeout) {
      clearTimeout(this._resizeTimeout);
    }
  }

  //
  // Listeners

  onMouseDown = (e) => {
    if (!this.props.isSmallScreen || !this._isMobile || this.state.touching || this.state.containerWidth <= 0) {
      return;
    }

    const { x, y } = cursorPoint(e);

    this.startTouchPositionX = x;
    this.startTouchPositionY = y;
    this.initTranslate = this.state.translate;
    this._gestureAxis = null;

    this.setState({
      stage: null,
      touching: true }, () => {
      this.addEventListeners();
    });
  };

  addEventListeners = () => {
    window.addEventListener('mousemove', this.onMouseMove);
    window.addEventListener('touchmove', this.onMouseMove);
    window.addEventListener('mouseup', this.onMouseUp);
    window.addEventListener('touchend', this.onMouseUp);
  };

  removeEventListeners = () => {
    window.removeEventListener('mousemove', this.onMouseMove);
    window.removeEventListener('touchmove', this.onMouseMove);
    window.removeEventListener('mouseup', this.onMouseUp);
    window.removeEventListener('touchend', this.onMouseUp);
  };

  onMouseMove = (e) => {
    const {
      touching,
      containerWidth
    } = this.state;

    if (!touching) {
      return;
    }

    const { x, y } = cursorPoint(e);
    const dx = x - this.startTouchPositionX;
    const dy = y - this.startTouchPositionY;

    // Direction lock: if the user is scrolling vertically, don't treat it as a swipe.
    if (!this._gestureAxis) {
      const lockDistance = 10;
      const axisRatio = 1.5;

      if (Math.abs(dx) < lockDistance && Math.abs(dy) < lockDistance) {
        return;
      }

      if (Math.abs(dx) > Math.abs(dy) * axisRatio) {
        this._gestureAxis = 'horizontal';
      } else if (Math.abs(dy) > Math.abs(dx) * axisRatio) {
        this._gestureAxis = 'vertical';

        // Cancel swipe handling and let the browser scroll normally.
        this.setState({ touching: false, translate: 0, stage: null });
        this.removeEventListeners();
        return;
      } else {
        // Ambiguous gesture: don't lock to horizontal yet (prevents accidental navigation on scroll).
        return;
      }
    }

    if (this._gestureAxis !== 'horizontal') {
      return;
    }

    const translate = Math.max(Math.min(dx + this.initTranslate, containerWidth), -1 * containerWidth);

    this.setState({ translate });
  };

  onMouseUp = () => {
    if (!this._isMounted) {
      this.removeEventListeners();
      return;
    }
    
    this.startTouchPositionX = null;
    this.startTouchPositionY = null;
    this._gestureAxis = null;

    const {
      nextLink,
      prevLink,
      navWidth
    } = this.props;

    const {
      containerWidth,
      translate
    } = this.state;

    // If we don't have a measured width yet, never attempt navigation.
    if (containerWidth <= 0) {
      this.setState({ touching: false, translate: 0, stage: null });
      this.removeEventListeners();
      return;
    }

    const newState = {
      touching: false
    };

    const acceptableMove = navWidth * 0.7;
    const showNav = Math.abs(translate) >= acceptableMove;
    const navWithoutConfirm = Math.abs(translate) >= containerWidth * 0.5;

    if (navWithoutConfirm) {
      newState.translate = Math.sign(translate) * containerWidth;
    }

    if (!showNav) {
      newState.translate = 0;
      newState.stage = null;
    }

    if (showNav && !navWithoutConfirm) {
      newState.translate = Math.sign(translate) * navWidth;
      newState.stage = 'showNav';
    }

    this.setState(newState, () => {
      if (navWithoutConfirm && this._isMounted) {
        this.onNavClick(translate < 0 ? nextLink : prevLink, Math.abs(translate) === containerWidth);
      }
    });

    this.removeEventListeners();
  };

  onNavClick = (url, callTransition) => {
    if (!this._isMounted) {
      return;
    }
    
    const {
      containerWidth,
      translate
    } = this.state;

    this.setState({
      stage: 'navigating',
      translate: Math.sign(translate) * containerWidth,
      url
    }, () => {
      if (callTransition && this._isMounted) {
        this.onTransitionEnd();
      }
    });
  };

  onTransitionEnd = (e) => {
    if (!this._isMounted) {
      return;
    }
    
    const {
      stage,
      url
    } = this.state;

    if (stage === 'navigating') {
      this.setState({
        stage: 'navigated',
        translate: 0,
        url: null
      }, () => {
        if (this._isMounted) {
          this.props.onGoTo(url);
          if (this._isMounted) {
            this.setState({ stage: null });
          }
        }
      });
    }
  };

  onNext = () => {
    this.onNavClick(this.props.nextLink);
  };

  onPrev = () => {
    this.onNavClick(this.props.prevLink);
  };

  onContainerMeasure = ({ width }) => {
    // Only update if width actually changed to avoid unnecessary re-renders
    if (width !== this.state.containerWidth) {
      this.setState({ containerWidth: width });
    }
  };

  //
  // Render

  render() {
    const {
      transitionDuration,
      className,
      children,
      prevComponent,
      currentComponent,
      nextComponent,
      isSmallScreen
    } = this.props;

    const {
      containerWidth,
      translate,
      touching,
      stage
    } = this.state;

    const allowSwipe = isSmallScreen && this._isMobile;

    const useTransition = !touching && stage !== 'navigated' && stage !== 'init';

    const style = {
      width: '100%',
      '--translate': 0
    };

    if (allowSwipe) {
      style.width = '300%';
      style['--translate'] = `${translate - containerWidth}px`;
      style['--transition'] = useTransition ? `transform ${transitionDuration}ms ease-out` : undefined;
    }

    return (
      <Measure
        ref={(ref) => { this._measureRef = ref; }}
        className={className}
        onMeasure={this.onContainerMeasure}
      >
        {children}

        <div
          className={styles.content}
          style={style}
          onMouseDown={this.onMouseDown}
          onTouchStart={this.onMouseDown}
          onTransitionEnd={this.onTransitionEnd}
        >
          {allowSwipe && containerWidth > 0 ? prevComponent(containerWidth) : null}
          {containerWidth > 0 ? currentComponent(containerWidth) : currentComponent(undefined)}
          {allowSwipe && containerWidth > 0 ? nextComponent(containerWidth) : null}
        </div>
      </Measure>
    );
  }
}

SwipeHeader.propTypes = {
  transitionDuration: PropTypes.number.isRequired,
  navWidth: PropTypes.number.isRequired,
  nextLink: PropTypes.string,
  prevLink: PropTypes.string,
  nextComponent: PropTypes.func.isRequired,
  currentComponent: PropTypes.func.isRequired,
  prevComponent: PropTypes.func.isRequired,
  isSmallScreen: PropTypes.bool.isRequired,
  className: PropTypes.string,
  onGoTo: PropTypes.func.isRequired,
  children: PropTypes.node.isRequired
};

SwipeHeader.defaultProps = {
  transitionDuration: 250,
  navWidth: 75
};

export default SwipeHeader;
