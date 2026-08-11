import reduce from 'lodash/reduce';

function split(input, separator = ',') {
  if (!input) {
    return [];
  }

  return reduce(input.split(separator), (result, s) => {
    if (s) {
      result.push(s);
    }

    return result;
  }, []);
}

export default split;
