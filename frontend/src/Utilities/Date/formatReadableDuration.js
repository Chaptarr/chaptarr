import dayjs from 'Utilities/Date/dayjsSetup';

function formatReadableDuration(timeSpan) {
  if (!timeSpan) {
    return '';
  }

  const duration = dayjs.duration(timeSpan);

  const totalHours = Math.floor(duration.asHours());
  const minutes = duration.get('minutes'); // Only the minute portion, not total minutes

  const parts = [];

  if (totalHours > 0) {
    parts.push(`${totalHours} hr`);
  }

  if (minutes > 0) {
    parts.push(`${minutes} min`);
  }

  // If no hours or minutes, show "0 min"
  if (parts.length === 0) {
    return '0 min';
  }

  return parts.join(' ');
}

export default formatReadableDuration;
