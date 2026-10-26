function words(value) {
  return typeof value === 'string' ?
    value.normalize('NFC').toLowerCase().match(/[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:\.\p{N}+)*/gu) || [] :
    [];
}

export default function createTextSearch(query) {
  const queryWords = words(query);
  const terms = [...new Set(queryWords)];
  const exact = queryWords.join(' ');

  const score = (value) => {
    const candidate = words(value);
    if (!terms.length || !terms.every((term) => candidate.some((word) => word.startsWith(term)))) {
      return 0;
    }

    if (candidate.join(' ') === exact) {
      return 3;
    }

    return terms.every((term) => candidate.includes(term)) ? 2 : 1;
  };

  return { matches: (value) => score(value) > 0, score };
}
