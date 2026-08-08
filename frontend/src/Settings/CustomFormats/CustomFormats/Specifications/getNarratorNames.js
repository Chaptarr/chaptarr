export const NARRATOR_NAMES_IMPLEMENTATION = 'NarratorNamesSpecification';

function normalizeValues(value) {
  let values = [];

  if (Array.isArray(value)) {
    values = value;
  } else if (typeof value === 'string') {
    values = value.split(',');
  }

  return values
    .map((name) => name.trim())
    .filter((name) => !!name);
}

export default function getNarratorNames(specification) {
  if (specification?.implementation !== NARRATOR_NAMES_IMPLEMENTATION) {
    return [];
  }

  const namesField = specification.fields?.find((field) => field.name === 'names');
  return normalizeValues(namesField?.value);
}
