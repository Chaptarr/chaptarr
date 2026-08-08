import PropTypes from 'prop-types';
import React, { useState, useEffect, useRef } from 'react';
import Icon from 'Components/Icon';
import LinearProgressBar from 'Components/LinearProgressBar';
import IconButton from 'Components/Link/IconButton';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './ImportProgressMessage.css';

function ImportProgressMessage(props) {
  const {
    message,
    progress,
    counters,
    currentItemName,
    currentItemType,
    currentBookMatched,
    currentBookType,
    isPaused,
    onPauseResume
  } = props;
  
  // State to hold the displayed book (persists for 3 seconds)
  const [displayedBook, setDisplayedBook] = useState({ title: null, type: null });
  const bookTimeoutRef = useRef(null);
  
  // Update displayed book when a new one is matched
  useEffect(() => {
    if (currentBookMatched) {
      // Clear any existing timeout
      if (bookTimeoutRef.current) {
        clearTimeout(bookTimeoutRef.current);
      }
      
      // Update displayed book immediately
      setDisplayedBook({ title: currentBookMatched, type: currentBookType });
      
      // Set timeout to clear after 3 seconds
      bookTimeoutRef.current = setTimeout(() => {
        setDisplayedBook({ title: null, type: null });
      }, 3000);
    }
    
    // Cleanup on unmount
    return () => {
      if (bookTimeoutRef.current) {
        clearTimeout(bookTimeoutRef.current);
      }
    };
  }, [currentBookMatched, currentBookType]);

  // Determine icon based on item type
  const getItemIcon = (type) => {
    switch (type) {
      case 'author':
        return icons.AUTHOR;
      case 'book':
        return icons.BOOK;
      case 'file':
        return icons.FILE;
      default:
        return icons.CIRCLE;
    }
  };
  
  // Get book type icon
  const getBookIcon = (type) => {
    if (type === 'ebook') {
      return icons.BOOK;
    }
    return icons.HEADPHONES; // audiobook
  };

  // Calculate progress percentages
  const authorProgress = counters && counters.totalAuthorFolders > 0 
    ? Math.round((counters.processedAuthorFolders / counters.totalAuthorFolders) * 100)
    : 0;
    
  const bookProgress = counters && counters.totalBookFolders > 0
    ? Math.round((counters.processedBookFolders / counters.totalBookFolders) * 100)
    : 0;

  return (
    <div className={styles.importProgressContainer}>
      {/* Book that was just matched - always reserve space */}
      <div className={styles.matchedBook} style={{ visibility: displayedBook.title ? 'visible' : 'hidden' }}>
        <span className={styles.matchedLabel}>{translate('ImportProgressMatchedLabel')}</span>
        <Icon 
          name={getBookIcon(displayedBook.type)} 
          className={styles.matchedIcon}
        />
        <span className={styles.matchedTitle}>{displayedBook.title || '\u00A0'}</span>
      </div>
      
      {/* Current author being processed - always show, even if empty */}
      <div className={isPaused ? styles.processingAuthorPaused : styles.processingAuthor}>
        <span className={styles.processingLabel}>
          {isPaused ? translate('ImportProgressPausedLabel') : translate('ImportProgressProcessingLabel')}
        </span>
        <Icon
          name={icons.AUTHOR}
          className={styles.authorIcon}
        />
        <span className={styles.authorName}>
          {isPaused ? translate('ImportProgressImportPaused') : (currentItemName || translate('ImportProgressScanning'))}
        </span>
      </div>
      
      {/* Counter boxes with mini progress bars */}
      {counters && (
        <div className={styles.counterGrid}>
          {/* Books counter */}
          <div className={styles.counterBox}>
            <div className={styles.counterContent}>
              <span className={styles.counterLabel}>{translate('Books')}</span>
              <span className={styles.counterValue}>{counters.totalBookFolders || 0}</span>
            </div>
            <div className={styles.miniProgressBar}>
              <div 
                className={styles.miniProgressFill} 
                style={{ width: `${bookProgress}%` }}
              />
            </div>
          </div>
          
          {/* Authors counter */}
          <div className={styles.counterBox}>
            <div className={styles.counterContent}>
              <span className={styles.counterLabel}>{translate('Authors')}</span>
              <span className={styles.counterValue}>{counters.totalAuthorFolders || 0}</span>
            </div>
            <div className={styles.miniProgressBar}>
              <div 
                className={styles.miniProgressFill} 
                style={{ width: `${authorProgress}%` }}
              />
            </div>
          </div>
        </div>
      )}
      
      {/* Main progress bar with pause button */}
      <div className={styles.progressBarSection}>
        <LinearProgressBar
          progress={progress || 0}
          showProgressText={true}
          kind="success"
          size="medium"
        />
        {onPauseResume && (
          <IconButton
            name={isPaused ? icons.PLAY : icons.PAUSE}
            title={isPaused ? translate('ResumeImport') : translate('PauseImport')}
            onPress={onPauseResume}
            className={isPaused ? styles.pauseButtonPaused : styles.pauseButton}
          />
        )}
      </div>
    </div>
  );
}

ImportProgressMessage.propTypes = {
  message: PropTypes.string,
  progress: PropTypes.number,
  counters: PropTypes.shape({
    processedAuthorFolders: PropTypes.number,
    totalAuthorFolders: PropTypes.number,
    processedBookFolders: PropTypes.number,
    totalBookFolders: PropTypes.number,
    authorsImported: PropTypes.number,
    matchedBooks: PropTypes.number,
    filesImported: PropTypes.number
  }),
  currentItemName: PropTypes.string,
  currentItemType: PropTypes.string,
  currentBookMatched: PropTypes.string,
  currentBookType: PropTypes.string,
  isPaused: PropTypes.bool,
  onPauseResume: PropTypes.func
};

ImportProgressMessage.defaultProps = {
  isPaused: false,
  progress: 0
};

export default ImportProgressMessage;
