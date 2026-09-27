import dayjs from 'Utilities/Date/dayjsSetup';

function isToday(date) {
  if (!date) {
    return false;
  }

  return dayjs(date).isSame(dayjs(), 'day');
}

export default isToday;
