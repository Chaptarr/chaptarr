import replacements from './unicodeComparisonFoldData';

function words(value) {
  return typeof value === 'string' ?
    Array.from(value.normalize('NFC'), (character) => replacements[character.codePointAt(0)] ?? character.toLowerCase()).join('').match(/[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:\.\p{N}+)*/gu) || [] :
    [];
}

export default function createTextSearch(query) {
  const queryWords = words(query);
  const terms = [...new Set(queryWords)];
  const exact = queryWords.join(' ');
  const unspaced = /^(?=.*\p{L})[\p{Script_Extensions=Han}\p{Script_Extensions=Hiragana}\p{Script_Extensions=Katakana}\p{Script_Extensions=Hangul}\p{Script_Extensions=Thai}\p{Script_Extensions=Lao}\p{Script_Extensions=Khmer}\p{Script_Extensions=Myanmar}\p{M}\p{N}]+$/u;

  const score = (value) => {
    const candidate = words(value);
    if (!terms.length || !terms.every((term) => candidate.some((word) => (unspaced.test(term) ? word.includes(term) : word.startsWith(term))))) {
      return 0;
    }

    if (candidate.join(' ') === exact) {
      return 3;
    }

    return terms.every((term) => candidate.includes(term)) ? 2 : 1;
  };

  return { matches: (value) => score(value) > 0, score };
}
