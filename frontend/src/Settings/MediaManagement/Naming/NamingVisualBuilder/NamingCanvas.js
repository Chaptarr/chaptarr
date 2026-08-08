import { useDroppable } from '@dnd-kit/core';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import React from 'react';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './NamingCanvas.css';

function DropZone({ index, isActive }) {
  const { setNodeRef, isOver } = useDroppable({
    id: `dropzone-${index}`,
    data: {
      source: 'canvas',
      insertIndex: index
    }
  });

  return (
    <div
      ref={setNodeRef}
      className={`${styles.dropZone} ${isOver ? styles.dropZoneActive : ''} ${isActive ? styles.dropZoneVisible : ''}`}
    />
  );
}

function SortableChip({ node, ast, onDelete, color }) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging
  } = useSortable({
    id: node.id,
    data: {
      source: 'canvas',
      nodeId: node.id,
      color,
      label: getNodeLabel(node)
    }
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    opacity: isDragging ? 0.5 : 1
  };

  const handleDelete = (e) => {
    e.preventDefault();
    e.stopPropagation();
    onDelete(node.id);
  };

  const getChipContent = () => {
    switch (node.kind) {
      case 'token':
        return getTokenLabel(node);
      case 'separator':
        return getSeparatorLabel(node);
      case 'group':
        return (
          <span className={styles.groupContent}>
            ({node.children.map((childId) =>
              (ast.nodesById[childId] ? getNodeLabel(ast.nodesById[childId]) : '')
            ).join('')})
          </span>
        );
      default:
        return node.id;
    }
  };

  return (
    <div
      ref={setNodeRef}
      style={style}
      className={`${styles.chip} ${styles[color]} ${isDragging ? styles.dragging : ''}`}
      {...attributes}
      {...listeners}
    >
      <span className={styles.chipContent}>
        {getChipContent()}
      </span>
      <button
        className={styles.deleteButton}
        onClick={handleDelete}
        type="button"
        title="Remove"
      >
        <Icon name={icons.REMOVE} size={12} />
      </button>
    </div>
  );
}

function getTokenLabel(node) {
  const labels = {
    AuthorName: 'First Last',
    AuthorSortName: 'Last First',
    AuthorCleanName: 'firstlast',
    AuthorNameFirstCharacter: 'F Last',
    BookTitle: 'Title',
    BookSubtitle: 'Subtitle',
    ReleaseYear: 'Published Year',
    PartNumber: 'Part Number',
    BookSeriesTitle: 'Series title - book position',
    BookSeries: 'Series name',
    BookSeriesPosition: 'Series Position',
    NarratorName: 'First Last',
    NarratorNameMultiple: 'Single Narrator Name, Full cast if multiple'
  };

  return labels[node.tokenKey] || node.tokenKey;
}

function getSeparatorLabel(node) {
  const labels = {
    '/': '(/)',
    '-': '-',
    ' ': 'Space',
    '.': '.',
    _: '_',
    '()': '( )'
  };

  return labels[node.value] || node.value;
}

function getNodeLabel(node) {
  switch (node.kind) {
    case 'token':
      return getTokenLabel(node);
    case 'separator':
      return getSeparatorLabel(node);
    case 'group':
      return `(${node.children.length} items)`;
    default:
      return '';
  }
}

function getNodeColor(node) {
  if (node.kind === 'separator') {
    return 'gray';
  }
  if (node.kind === 'group') {
    return 'purple';
  }

  const authorTokens = ['AuthorName', 'AuthorSortName', 'AuthorCleanName', 'AuthorNameFirstCharacter', 'AuthorNameThe', 'AuthorDisambiguation'];
  const bookTokens = ['BookTitle', 'BookTitleNoSub', 'BookSubtitle', 'BookCleanTitle', 'ReleaseYear', 'PartNumber'];
  const seriesTokens = ['BookSeries', 'BookSeriesPosition', 'BookSeriesTitle'];
  const narratorTokens = ['NarratorName', 'NarratorNameMultiple'];

  if (authorTokens.includes(node.tokenKey)) {
    return 'blue';
  }
  if (bookTokens.includes(node.tokenKey)) {
    return 'purple';
  }
  if (seriesTokens.includes(node.tokenKey)) {
    return 'green';
  }
  if (narratorTokens.includes(node.tokenKey)) {
    return 'orange';
  }

  return 'gray';
}

function NamingCanvas({ ast, onDeleteNode, isDragging, compact = false }) {
  const { setNodeRef, isOver } = useDroppable({
    id: 'canvas',
    data: {
      source: 'canvas'
    }
  });

  const hasItems = ast.rootIds.length > 0;

  if (compact) {
    // Compact mode for path builder above drag area
    return (
      <div ref={setNodeRef} className={`${styles.pathBuilder} ${isOver ? styles.canvasOver : ''}`}>
        {!hasItems && (
          <span style={{ color: 'var(--dimColor)', fontSize: '12px' }}>
            {translate('NamingBuilderDragInstructions')}
          </span>
        )}

        {hasItems && (
          <>
            <DropZone index={0} isActive={isDragging} />
            {ast.rootIds.map((nodeId, index) => {
              const node = ast.nodesById[nodeId];
              if (!node) {
                return null;
              }

              const color = getNodeColor(node);

              return (
                <React.Fragment key={nodeId}>
                  <SortableChip
                    node={node}
                    ast={ast}
                    onDelete={onDeleteNode}
                    color={color}
                  />
                  <DropZone index={index + 1} isActive={isDragging} />
                </React.Fragment>
              );
            })}
          </>
        )}
      </div>
    );
  }

  // Regular mode
  return (
    <div ref={setNodeRef} className={styles.canvas}>
      <div className={styles.canvasHeader}>
        <h3>{translate('NamingBuilderFilePathHeader')}</h3>
        <span className={styles.instructions}>
          {translate('NamingBuilderDragInstructions')}
        </span>
      </div>

      <div className={`${styles.canvasContent} ${isOver ? styles.canvasOver : ''}`}>
        {!hasItems && (
          <div className={styles.emptyState}>
            <Icon name={icons.ARROW_LEFT} size={24} />
            <span>{translate('NamingBuilderEmptyStateInstructions')}</span>
          </div>
        )}

        {hasItems && (
          <div className={styles.pathBuilder}>
            <DropZone index={0} isActive={isDragging} />

            {ast.rootIds.map((nodeId, index) => {
              const node = ast.nodesById[nodeId];
              if (!node) {
                return null;
              }

              const color = getNodeColor(node);

              return (
                <React.Fragment key={nodeId}>
                  <SortableChip
                    node={node}
                    ast={ast}
                    onDelete={onDeleteNode}
                    color={color}
                  />
                  <DropZone index={index + 1} isActive={isDragging} />
                </React.Fragment>
              );
            })}
          </div>
        )}
      </div>

      <div className={styles.canvasFooter}>
        <div className={styles.colorLegend}>
          <span className={`${styles.legendItem} ${styles.blue}`}>{translate('Author')}</span>
          <span className={`${styles.legendItem} ${styles.purple}`}>{translate('Book')}</span>
          <span className={`${styles.legendItem} ${styles.green}`}>{translate('Series')}</span>
          <span className={`${styles.legendItem} ${styles.orange}`}>{translate('Narrator')}</span>
          <span className={`${styles.legendItem} ${styles.gray}`}>{translate('NamingTokenCategorySeparators')}</span>
        </div>
      </div>
    </div>
  );
}

export default NamingCanvas;
