import dayjs from 'Utilities/Date/dayjsSetup';

function isYesterday(date) {
  if (!date) {
    return false;
  }

  return dayjs(date).isSame(dayjs().subtract(1, 'day'), 'day');
}

export default isYesterday;
