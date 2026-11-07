import React, { useEffect, useState } from 'react';
import translate, { translateWithFallback } from 'Utilities/String/translate';
import messages from './loading-messages.json';
import styles from './LoadingMessage.css';

// Maintain a module-level pool to avoid duplicates until all have been shown
let messagePool = [];

function shuffle(arr) {
  for (let i = arr.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  return arr;
}

function nextMessage() {
  if (messagePool.length === 0) {
    // Refill pool with a fresh random order
    messagePool = shuffle(messages.slice());
  }
  return messagePool.pop();
}

function LoadingMessage() {
  const [message, setMessage] = useState(null);

  // On mount (each loading event), pick the next message without repeats
  useEffect(() => {
    setMessage(nextMessage());
  }, []);

  return (
    <div className={styles.loadingMessage}>
      {message ? translateWithFallback(message.id, message.source) : translate('Loading')}
    </div>
  );
}

export default LoadingMessage;
