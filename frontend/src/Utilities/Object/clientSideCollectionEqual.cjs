function areShallowEqualItems(previousItems, nextItems) {
  if (previousItems === nextItems) {
    return true;
  }

  if (!Array.isArray(previousItems) || !Array.isArray(nextItems) || previousItems.length !== nextItems.length) {
    return false;
  }

  for (let index = 0; index < previousItems.length; index++) {
    const previousItem = previousItems[index];
    const nextItem = nextItems[index];

    if (previousItem !== nextItem) {
      if (!previousItem || !nextItem) {
        return false;
      }

      const previousKeys = Object.keys(previousItem);
      const nextKeys = Object.keys(nextItem);

      if (previousKeys.length !== nextKeys.length) {
        return false;
      }

      for (const key of previousKeys) {
        if (
          !Object.prototype.hasOwnProperty.call(nextItem, key) ||
          previousItem[key] !== nextItem[key]
        ) {
          return false;
        }
      }
    }
  }

  return true;
}

function clientSideCollectionEqual(previous, next) {
  if (previous === next) {
    return true;
  }

  if (!previous || !next) {
    return false;
  }

  const previousKeys = Object.keys(previous);
  const nextKeys = Object.keys(next);

  if (previousKeys.length !== nextKeys.length) {
    return false;
  }

  for (const key of previousKeys) {
    if (!Object.prototype.hasOwnProperty.call(next, key)) {
      return false;
    }

    if (key === 'items') {
      if (!areShallowEqualItems(previous.items, next.items)) {
        return false;
      }
    } else if (previous[key] !== next[key]) {
      return false;
    }
  }

  return true;
}

module.exports = clientSideCollectionEqual;
