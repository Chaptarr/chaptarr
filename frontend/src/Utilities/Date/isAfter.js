import dayjs from 'Utilities/Date/dayjsSetup';

function isAfter(date, offsets = {}) {
  if (!date) {
    return false;
  }

  let offsetTime = dayjs();

  Object.keys(offsets).forEach((key) => {
    offsetTime = offsetTime.add(offsets[key], key);
  });

  return dayjs(date).isAfter(offsetTime);
}

export default isAfter;
