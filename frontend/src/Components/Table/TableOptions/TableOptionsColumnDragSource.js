import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component, useCallback, useRef } from 'react';
import { useDrag, useDrop } from 'react-dnd';
import { TABLE_COLUMN } from 'Helpers/dragTypes';
import TableOptionsColumn from './TableOptionsColumn';
import styles from './TableOptionsColumnDragSource.css';

class TableOptionsColumnDragSourceView extends Component {

  //
  // Render

  render() {
    const {
      name,
      label,
      isVisible,
      isModifiable,
      index,
      isDragging,
      isDraggingUp,
      isDraggingDown,
      isOver,
      connectDragSource,
      connectDropTarget,
      onVisibleChange
    } = this.props;

    const isBefore = !isDragging && isDraggingUp && isOver;
    const isAfter = !isDragging && isDraggingDown && isOver;

    // if (isDragging && !isOver) {
    //   return null;
    // }

    return connectDropTarget(
      <div
        className={classNames(
          styles.columnDragSource,
          isBefore && styles.isDraggingUp,
          isAfter && styles.isDraggingDown
        )}
      >
        {
          isBefore &&
            <div
              className={classNames(
                styles.columnPlaceholder,
                styles.columnPlaceholderBefore
              )}
            />
        }

        <TableOptionsColumn
          name={name}
          label={typeof label === 'function' ? label() : label}
          isVisible={isVisible}
          isModifiable={isModifiable}
          index={index}
          isDragging={isDragging}
          isOver={isOver}
          connectDragSource={connectDragSource}
          onVisibleChange={onVisibleChange}
        />

        {
          isAfter &&
            <div
              className={classNames(
                styles.columnPlaceholder,
                styles.columnPlaceholderAfter
              )}
            />
        }
      </div>
    );
  }
}

TableOptionsColumnDragSourceView.propTypes = {
  name: PropTypes.string.isRequired,
  label: PropTypes.oneOfType([PropTypes.string, PropTypes.func]).isRequired,
  isVisible: PropTypes.bool.isRequired,
  isModifiable: PropTypes.bool.isRequired,
  index: PropTypes.number.isRequired,
  isDragging: PropTypes.bool,
  isDraggingUp: PropTypes.bool,
  isDraggingDown: PropTypes.bool,
  isOver: PropTypes.bool,
  connectDragSource: PropTypes.func,
  connectDropTarget: PropTypes.func,
  onVisibleChange: PropTypes.func.isRequired,
  onColumnDragMove: PropTypes.func.isRequired,
  onColumnDragEnd: PropTypes.func.isRequired
};

function TableOptionsColumnDragSource(props) {
  const wrapperRef = useRef(null);
  const [{ isDragging }, dragSource] = useDrag(() => ({
    type: TABLE_COLUMN,
    item: () => props,
    end(item, monitor) {
      props.onColumnDragEnd(item, monitor.didDrop());
    },
    collect: (monitor) => ({
      isDragging: monitor.isDragging()
    })
  }), [props]);

  const [{ isOver }, dropTarget] = useDrop(() => ({
    accept: TABLE_COLUMN,
    hover(item, monitor) {
      if (!wrapperRef.current) {
        return;
      }

      const dragIndex = item.index;
      const hoverIndex = props.index;
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

      if (dragIndex < hoverIndex && hoverClientY < hoverMiddleY) {
        return;
      }

      if (dragIndex > hoverIndex && hoverClientY > hoverMiddleY) {
        return;
      }

      props.onColumnDragMove(dragIndex, hoverIndex);
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
    <TableOptionsColumnDragSourceView
      {...props}
      isDragging={isDragging}
      isOver={isOver}
      connectDragSource={connectDragSource}
      connectDropTarget={connectDropTarget}
    />
  );
}

TableOptionsColumnDragSource.propTypes = TableOptionsColumnDragSourceView.propTypes;

export default TableOptionsColumnDragSource;
