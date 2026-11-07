const crypto = require('node:crypto');

function validateSourceEqualities(audit, english, locales, catalogs) {
  const errors = [];
  if (audit.version !== 1 || !audit.entries || typeof audit.entries !== 'object' || Array.isArray(audit.entries)) {
    return ['Source equality audit must define version 1 and an entries map.'];
  }
  for (const [tag, entries] of Object.entries(audit.entries)) {
    const locale = locales.find((item) => item.tag === tag && tag !== 'en');
    if (!locale) {
      errors.push('Source equality audit references unsupported locale ' + tag + '.');
      continue;
    }
    for (const [key, record] of Object.entries(entries)) {
      const source = english[key];
      if (typeof source !== 'string' || catalogs[locale.file]?.[key] !== source) {
        errors.push(tag + ' source equality must reference an exact source-equal value: ' + key + '.');
        continue;
      }
      const fingerprint = crypto.createHash('sha256').update(key + '\0' + source, 'utf8').digest('hex');
      if (!record || record.sourceFingerprint !== fingerprint || !['invariant', 'native-equivalent'].includes(record.category)) {
        errors.push(tag + ' source equality needs a current fingerprint and explicit category: ' + key + '.');
      }
    }
  }
  return errors;
}

module.exports = { validateSourceEqualities };
