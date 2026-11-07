import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BidiText from 'Components/BidiText';
import Scroller from 'Components/Scroller/Scroller';
import { scrollDirections } from 'Helpers/Props';
import styles from './ModalBody.css';

class ModalBody extends Component {

  //
  // Render

  render() {
    const {
      innerClassName,
      scrollDirection,
      children,
      ...otherProps
    } = this.props;

    let className = this.props.className;
    const hasScroller = scrollDirection !== scrollDirections.NONE;
    const bidiChildren = typeof children === 'string' || typeof children === 'number' ?
      <BidiText>{children}</BidiText> :
      children;

    if (!className) {
      className = hasScroller ? styles.modalScroller : styles.modalBody;
    }

    return (
      <Scroller
        className={className}
        scrollDirection={scrollDirection}
        scrollTop={0}
        {...otherProps}
      >
        {
          hasScroller ?
            <div className={innerClassName}>
              {bidiChildren}
            </div> :
            bidiChildren
        }
      </Scroller>
    );
  }

}

ModalBody.propTypes = {
  className: PropTypes.string,
  innerClassName: PropTypes.string,
  children: PropTypes.node,
  scrollDirection: PropTypes.oneOf(scrollDirections.all)
};

ModalBody.defaultProps = {
  innerClassName: styles.innerModalBody,
  scrollDirection: scrollDirections.VERTICAL
};

export default ModalBody;
