import reduce from 'lodash/reduce';
import uniq from 'lodash/uniq';

function selectUniqueIds(items, idProp) {
  const ids = reduce(items, (result, item) => {
    if (item[idProp]) {
      result.push(item[idProp]);
    }

    return result;
  }, []);

  return uniq(ids);
}

export default selectUniqueIds;
