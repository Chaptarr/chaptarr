import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component, useCallback, useRef } from 'react';
import { useDrag, useDrop } from 'react-dnd';
import { DELAY_PROFILE } from 'Helpers/dragTypes';
import DelayProfile from './DelayProfile';
import styles from './DelayProfileDragSource.css';

class DelayProfileDragSourceView extends Component {

  //
  // Render

  render() {
    const {
      id,
      order,
      isDragging,
      isDraggingUp,
      isDraggingDown,
      isOver,
      connectDragSource,
      connectDropTarget,
      ...otherProps
    } = this.props;

    const isBefore = !isDragging && isDraggingUp && isOver;
    const isAfter = !isDragging && isDraggingDown && isOver;

    // if (isDragging && !isOver) {
    //   return null;
    // }

    return connectDropTarget(
      <div
        className={classNames(
          styles.delayProfileDragSource,
          isBefore && styles.isDraggingUp,
          isAfter && styles.isDraggingDown
        )}
      >
        {
          isBefore &&
            <div
              className={classNames(
                styles.delayProfilePlaceholder,
                styles.delayProfilePlaceholderBefore
              )}
            />
        }

        <DelayProfile
          id={id}
          order={order}
          isDragging={isDragging}
          isOver={isOver}
          {...otherProps}
          connectDragSource={connectDragSource}
        />

        {
          isAfter &&
            <div
              className={classNames(
                styles.delayProfilePlaceholder,
                styles.delayProfilePlaceholderAfter
              )}
            />
        }
      </div>
    );
  }
}

DelayProfileDragSourceView.propTypes = {
  id: PropTypes.number.isRequired,
  order: PropTypes.number.isRequired,
  isDragging: PropTypes.bool,
  isDraggingUp: PropTypes.bool,
  isDraggingDown: PropTypes.bool,
  isOver: PropTypes.bool,
  connectDragSource: PropTypes.func,
  connectDropTarget: PropTypes.func,
  onDelayProfileDragMove: PropTypes.func.isRequired,
  onDelayProfileDragEnd: PropTypes.func.isRequired
};

function DelayProfileDragSource(props) {
  const wrapperRef = useRef(null);
  const [{ isDragging }, dragSource] = useDrag(() => ({
    type: DELAY_PROFILE,
    item: () => props,
    end(item, monitor) {
      props.onDelayProfileDragEnd(item, monitor.didDrop());
    },
    collect: (monitor) => ({
      isDragging: monitor.isDragging()
    })
  }), [props]);

  const [{ isOver }, dropTarget] = useDrop(() => ({
    accept: DELAY_PROFILE,
    hover(item, monitor) {
      if (!wrapperRef.current) {
        return;
      }

      const dragIndex = item.order;
      const hoverIndex = props.order;
      const hoverBoundingRect = wrapperRef.current.getBoundingClientRect();
      const hoverMiddleY = (hoverBoundingRect.bottom - hoverBoundingRect.top) / 2;
      const clientOffset = monitor.getClientOffset();
      const hoverClientY = clientOffset.y - hoverBoundingRect.top;

      if (dragIndex === hoverIndex) {
        return;
      }

      // When moving up, only trigger if drag position is above 50% and
      // when moving down, only trigger if drag position is below 50%.
      // If we're moving down the hoverIndex needs to be increased
      // by one so it's ordered properly. Otherwise the hoverIndex will work.

      if (dragIndex < hoverIndex && hoverClientY > hoverMiddleY) {
        props.onDelayProfileDragMove(dragIndex, hoverIndex + 1);
      } else if (dragIndex > hoverIndex && hoverClientY < hoverMiddleY) {
        props.onDelayProfileDragMove(dragIndex, hoverIndex);
      }
    },
    collect: (monitor) => ({
      isOver: monitor.isOver()
    })
  }), [props]);

  const dropTargetRef = useCallback((node) => {
    wrapperRef.current = node;
    dropTarget(node);
  }, [dropTarget]);
  const connectDropTarget = useCallback((node) => (
    React.cloneElement(node, { ref: dropTargetRef })
  ), [dropTargetRef]);
  const connectDragSource = useCallback((node) => (
    React.cloneElement(node, { ref: dragSource })
  ), [dragSource]);

  return (
    <DelayProfileDragSourceView
      {...props}
      isDragging={isDragging}
      isOver={isOver}
      connectDragSource={connectDragSource}
      connectDropTarget={connectDropTarget}
    />
  );
}

DelayProfileDragSource.propTypes = DelayProfileDragSourceView.propTypes;

export default DelayProfileDragSource;
