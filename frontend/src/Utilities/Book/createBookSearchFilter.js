import createTextSearch from 'Utilities/String/createTextSearch';

const some = (list, predicate) => Array.isArray(list) && list.some(predicate);

export default function createBookSearchFilter(query, seriesTitle) {
  const { matches } = createTextSearch(query);
  const seriesMatches = matches(seriesTitle);

  return (book) => Boolean(book) && (
    seriesMatches ||
    [book.title, book.authorTitle, book.seriesTitle, book.narrator].some(matches) ||
    some(book.seriesLabels, matches) ||
    some(book.availableNarrators, matches) ||
    some(book.editions, (edition) => [edition?.title, edition?.subtitle, edition?.narrator].some(matches))
  );
}
