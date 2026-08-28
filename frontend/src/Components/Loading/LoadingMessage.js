import React, { useEffect, useState } from 'react';
import styles from './LoadingMessage.css';

const messages = [
  'Organizing your bookshelf...',
  'Turning to the next chaptarr...',
  'Shh! Library voices, please...',
  'Checking for overdue books...',
  'There\'s just nothing quite like the smell of opening a new... eBook?',
  'Finding your place in the story...',
  'Be Kind: Rewind your audiobooks',
  'Loading your next adventure...',
  'I blame Cody...',
  'Sorry Gomeyy...this isn\'t the API you\'re looking for',
  'Shane.......our King',
  'Dodge-ing these piles of books',
  'Solid. Goldan. Dragon.',
  'Have you ever seen DefinitelyNotRLH and RLH in the same room?',
  'A Black Swan and a Goldan Dragon walk into a library...',
  'These old books always get skipped',
  'Something Something Skels'
];

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
  const [message, setMessage] = useState('');

  // On mount (each loading event), pick the next message without repeats
  useEffect(() => {
    setMessage(nextMessage());
  }, []);

  return (
    <div className={styles.loadingMessage}>
      {message}
    </div>
  );
}

export default LoadingMessage;
