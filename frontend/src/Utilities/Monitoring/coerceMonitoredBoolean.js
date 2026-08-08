function coerceMonitoredBoolean(value) {
  if (value === true) {
    return true;
  }

  if (value === false) {
    return false;
  }

  if (typeof value === 'number') {
    return value > 0;
  }

  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();

    if (normalized === 'all' || normalized === 'select' || normalized === 'selected') {
      return true;
    }

    if (normalized === 'none') {
      return false;
    }

    if (normalized === 'true' || normalized === '1') {
      return true;
    }

    if (normalized === 'false' || normalized === '0') {
      return false;
    }
  }

  return false;
}

export default coerceMonitoredBoolean;
