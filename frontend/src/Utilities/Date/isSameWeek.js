import dayjs from 'Utilities/Date/dayjsSetup';

function isSameWeek(date) {
  if (!date) {
    return false;
  }

  return dayjs(date).isSame(dayjs(), 'week');
}

export default isSameWeek;
