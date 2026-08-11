import findIndex from 'lodash/findIndex';

export default function getIndexOfFirstCharacter(items, sortKey, character) {
  return findIndex(items, (item) => {
    const firstCharacter = item[sortKey].charAt(0);

    if (character === '#') {
      return !isNaN(firstCharacter);
    }

    return firstCharacter === character;
  });
}
