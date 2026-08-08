import _ from 'lodash';

// Helper to normalize field values to the expected wrapped shape
function normalizeFieldValue(raw) {
  // Helper to convert any value to array (handles string, null, undefined)
  const toArray = (v) => v == null ? [] : Array.isArray(v) ? v : [v];
  
  // Already wrapped with value/errors/warnings structure
  if (raw && typeof raw === 'object' && Object.prototype.hasOwnProperty.call(raw, 'value')) {
    // Normalize undefined to null for controlled components
    const value = raw.value === undefined ? null : raw.value;
    return {
      value,
      errors: toArray(raw.errors),
      warnings: toArray(raw.warnings)
    };
  }

  // Primitive or undefined - wrap it
  return {
    value: raw === undefined ? null : raw,
    errors: [],
    warnings: []
  };
}

function getValidationFailures(saveError) {
  if (!saveError || saveError.status !== 400) {
    return [];
  }

  return _.cloneDeep(saveError.responseJSON);
}

function mapFailure(failure) {
  return {
    message: failure.errorMessage,
    link: failure.infoLink,
    detailedMessage: failure.detailedDescription
  };
}

function selectSettings(item, pendingChanges, saveError) {
  const validationFailures = getValidationFailures(saveError);
  
  // Ensure item and pendingChanges are objects
  const safeItem = item || {};
  const safePendingChanges = pendingChanges || {};

  // First, get all properties from the item to ensure we don't miss any
  const allProperties = Object.keys(safeItem);
  
  // Then add any properties from pending changes that aren't in the item
  Object.keys(safePendingChanges).forEach(key => {
    if (!allProperties.includes(key)) {
      allProperties.push(key);
    }
  });

  const settings = _.reduce(allProperties, (result, key) => {
    if (key === 'fields') {
      return result;
    }

    // Return a flattened value
    if (key === 'implementationName') {
      result.implementationName = safeItem[key];

      return result;
    }

    // Normalize the field value to ensure consistent shape
    const normalizedField = normalizeFieldValue(safeItem[key]);
    
    // Extract server validation messages for this field
    // Note: _.remove mutates the array and returns removed elements - this is intentional
    // to prevent the same validation from being matched by multiple fields
    const serverErrors = _.map(_.remove(validationFailures, (failure) => {
      return (failure.propertyName || '').toLowerCase() === key.toLowerCase() && !failure.isWarning;
    }), mapFailure);
    
    const serverWarnings = _.map(_.remove(validationFailures, (failure) => {
      return (failure.propertyName || '').toLowerCase() === key.toLowerCase() && failure.isWarning;
    }), mapFailure);
    
    const setting = {
      value: normalizedField.value,
      errors: serverErrors.concat(normalizedField.errors),
      warnings: serverWarnings.concat(normalizedField.warnings)
    };

    if (safePendingChanges.hasOwnProperty(key)) {
      setting.previousValue = setting.value;
      // Also normalize pending changes
      const normalizedPending = normalizeFieldValue(safePendingChanges[key]);
      setting.value = normalizedPending.value;
      setting.pending = true;
    }

    result[key] = setting;
    return result;
  }, {});

  const fields = _.reduce(safeItem.fields || [], (result, f) => {
    const field = Object.assign({ pending: false }, f);
    const hasPendingFieldChange = safePendingChanges.fields && safePendingChanges.fields.hasOwnProperty(field.name);

    if (hasPendingFieldChange) {
      field.previousValue = field.value;
      field.value = safePendingChanges.fields[field.name];
      field.pending = true;
    }

    field.errors = _.map(_.remove(validationFailures, (failure) => {
      return failure.propertyName.toLowerCase() === field.name.toLowerCase() && !failure.isWarning;
    }), mapFailure);

    field.warnings = _.map(_.remove(validationFailures, (failure) => {
      return failure.propertyName.toLowerCase() === field.name.toLowerCase() && failure.isWarning;
    }), mapFailure);

    result.push(field);
    return result;
  }, []);

  if (fields.length) {
    settings.fields = fields;
  }

  const validationErrors = _.filter(validationFailures, (failure) => {
    return !failure.isWarning;
  });

  const validationWarnings = _.filter(validationFailures, (failure) => {
    return failure.isWarning;
  });

  return {
    settings,
    validationErrors,
    validationWarnings,
    hasPendingChanges: !_.isEmpty(safePendingChanges),
    hasSettings: !_.isEmpty(settings),
    pendingChanges: safePendingChanges
  };
}

export default selectSettings;
