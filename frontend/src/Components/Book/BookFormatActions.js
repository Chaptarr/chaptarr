import PropTypes from 'prop-types';
import React from 'react';
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
    onAdd
  } = props;

  const icon = getMediaTypeIcon(mediaType);
  const label = getMediaTypeLabel(mediaType);
  const safeInstances = instances || [];

  if (safeInstances.length === 1) {
    const instance = safeInstances[0];

    return (
      <Link
        className={styles.existingFormat}
        to={`/book/${instance.id}`}
        title={translate('OpenFormatOfTitle', { format: label, title })}
        onMouseDown={stopMouseDown}
        onPress={stopEvent}
      >
        <Icon
          name={icon}
          size={size}
        />
      </Link>
    );
  }

  if (safeInstances.length > 1) {
    const body = (
      <div className={styles.instanceList}>
        {
          safeInstances.map((instance) => (
            <Link
              key={instance.id}
              className={styles.instanceLink}
              to={`/book/${instance.id}`}
              onMouseDown={stopMouseDown}
              onPress={stopEvent}
            >
              {getInstanceLabel(instance, mediaType)}
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
            className={styles.existingFormat}
            title={translate('OpenFormatOfTitle', { format: label, title })}
            onMouseDown={stopMouseDown}
            onClick={stopEvent}
          >
            <Icon
              name={icon}
              size={size}
            />
            <span className={styles.badge}>{safeInstances.length}</span>
          </button>
        }
        title={translate('FormatCopies', { count: safeInstances.length, format: label })}
        body={body}
        position={tooltipPositions.BOTTOM}
        canFlip={true}
      />
    );
  }

  if (!showAddActions) {
    return null;
  }

  const addTitle = canAdd ?
    translate('AddAsFormat', { format: label }) :
    translate('CannotAddFormatMissingProviderIds', { format: label });

  return (
    <Link
      className={canAdd ? styles.addFormat : styles.unavailableFormat}
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
      <Icon
        name={icon}
        size={size}
      />
      <Icon
        className={styles.addBadge}
        name={icons.ADD}
        size={10}
      />
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
        onAdd={onAdd}
      />
      <BookFormatAction
        mediaType="ebook"
        title={title}
        instances={localEbookBooks}
        canAdd={canAdd}
        size={size}
        showAddActions={showAddActions}
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
  onAdd: PropTypes.func
};

BookFormatActions.propTypes = {
  title: PropTypes.string.isRequired,
  localAudiobookBooks: PropTypes.arrayOf(PropTypes.object),
  localEbookBooks: PropTypes.arrayOf(PropTypes.object),
  canAdd: PropTypes.bool.isRequired,
  size: PropTypes.number,
  showAddActions: PropTypes.bool,
  onAdd: PropTypes.func
};

BookFormatActions.defaultProps = {
  localAudiobookBooks: [],
  localEbookBooks: [],
  size: 18,
  showAddActions: true
};

export default BookFormatActions;
