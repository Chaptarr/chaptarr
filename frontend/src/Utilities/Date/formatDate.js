import dayjs from 'Utilities/Date/dayjsSetup';

function formatDate(date, dateFormat) {
  if (!date) {
    return '';
  }

  return dayjs(date).format(dateFormat);
}

export default formatDate;
