import dayjs from 'Utilities/Date/dayjsSetup';
import PropTypes from 'prop-types';
import React from 'react';
import AgendaEventConnector from './AgendaEventConnector';
import styles from './Agenda.css';

function Agenda(props) {
  const {
    items
  } = props;

  return (
    <div className={styles.agenda}>
      {
        items.map((item, index) => {
          const dayjsDate = dayjs(item.releaseDate);
          const showDate = index === 0 ||
            !dayjs(items[index - 1].releaseDate).isSame(dayjsDate, 'day');

          return (
            <AgendaEventConnector
              key={item.id}
              bookId={item.id}
              showDate={showDate}
              {...item}
            />
          );
        })
      }
    </div>
  );
}

Agenda.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired
};

export default Agenda;
