import dayjs from 'Utilities/Date/dayjsSetup';

function isBefore(date, offsets = {}) {
  if (!date) {
    return false;
  }

  let offsetTime = dayjs();

  Object.keys(offsets).forEach((key) => {
    offsetTime = offsetTime.add(offsets[key], key);
  });

  return dayjs(date).isBefore(offsetTime);
}

export default isBefore;
