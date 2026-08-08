function formatDurationMinutes(minutes) {
  if (!minutes || minutes <= 0) {
    return '';
  }

  const totalMinutes = Math.round(minutes);
  const hours = Math.floor(totalMinutes / 60);
  const mins = totalMinutes % 60;

  if (hours > 0 && mins > 0) {
    return `${hours}hr ${mins}min`;
  }

  if (hours > 0) {
    return `${hours}hr`;
  }

  return `${mins}min`;
}

export default formatDurationMinutes;