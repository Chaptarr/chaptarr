import dayjs from 'Utilities/Date/dayjsSetup';

function isInNextWeek(date) {
  if (!date) {
    return false;
  }
  const now = dayjs();
  return dayjs(date).isBetween(now, now.add(6, 'days').endOf('day'));
}

export default isInNextWeek;
