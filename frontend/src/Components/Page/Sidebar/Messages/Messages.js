import PropTypes from 'prop-types';
import React from 'react';
import MessageConnector from './MessageConnector';
import styles from './Messages.css';

function getMessageRank(message) {
  if (message.name === 'BulkAuthorBookProgress') {
    return 1;
  }

  if (message.name === 'BulkRefreshAuthor') {
    return 2;
  }

  return 0;
}

function Messages({ messages }) {
  const orderedMessages = messages
    .map((message, index) => ({ message, index }))
    .sort((left, right) => {
      const rankDifference = getMessageRank(left.message) - getMessageRank(right.message);

      if (rankDifference !== 0) {
        return rankDifference;
      }

      return left.index - right.index;
    })
    .map(({ message }) => message);

  return (
    <div className={styles.messages}>
      {
        orderedMessages.map((message) => {
          return (
            <MessageConnector
              key={message.id}
              {...message}
            />
          );
        })
      }
    </div>
  );
}

Messages.propTypes = {
  messages: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default Messages;
