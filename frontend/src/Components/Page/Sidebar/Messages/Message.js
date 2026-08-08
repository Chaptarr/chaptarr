import classNames from 'classnames';
import PropTypes from 'prop-types';
import React, { useState } from 'react';
import Icon from 'Components/Icon';
import { icons } from 'Helpers/Props';
import ImportProgressMessage from './ImportProgressMessage';
import styles from './Message.css';

function getIconName(name) {
  switch (name) {
    case 'ApplicationUpdate':
      return icons.RESTART;
    case 'Backup':
      return icons.BACKUP;
    case 'CheckHealth':
      return icons.HEALTH;
    case 'BulkAuthorBookProgress':
      return icons.BOOK;
    case 'BookSearch':
      return icons.SEARCH;
    case 'Housekeeping':
      return icons.HOUSEKEEPING;
    case 'RescanFolders':
      return icons.RESCAN;
    case 'RefreshAuthor':
      return icons.REFRESH;
    case 'RssSync':
      return icons.RSS;
    case 'SeasonSearch':
      return icons.SEARCH;
    case 'AuthorSearch':
      return icons.SEARCH;
    case 'UpdateSceneMapping':
      return icons.REFRESH;
    default:
      return icons.SPINNER;
  }
}

function getDisplayIconName(name, isWorking, isPaused) {
  if (!isPaused && isWorking) {
    return icons.SPINNER;
  }

  return getIconName(name);
}

function Message(props) {
  const {
    id,
    name,
    message,
    type,
    progress,
    counters,
    currentItemName,
    currentItemType,
    currentBookMatched,
    currentBookType,
    onClick,
    onPauseResume,
    clickable,
    commandStatus
  } = props;
  
  // Use command status from props to determine if paused
  const isPaused = commandStatus === 'paused';
  const isWorking = commandStatus === 'queued' || commandStatus === 'started';
  const [isHovered, setIsHovered] = useState(false);

  // Use custom component for import progress messages
  if (type === 'importProgress') {
    return (
      <ImportProgressMessage
        message={message}
        progress={progress}
        counters={counters}
        currentItemName={currentItemName}
        currentItemType={currentItemType}
        currentBookMatched={currentBookMatched}
        currentBookType={currentBookType}
        isPaused={isPaused}
        onPauseResume={onPauseResume || onClick}
      />
    );
  }
  
  // Use custom component for scan progress messages (now handles entire import pipeline)
  if (type === 'scanProgress') {
    const handleClick = () => {
      if (onClick) {
        onClick(!isPaused);
      }
    };
    
    // Determine the current stage based on message content
    let stageIcon = getIconName(name);
    let stageText = message;
    let pausedText = 'Import paused';
    let actionText = isPaused ? 'Resume' : 'Pause';
    
    if (message.includes('Scanning folder') || message.includes('library scan')) {
      stageIcon = icons.FOLDER_OPEN;
      pausedText = 'Folder scan paused';
    } else if (message.includes('Searching for authors') || message.includes('Processing author')) {
      stageIcon = icons.SEARCH;
      pausedText = 'Author search paused';
    } else if (message.includes('Matching books') || message.includes('Identifying book')) {
      stageIcon = icons.BOOK;
      pausedText = 'Book matching paused';
    } else if (message.includes('Importing book files') || message.includes('Processing files')) {
      stageIcon = icons.DOWNLOAD;
      pausedText = 'File import paused';
    }
    
    return (
      <div 
        className={classNames(
          styles.message,
          styles[type],
          clickable && styles.clickable,
          isPaused && styles.paused,
          isHovered && styles.hovered
        )}
        onClick={clickable ? handleClick : undefined}
        onMouseEnter={() => setIsHovered(true)}
        onMouseLeave={() => setIsHovered(false)}
        role={clickable ? 'button' : undefined}
        tabIndex={clickable ? 0 : undefined}
      >
        <div className={styles.iconContainer}>
          <Icon
            name={isPaused ? icons.PAUSED : stageIcon}
            title={name}
          />
        </div>

        <div className={styles.content}>
          <div className={styles.text}>
            {isPaused ? pausedText : stageText}
          </div>
          {clickable && (
            <div className={styles.actionContainer}>
              <Icon
                name={isPaused ? icons.PLAY : icons.PAUSE}
                size={14}
                className={styles.actionIcon}
              />
              <span className={styles.actionText}>{actionText}</span>
            </div>
          )}
        </div>
      </div>
    );
  }

  return (
    <div 
      className={classNames(
        styles.message,
        styles[type],
        clickable && styles.clickable
      )}
      onClick={clickable ? onClick : undefined}
      role={clickable ? 'button' : undefined}
      tabIndex={clickable ? 0 : undefined}
    >
        <div className={styles.iconContainer}>
          <Icon
            name={isPaused ? icons.PAUSED : getDisplayIconName(name, isWorking, isPaused)}
            isSpinning={isWorking && !isPaused}
            title={name}
          />
        </div>

      <div className={styles.text}>
        {message}
      </div>
    </div>
  );
}

Message.propTypes = {
  id: PropTypes.oneOfType([PropTypes.number, PropTypes.string]),
  name: PropTypes.string.isRequired,
  message: PropTypes.string.isRequired,
  type: PropTypes.string.isRequired,
  progress: PropTypes.number,
  counters: PropTypes.object,
  currentItemName: PropTypes.string,
  currentItemType: PropTypes.string,
  currentBookMatched: PropTypes.string,
  currentBookType: PropTypes.string,
  commandStatus: PropTypes.string,
  commandId: PropTypes.number,
  onClick: PropTypes.func,
  onPauseResume: PropTypes.func,
  clickable: PropTypes.bool
};

Message.defaultProps = {
  clickable: false
};

export default Message;
