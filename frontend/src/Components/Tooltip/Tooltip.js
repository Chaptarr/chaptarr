import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { Manager, Popper, Reference } from 'react-popper';
import Portal from 'Components/Portal';
import { kinds, tooltipPositions } from 'Helpers/Props';
import dimensions from 'Styles/Variables/dimensions';
import { isMobile as isMobileUtil } from 'Utilities/browser';
import styles from './Tooltip.css';

function getMaxWidth(windowWidth) {
  if (windowWidth >= parseInt(dimensions.breakpointLarge)) {
    return 800;
  } else if (windowWidth >= parseInt(dimensions.breakpointMedium)) {
    return 650;
  } else if (windowWidth >= parseInt(dimensions.breakpointSmall)) {
    return 500;
  }

  return 450;
}

const popoverFlipBehavior = {
  [tooltipPositions.TOP]: [
    tooltipPositions.TOP,
    tooltipPositions.BOTTOM,
    tooltipPositions.RIGHT,
    tooltipPositions.LEFT
  ],
  [tooltipPositions.RIGHT]: [
    tooltipPositions.RIGHT,
    tooltipPositions.LEFT,
    tooltipPositions.BOTTOM,
    tooltipPositions.TOP
  ],
  [tooltipPositions.BOTTOM]: [
    tooltipPositions.BOTTOM,
    tooltipPositions.TOP,
    tooltipPositions.RIGHT,
    tooltipPositions.LEFT
  ],
  [tooltipPositions.LEFT]: [
    tooltipPositions.LEFT,
    tooltipPositions.RIGHT,
    tooltipPositions.BOTTOM,
    tooltipPositions.TOP
  ]
};

let nextTooltipId = 0;

class Tooltip extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._scheduleUpdate = null;
    this._openTimeout = null;
    this._closeTimeout = null;
    this._referenceElement = null;
    this._contentId = `tooltip-${++nextTooltipId}`;

    this.state = {
      isOpen: false
    };
  }

  componentDidUpdate(prevProps, prevState) {
    if (this._scheduleUpdate && this.state.isOpen) {
      this._scheduleUpdate();
    }

    const wasOpen = prevState.isOpen;
    const isOpen = this.state.isOpen;

    if (!wasOpen && isOpen) {
      document.addEventListener('pointerdown', this.onDocumentPointerDown);
      window.addEventListener('keydown', this.onWindowKeyDown);
    } else if (wasOpen && !isOpen) {
      this.removeDismissListeners();
    }
  }

  componentWillUnmount() {
    if (this._openTimeout) {
      this._openTimeout = clearTimeout(this._openTimeout);
    }

    if (this._closeTimeout) {
      this._closeTimeout = clearTimeout(this._closeTimeout);
    }

    this.removeDismissListeners();
  }

  //
  // Control

  isClickTrigger = (props = this.props) => {
    return props.trigger === 'click' || isMobileUtil();
  };

  isClickInteractionEnabled = (props = this.props) => {
    return this.isClickTrigger(props) || props.contentRole === 'dialog';
  };

  computeMaxSize = (data) => {
    const windowWidth = window.innerWidth;
    const windowHeight = window.innerHeight;
    const viewportPadding = 20;
    const popperMargin = 10;
    const reservedSpace = (viewportPadding + popperMargin) * 2;

    // Keep the panel's dimensions stable while Popper evaluates placements.
    // Placement-dependent dimensions can cause it to alternate between sides.
    data.styles.maxWidth = Math.max(
      0,
      Math.min(getMaxWidth(windowWidth), windowWidth - reservedSpace)
    );
    data.styles.maxHeight = Math.max(
      0,
      Math.min(300, windowHeight - reservedSpace)
    );

    return data;
  };

  //
  // Listeners

  onMeasure = ({ width }) => {
    this.setState({ width });
  };

  removeDismissListeners = () => {
    document.removeEventListener('pointerdown', this.onDocumentPointerDown);
    window.removeEventListener('keydown', this.onWindowKeyDown);
  };

  onClick = (event) => {
    if (this.isClickInteractionEnabled()) {
      event.stopPropagation();
      this._referenceElement = event.currentTarget;
      this.setState({ isOpen: !this.state.isOpen });
    }
  };

  onAnchorKeyDown = (event) => {
    if (!this.isClickInteractionEnabled() || event.target !== event.currentTarget) {
      return;
    }

    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      event.stopPropagation();
      this._referenceElement = event.currentTarget;
      this.setState({ isOpen: !this.state.isOpen });
    }
  };

  onDocumentPointerDown = (event) => {
    if (this._referenceElement?.contains(event.target)) {
      return;
    }

    this.setState({ isOpen: false });
  };

  onTooltipPointerDown = (event) => {
    event.stopPropagation();
  };

  onWindowKeyDown = (event) => {
    if (event.key === 'Escape') {
      this.setState({ isOpen: false });
    }
  };

  onMouseEnter = () => {
    if (this.isClickTrigger()) {
      return;
    }

    if (this._closeTimeout) {
      this._closeTimeout = clearTimeout(this._closeTimeout);
    }

    if (!this.state.isOpen && !this._openTimeout) {
      this._openTimeout = setTimeout(() => {
        this._openTimeout = null;
        this.setState({ isOpen: true });
      }, 500);
    }
  };

  onMouseLeave = () => {
    if (this.isClickTrigger()) {
      return;
    }

    if (this._openTimeout) {
      this._openTimeout = clearTimeout(this._openTimeout);
    }

    this._closeTimeout = setTimeout(() => {
      this.setState({ isOpen: false });
    }, 100);
  };

  onFocus = () => {
    if (this.isClickTrigger() || this.props.contentRole === 'dialog') {
      return;
    }

    if (this._openTimeout) {
      this._openTimeout = clearTimeout(this._openTimeout);
    }

    if (this._closeTimeout) {
      this._closeTimeout = clearTimeout(this._closeTimeout);
    }

    this.setState({ isOpen: true });
  };

  onBlur = () => {
    if (this.isClickTrigger() || this.props.contentRole === 'dialog') {
      return;
    }

    this._closeTimeout = setTimeout(() => {
      this.setState({ isOpen: false });
    }, 100);
  };

  //
  // Render

  render() {
    const {
      className,
      bodyClassName,
      anchor,
      tooltip,
      kind,
      position,
      canFlip,
      accessibleLabel,
      isAnchorFocusable,
      contentRole
    } = this.props;
    const isClickTrigger = this.isClickTrigger();
    const isDialog = contentRole === 'dialog';

    return (
      <Manager>
        <Reference>
          {({ ref }) => (
            <span
              ref={ref}
              className={className}
              onClick={this.onClick}
              onKeyDown={this.onAnchorKeyDown}
              onMouseEnter={this.onMouseEnter}
              onMouseLeave={this.onMouseLeave}
              onFocus={this.onFocus}
              onBlur={this.onBlur}
              role={(isDialog || isClickTrigger) && isAnchorFocusable ? 'button' : undefined}
              tabIndex={isAnchorFocusable ? 0 : undefined}
              aria-label={isAnchorFocusable ? accessibleLabel : undefined}
              aria-haspopup={isDialog && isAnchorFocusable ? 'dialog' : undefined}
              aria-expanded={isDialog && isAnchorFocusable ? this.state.isOpen : undefined}
              aria-controls={isDialog && this.state.isOpen ? this._contentId : undefined}
              aria-describedby={!isDialog && this.state.isOpen ? this._contentId : undefined}
            >
              {anchor}
            </span>
          )}
        </Reference>

        <Portal>
          <Popper
            placement={position}
            // Disable events to improve performance when many tooltips
            // are shown (Quality Definitions for example). Open tooltips and
            // popovers still need to follow viewport changes.
            eventsEnabled={this.state.isOpen}
            modifiers={{
              computeMaxHeight: {
                order: 851,
                enabled: true,
                fn: this.computeMaxSize
              },
              preventOverflow: {
                // Fixes positioning for tooltips in the queue
                // and likely others.
                escapeWithReference: false
              },
              flip: {
                enabled: canFlip,
                behavior: canFlip ? popoverFlipBehavior[position] : 'flip'
              }
            }}
          >
            {({ ref, style, placement, arrowProps, scheduleUpdate }) => {
              this._scheduleUpdate = scheduleUpdate;

              const popperPlacement = placement ? placement.split('-')[0] : position;
              const vertical = popperPlacement === 'top' || popperPlacement === 'bottom';

              return (
                <div
                  ref={ref}
                  id={this._contentId}
                  role={contentRole}
                  aria-label={isDialog ? accessibleLabel : undefined}
                  className={classNames(
                    styles.tooltipContainer,
                    vertical ? styles.verticalContainer : styles.horizontalContainer
                  )}
                  style={style}
                  onPointerDown={this.onTooltipPointerDown}
                  onMouseEnter={this.onMouseEnter}
                  onMouseLeave={this.onMouseLeave}
                  onFocus={this.onFocus}
                  onBlur={this.onBlur}
                >
                  <div
                    className={this.state.isOpen ? classNames(
                      styles.arrow,
                      styles[kind],
                      styles[popperPlacement]
                    ) : styles.arrowDisabled}
                    ref={arrowProps.ref}
                    style={arrowProps.style}
                  />
                  {
                    this.state.isOpen ?
                      <div
                        className={classNames(
                          styles.tooltip,
                          styles[kind]
                        )}
                      >
                        <div
                          className={bodyClassName}
                        >
                          {tooltip}
                        </div>
                      </div> :
                      null
                  }
                </div>
              );
            }}
          </Popper>
        </Portal>
      </Manager>
    );
  }
}

Tooltip.propTypes = {
  className: PropTypes.string,
  bodyClassName: PropTypes.string.isRequired,
  anchor: PropTypes.node.isRequired,
  tooltip: PropTypes.oneOfType([PropTypes.string, PropTypes.node]).isRequired,
  kind: PropTypes.oneOf([kinds.DEFAULT, kinds.INVERSE]),
  position: PropTypes.oneOf(tooltipPositions.all),
  canFlip: PropTypes.bool.isRequired,
  trigger: PropTypes.oneOf(['hover', 'click']).isRequired,
  accessibleLabel: PropTypes.string,
  isAnchorFocusable: PropTypes.bool.isRequired,
  contentRole: PropTypes.oneOf(['dialog', 'tooltip']).isRequired
};

Tooltip.defaultProps = {
  bodyClassName: styles.body,
  kind: kinds.DEFAULT,
  position: tooltipPositions.TOP,
  canFlip: false,
  trigger: 'hover',
  isAnchorFocusable: true,
  contentRole: 'tooltip'
};

export default Tooltip;
