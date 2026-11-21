import { closestCenter, DndContext, KeyboardSensor, PointerSensor, useSensor, useSensors } from '@dnd-kit/core';
import { arrayMove, rectSortingStrategy, SortableContext, sortableKeyboardCoordinates, useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import PropTypes from 'prop-types';
import React, { useEffect, useState } from 'react';
import Alert from 'Components/Alert';
import SelectInput from 'Components/Form/SelectInput';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import IconButton from 'Components/Link/IconButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { icons, kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import NamingOption from './NamingOption';
import styles from './SmartNarratorEditor.css';

const defaultOrder = ['AudioProductionType', 'FullCast', 'Narrators'];
export const smartNarratorPattern = /(\{[- ._,[(]*Smart[- ._]*Narrator)(?::([^{}]*?))?([- ._)\]]*\})/i;

const choices = {
  AudioProductionType: 'NamingProductionLabel',
  GraphicAudio: 'GraphicAudio',
  FullCast: 'NamingFullCastThreePlus',
  Narrator: 'Narrator',
  Narrators: 'NamingAllNarrators',
  FirstNarrator: 'NamingFirstNarrator',
  NarratorCleanName: 'NamingTokenNarratorCleanName',
  NarratorFirst: 'NamingTokenNarratorFirst',
  NarratorLast: 'NamingTokenNarratorLast',
  NarratorInitials: 'NamingTokenNarratorInitials'
};

function SortableChip({ id, label, onDelete }) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging
  } = useSortable({ id });

  return (
    <div
      ref={setNodeRef}
      style={{
        transform: CSS.Transform.toString(transform),
        transition,
        opacity: isDragging ? 0.5 : 1
      }}
      className={`${styles.chip} ${isDragging ? styles.dragging : ''}`}
      {...attributes}
      {...listeners}
      aria-label={label}
    >
      <span className={styles.chipContent}>{label}</span>
      <button
        className={styles.deleteButton}
        onClick={(event) => {
          event.preventDefault();
          event.stopPropagation();
          onDelete();
        }}
        type="button"
        title={translate('Remove')}
        aria-label={`${translate('Remove')} ${label}`}
        onPointerDown={(event) => event.stopPropagation()}
        onKeyDown={(event) => event.stopPropagation()}
      >
        <Icon name={icons.REMOVE} size={12} />
      </button>
    </div>
  );
}

SortableChip.propTypes = {
  id: PropTypes.string.isRequired,
  label: PropTypes.string.isRequired,
  onDelete: PropTypes.func.isRequired
};

function SmartNarratorOrder({ initialOrder, onApply, onCancel }) {
  const [order, setOrder] = useState(initialOrder);
  const [selected, setSelected] = useState('');
  const [preview, setPreview] = useState({ order: null, value: '', failed: false });
  const loading = order.length > 0 && preview.order !== order;
  const error = order.length > 0 && !loading && preview.failed;
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates })
  );
  const available = Object.keys(choices).filter((key) => !order.includes(key));
  const selectedChoice = available.includes(selected) ? selected : available[0];

  useEffect(() => {
    if (!order.length) {
      return undefined;
    }

    let active = true;
    const { request, abortRequest } = createAjaxRequest({
      url: '/config/naming-pattern/preview',
      method: 'POST',
      contentType: 'application/json',
      dataType: 'json',
      data: JSON.stringify({
        pattern: `{Smart Narrator:${order.join(',')}}`,
        sample: {
          narratorName: 'Alice Reader',
          narratorNames: ['Alice Reader', 'Bob Reader', 'Carol Reader'],
          audioProductionType: 'GraphicAudio',
          publisher: 'GraphicAudio',
          isGraphicAudio: true
        }
      })
    });
    request.done(({ path }) => {
      if (active) {
        setPreview({ order, value: path, failed: false });
      }
    });
    request.fail(() => {
      if (active) {
        setPreview({ order, value: '', failed: true });
      }
    });
    return () => {
      active = false;
      abortRequest();
    };
  }, [order]);

  return (
    <div className={styles.editor}>
      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        onDragEnd={({ active, over }) => {
          if (over && active.id !== over.id) {
            setOrder((current) => arrayMove(current, current.indexOf(active.id), current.indexOf(over.id)));
          }
        }}
      >
        <div className={styles.order}>
          <SortableContext items={order} strategy={rectSortingStrategy}>
            {order.map((key) => (
              <SortableChip
                key={key}
                id={key}
                label={choices[key] ? translate(choices[key]) : key}
                onDelete={() => setOrder((current) => current.filter((item) => item !== key))}
              />
            ))}
          </SortableContext>
        </div>
      </DndContext>

      <div className={styles.actions}>
        {available.length > 0 && (
          <>
            <SelectInput
              name="smartNarratorChoice"
              value={selectedChoice}
              values={available.map((key) => ({ key, value: translate(choices[key]) }))}
              onChange={({ value }) => setSelected(value)}
            />
            <IconButton
              name={icons.ADD}
              title={translate('Add')}
              aria-label={translate('Add')}
              onPress={() => setOrder((current) => [...current, selectedChoice])}
            />
          </>
        )}
      </div>

      <div className={styles.preview} aria-live="polite">
        {loading && <LoadingIndicator />}
        {!loading && error && (
          <Alert kind={kinds.DANGER}>{translate('NamingSmartPreviewFailed')}</Alert>
        )}
        {!loading && !error && (
          <span>{translate('NamingSmartExample', { value: (order.length && preview.value) || '—' })}</span>
        )}
      </div>
      <div className={styles.actions}>
        <Button kind={kinds.PRIMARY} isDisabled={!order.length || loading || error}
          onPress={() => onApply(order.join(','))}
        >
          {translate('Apply')}
        </Button>
        <Button onPress={onCancel}>{translate('Cancel')}</Button>
      </div>
    </div>
  );
}

SmartNarratorOrder.propTypes = {
  initialOrder: PropTypes.arrayOf(PropTypes.string).isRequired,
  onApply: PropTypes.func.isRequired,
  onCancel: PropTypes.func.isRequired
};

function SmartNarratorEditor({ value, tokenSeparator, tokenCase, onApply }) {
  const [open, setOpen] = useState(false);
  const match = value.match(smartNarratorPattern);
  const savedOrder = match?.[2]?.split(',').map((key) => Object.keys(choices).find((choice) => choice.toLowerCase() === key.toLowerCase()) || key);

  return (
    <div>
      <NamingOption
        token="{Smart Narrator}"
        example="GraphicAudio"
        tokenSeparator={tokenSeparator}
        tokenCase={tokenCase}
        onPress={() => setOpen(!open)}
      />
      {open && (
        <SmartNarratorOrder
          initialOrder={savedOrder || defaultOrder}
          onApply={(order) => {
            onApply(order);
            setOpen(false);
          }}
          onCancel={() => setOpen(false)}
        />
      )}
    </div>
  );
}

SmartNarratorEditor.propTypes = {
  value: PropTypes.string.isRequired,
  tokenSeparator: PropTypes.string.isRequired,
  tokenCase: PropTypes.string.isRequired,
  onApply: PropTypes.func.isRequired
};

export default SmartNarratorEditor;
