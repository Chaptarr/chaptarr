import PropTypes from 'prop-types';
import React from 'react';
import BidiText from 'Components/BidiText';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import Popover from 'Components/Tooltip/Popover';
import { icons, tooltipPositions } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './BookFormatActions.css';

function getMediaTypeLabel(mediaType) {
  return mediaType === 'ebook' ? translate('Ebook') : translate('Audiobook');
}

function getMediaTypeIcon(mediaType) {
  return mediaType === 'ebook' ? icons.BOOK : icons.HEADPHONES;
}

function getInstanceLabel(instance, mediaType) {
  const parts = [];

  if (instance.title) {
    parts.push(instance.title);
  }

  if (instance.narrator) {
    parts.push(instance.narrator);
  }

  if (instance.disambiguation) {
    parts.push(instance.disambiguation);
  }

  if (!parts.length) {
    parts.push(getMediaTypeLabel(mediaType));
  }

  return parts.join(' - ');
}

function stopEvent(event) {
  event.stopPropagation();
}

function stopMouseDown(event) {
  event.preventDefault();
  event.stopPropagation();
}

function BookFormatAction(props) {
  const {
    mediaType,
    title,
    instances,
    canAdd,
    size,
    showAddActions,
    showLabels,
    onAdd
  } = props;

  const icon = getMediaTypeIcon(mediaType);
  const label = getMediaTypeLabel(mediaType);
  const visibleLabel = translate(mediaType === 'ebook' ? 'EbookLabel' : 'AudiobookLabel');
  const safeInstances = instances || [];

  if (safeInstances.length === 1) {
    const instance = safeInstances[0];
    const openTitle = translate('OpenFormatOfTitle', { format: label, title });
    const existingFormatClassName = showLabels ? `${styles.existingFormat} ${styles.withLabel}` : styles.existingFormat;

    return (
      <Link
        className={existingFormatClassName}
        to={`/book/${instance.id}`}
        aria-label={openTitle}
        title={openTitle}
        onMouseDown={stopMouseDown}
        onPress={stopEvent}
      >
        <span className={styles.iconContainer}>
          <Icon name={icon} size={size} />
        </span>
        {showLabels ? <span className={styles.formatLabel}>{visibleLabel}</span> : null}
      </Link>
    );
  }

  if (safeInstances.length > 1) {
    const existingFormatClassName = showLabels ? `${styles.existingFormat} ${styles.withLabel}` : styles.existingFormat;
    const body = (
      <div className={styles.instanceList}>
        {
          safeInstances.map((instance) => (
            <Link
              key={instance.id}
              className={styles.instanceLink}
              to={`/book/${instance.id}`}
              aria-label={translate('OpenFormatOfTitle', {
                format: label,
                title: getInstanceLabel(instance, mediaType)
              })}
              onMouseDown={stopMouseDown}
              onPress={stopEvent}
            >
              <BidiText>{getInstanceLabel(instance, mediaType)}</BidiText>
            </Link>
          ))
        }
      </div>
    );

    return (
      <Popover
        anchor={
          <button
            type="button"
            className={existingFormatClassName}
            aria-label={translate('FormatCopies', { count: safeInstances.length, format: label })}
            title={translate('OpenFormatOfTitle', { format: label, title })}
            onMouseDown={stopMouseDown}
          >
            <span className={styles.iconContainer}>
              <Icon name={icon} size={size} />
              <span className={styles.badge}>{safeInstances.length}</span>
            </span>
            {showLabels ? <span className={styles.formatLabel}>{visibleLabel}</span> : null}
          </button>
        }
        title={translate('FormatCopies', { count: safeInstances.length, format: label })}
        body={body}
        position={tooltipPositions.BOTTOM}
        canFlip={true}
        isAnchorFocusable={false}
      />
    );
  }

  if (!showAddActions) {
    return null;
  }

  const addTitle = canAdd ?
    translate('AddAsFormat', { format: label }) :
    translate('CannotAddFormatMissingProviderIds', { format: label });
  const addFormatClassName = canAdd ? styles.addFormat : styles.unavailableFormat;

  return (
    <Link
      className={showLabels ? `${addFormatClassName} ${styles.withLabel}` : addFormatClassName}
      aria-label={addTitle}
      title={addTitle}
      isDisabled={!canAdd}
      onMouseDown={stopMouseDown}
      onPress={(event) => {
        stopEvent(event);

        if (canAdd && onAdd) {
          onAdd(mediaType);
        }
      }}
    >
      <span className={styles.iconContainer}>
        <Icon name={icon} size={size} />
        <Icon
          className={styles.addBadge}
          name={icons.ADD}
          size={10}
        />
      </span>
      {showLabels ? <span className={styles.formatLabel}>{visibleLabel}</span> : null}
    </Link>
  );
}

function BookFormatActions(props) {
  const {
    title,
    localAudiobookBooks,
    localEbookBooks,
    canAdd,
    size,
    showAddActions,
    showLabels,
    onAdd
  } = props;

  return (
    <div
      className={styles.formatActions}
      onMouseDown={stopEvent}
      onClick={stopEvent}
    >
      <BookFormatAction
        mediaType="audiobook"
        title={title}
        instances={localAudiobookBooks}
        canAdd={canAdd}
        size={size}
        showAddActions={showAddActions}
        showLabels={showLabels}
        onAdd={onAdd}
      />
      <BookFormatAction
        mediaType="ebook"
        title={title}
        instances={localEbookBooks}
        canAdd={canAdd}
        size={size}
        showAddActions={showAddActions}
        showLabels={showLabels}
        onAdd={onAdd}
      />
    </div>
  );
}

BookFormatAction.propTypes = {
  mediaType: PropTypes.oneOf(['audiobook', 'ebook']).isRequired,
  title: PropTypes.string.isRequired,
  instances: PropTypes.arrayOf(PropTypes.object),
  canAdd: PropTypes.bool.isRequired,
  size: PropTypes.number.isRequired,
  showAddActions: PropTypes.bool.isRequired,
  showLabels: PropTypes.bool.isRequired,
  onAdd: PropTypes.func
};

BookFormatActions.propTypes = {
  title: PropTypes.string.isRequired,
  localAudiobookBooks: PropTypes.arrayOf(PropTypes.object),
  localEbookBooks: PropTypes.arrayOf(PropTypes.object),
  canAdd: PropTypes.bool.isRequired,
  size: PropTypes.number,
  showAddActions: PropTypes.bool,
  showLabels: PropTypes.bool,
  onAdd: PropTypes.func
};

BookFormatActions.defaultProps = {
  localAudiobookBooks: [],
  localEbookBooks: [],
  size: 18,
  showAddActions: true,
  showLabels: false
};

export default BookFormatActions;
