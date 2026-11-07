import dayjs from 'Utilities/Date/dayjsSetup';

function isTomorrow(date) {
  if (!date) {
    return false;
  }

  return dayjs(date).isSame(dayjs().add(1, 'day'), 'day');
}

export default isTomorrow;
