import reduce from 'lodash/reduce';
import { update } from 'Store/Actions/baseActions';

function updateBooks(section, books, bookIds, options) {
  const data = reduce(books, (result, item) => {
    if (bookIds.indexOf(item.id) > -1) {
      result.push({
        ...item,
        ...options
      });
    } else {
      result.push(item);
    }

    return result;
  }, []);

  return update({ section, data });
}

export default updateBooks;
