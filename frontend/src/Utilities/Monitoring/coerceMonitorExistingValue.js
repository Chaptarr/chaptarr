function coerceMonitorExistingValue(value) {
  if (typeof value === 'number') {
    return value;
  }

  if (typeof value === 'boolean') {
    return value ? 1 : 0;
  }

  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();

    if (normalized === 'all') {
      return 1;
    }

    if (normalized === 'select' || normalized === 'selected') {
      return 2;
    }

    if (normalized === 'none') {
      return 0;
    }

    const parsed = Number(normalized);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return value;
}

export default coerceMonitorExistingValue;
