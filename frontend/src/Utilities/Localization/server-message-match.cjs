const MAX_MESSAGE_LENGTH = 8192;

function compilePattern(pattern, parameters) {
  const parts = [];
  const names = [];
  let cursor = 0;
  const parameterNames = new Set(parameters);

  for (const match of pattern.matchAll(/\{(value\d+)\}/g)) {
    if (parameterNames.has(match[1])) {
      parts.push(pattern.slice(cursor, match.index));
      names.push(match[1]);
      cursor = match.index + match[0].length;
    }
  }

  parts.push(pattern.slice(cursor));

  if (names.length !== parameters.length || new Set(names).size !== names.length) {
    throw new Error('Server-message pattern must contain every parameter once');
  }

  if (parts.slice(1, -1).some((part) => !part)) {
    throw new Error('Adjacent server-message parameters are ambiguous');
  }

  if (!parts.some((part) => (/[A-Za-z]/).test(part))) {
    throw new Error('Server-message patterns require fixed application wording');
  }

  return { parts, names, specificity: parts.join('').length };
}

function compileServerMessageMatchers(records) {
  const exact = new Map();
  const templates = [];
  const ids = new Set();

  for (const record of records) {
    if (!record || typeof record.id !== 'string' || !record.id ||
        typeof record.source !== 'string' || !record.source ||
        !Array.isArray(record.parameters) || !Array.isArray(record.patterns) ||
        !record.patterns.length || ids.has(record.id)) {
      throw new Error('Invalid or duplicate server-message record');
    }

    ids.add(record.id);
    const parameters = record.parameters.map((parameter) => parameter.name);

    if (parameters.some((name) => !(/^value\d+$/).test(name)) ||
        new Set(parameters).size !== parameters.length) {
      throw new Error('Invalid server-message parameters');
    }

    for (const pattern of record.patterns) {
      if (typeof pattern !== 'string' || !pattern) {
        throw new Error('Invalid server-message pattern');
      }

      const compiled = compilePattern(pattern, parameters);
      const entry = { ...compiled, id: record.id, source: record.source };

      if (!parameters.length) {
        const previous = exact.get(pattern);

        if (previous && previous.id !== record.id) {
          throw new Error('Conflicting exact server-message patterns');
        }

        exact.set(pattern, entry);
      } else {
        templates.push(entry);
      }
    }
  }

  templates.sort((left, right) => right.specificity - left.specificity);
  return { exact, templates };
}

function matchTemplate(message, template) {
  const { parts, names } = template;

  if (!message.startsWith(parts[0]) || !message.endsWith(parts[parts.length - 1])) {
    return null;
  }

  const values = {};
  let cursor = parts[0].length;

  for (let index = 0; index < names.length; index++) {
    const nextPart = parts[index + 1];
    const last = index === names.length - 1;
    const boundary = last ? message.length - nextPart.length : message.indexOf(nextPart, cursor);

    if (boundary < cursor) {
      return null;
    }

    // Repeated interior delimiters make the association between dynamic values
    // ambiguous. Leave that message unchanged instead of guessing a split.
    if (!last && message.indexOf(nextPart, boundary + nextPart.length) !== -1) {
      return null;
    }

    values[names[index]] = message.slice(cursor, boundary);
    cursor = boundary + nextPart.length;
  }

  return cursor === message.length ? values : null;
}

function matchServerMessage(message, matchers) {
  if (typeof message !== 'string' || !message || message.length > MAX_MESSAGE_LENGTH) {
    return null;
  }

  const exact = matchers.exact.get(message);

  if (exact) {
    return { id: exact.id, source: exact.source, tokens: {} };
  }

  let result = null;
  let specificity = -1;

  for (const template of matchers.templates) {
    if (template.specificity < specificity) {
      break;
    }

    const tokens = matchTemplate(message, template);

    if (tokens) {
      if (result && result.id !== template.id) {
        return null;
      }

      result = { id: template.id, source: template.source, tokens };
      specificity = template.specificity;
    }
  }

  return result;
}

module.exports = { compileServerMessageMatchers, matchServerMessage };
