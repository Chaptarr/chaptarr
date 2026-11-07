import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { Component, useCallback, useRef } from 'react';
import { useDrag, useDrop } from 'react-dnd';
import { QUALITY_PROFILE_ITEM } from 'Helpers/dragTypes';
import QualityProfileItem from './QualityProfileItem';
import styles from './QualityProfileItemDragSource.css';

class QualityProfileItemDragSourceView extends Component {

  //
  // Render

  render() {
    const {
      editGroups,
      groupId,
      qualityId,
      name,
      allowed,
      qualityIndex,
      isDragging,
      isDraggingUp,
      isDraggingDown,
      isOverCurrent,
      connectDragSource,
      connectDropTarget,
      onCreateGroupPress,
      onQualityProfileItemAllowedChange
    } = this.props;

    const isBefore = !isDragging && isDraggingUp && isOverCurrent;
    const isAfter = !isDragging && isDraggingDown && isOverCurrent;

    return connectDropTarget(
      <div
        className={classNames(
          styles.qualityProfileItemDragSource,
          isBefore && styles.isDraggingUp,
          isAfter && styles.isDraggingDown
        )}
      >
        {
          isBefore &&
            <div
              className={classNames(
                styles.qualityProfileItemPlaceholder,
                styles.qualityProfileItemPlaceholderBefore
              )}
            />
        }

        <QualityProfileItem
          editGroups={editGroups}
          groupId={groupId}
          qualityId={qualityId}
          name={name}
          allowed={allowed}
          qualityIndex={qualityIndex}
          isDragging={isDragging}
          isOverCurrent={isOverCurrent}
          connectDragSource={connectDragSource}
          onCreateGroupPress={onCreateGroupPress}
          onQualityProfileItemAllowedChange={onQualityProfileItemAllowedChange}
        />

        {
          isAfter &&
            <div
              className={classNames(
                styles.qualityProfileItemPlaceholder,
                styles.qualityProfileItemPlaceholderAfter
              )}
            />
        }
      </div>
    );
  }
}

QualityProfileItemDragSourceView.propTypes = {
  editGroups: PropTypes.bool.isRequired,
  groupId: PropTypes.number,
  qualityId: PropTypes.number,
  name: PropTypes.string.isRequired,
  allowed: PropTypes.bool.isRequired,
  qualityIndex: PropTypes.string.isRequired,
  isDragging: PropTypes.bool,
  isDraggingUp: PropTypes.bool,
  isDraggingDown: PropTypes.bool,
  isOverCurrent: PropTypes.bool,
  isInGroup: PropTypes.bool,
  connectDragSource: PropTypes.func,
  connectDropTarget: PropTypes.func,
  onCreateGroupPress: PropTypes.func,
  onQualityProfileItemAllowedChange: PropTypes.func.isRequired
};

function QualityProfileItemDragSource(props) {
  const wrapperRef = useRef(null);
  const [{ isDragging }, dragSource] = useDrag(() => ({
    type: QUALITY_PROFILE_ITEM,
    item: () => {
      const {
        editGroups,
        qualityIndex,
        groupId,
        qualityId,
        name,
        allowed
      } = props;

      return {
        editGroups,
        qualityIndex,
        groupId,
        qualityId,
        isGroup: !qualityId,
        name,
        allowed
      };
    },
    end(item, monitor) {
      props.onQualityProfileItemDragEnd(monitor.didDrop());
    },
    collect: (monitor) => ({
      isDragging: monitor.isDragging()
    })
  }), [props]);

  const [{ isOver, isOverCurrent }, dropTarget] = useDrop(() => ({
    accept: QUALITY_PROFILE_ITEM,
    hover(item, monitor) {
      // If we're hovering over a child don't trigger on the parent.
      if (!monitor.isOver({ shallow: true }) || !wrapperRef.current) {
        return;
      }

      const {
        qualityIndex: dragQualityIndex,
        isGroup: isDragGroup
      } = item;
      const dropQualityIndex = props.qualityIndex;
      const isDropGroupItem = !!(props.qualityId && props.groupId);

      // Use childNodeIndex to select the correct node to get the middle of so
      // we don't bounce between above and below causing rapid setState calls.
      const childNodeIndex = props.isDraggingUp ? 1 : 0;
      const componentDOMNode = wrapperRef.current.children[childNodeIndex];

      if (!componentDOMNode) {
        return;
      }

      const hoverBoundingRect = componentDOMNode.getBoundingClientRect();
      const hoverMiddleY = (hoverBoundingRect.bottom - hoverBoundingRect.top) / 2;
      const clientOffset = monitor.getClientOffset();
      const hoverClientY = clientOffset.y - hoverBoundingRect.top;

      // Don't show targets for dropping on self.
      if (dragQualityIndex === dropQualityIndex) {
        return;
      }

      // Don't allow a group to be dropped inside a group.
      if (isDragGroup && isDropGroupItem) {
        return;
      }

      let dropPosition = null;

      // Determine drop position based on position over target.
      if (hoverClientY > hoverMiddleY) {
        dropPosition = 'below';
      } else if (hoverClientY < hoverMiddleY) {
        dropPosition = 'above';
      } else {
        return;
      }

      props.onQualityProfileItemDragMove({
        dragQualityIndex,
        dropQualityIndex,
        dropPosition
      });
    },
    collect: (monitor) => ({
      isOver: monitor.isOver(),
      isOverCurrent: monitor.isOver({ shallow: true })
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
    <QualityProfileItemDragSourceView
      {...props}
      isDragging={isDragging}
      isOver={isOver}
      isOverCurrent={isOverCurrent}
      connectDragSource={connectDragSource}
      connectDropTarget={connectDropTarget}
    />
  );
}

QualityProfileItemDragSource.propTypes = {
  editGroups: PropTypes.bool.isRequired,
  groupId: PropTypes.number,
  qualityId: PropTypes.number,
  qualityIndex: PropTypes.string.isRequired,
  name: PropTypes.string.isRequired,
  allowed: PropTypes.bool.isRequired,
  isDraggingUp: PropTypes.bool,
  onQualityProfileItemDragMove: PropTypes.func.isRequired,
  onQualityProfileItemDragEnd: PropTypes.func.isRequired
};

export default QualityProfileItemDragSource;
