import createTextSearch from 'Utilities/String/createTextSearch';

export default function orderAuthorSearchResults(items, term) {
  const { score } = createTextSearch(term);
  const authors = items.filter((item) => item.author)
    .map((item) => ({ item, score: score(item.author.authorName) }))
    .sort((a, b) => b.score - a.score);
  let index = 0;

  return items.map((item) => (item.author ? authors[index++].item : item));
}
