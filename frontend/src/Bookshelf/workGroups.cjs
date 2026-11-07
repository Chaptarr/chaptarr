const WORK_ID_PROVIDERS = new Set(['hc', 'gr', 'ol']);

function normalizeProviderId(value, expectedProvider) {
  const id = String(value ?? '').trim();

  if (!id) {
    return null;
  }

  const separator = id.indexOf(':');
  const prefix = separator > 0 ? id.slice(0, separator).toLowerCase() : expectedProvider;
  const valueWithoutPrefix = separator > 0 ? id.slice(separator + 1).trim() : id;
  const hasUnsafeProviderArtifact =
    id.toLowerCase().includes('system.collections') ||
    id.includes('`') ||
    id.includes('[') ||
    id.includes(']');

  if (
    prefix !== expectedProvider ||
    !valueWithoutPrefix ||
    valueWithoutPrefix.includes(':') ||
    hasUnsafeProviderArtifact
  ) {
    return null;
  }

  return `${prefix}:${valueWithoutPrefix.toLowerCase()}`;
}

function getWorkProviderIds(book) {
  const directIds = [
    normalizeProviderId(book?.hardcoverBookId, 'hc'),
    normalizeProviderId(book?.goodreadsWorkId, 'gr'),
    normalizeProviderId(book?.openLibraryWorkId, 'ol')
  ];
  const resourceWorkIds = Array.isArray(book?.workProviderIds) ? book.workProviderIds : [];
  const ids = directIds.concat(
    resourceWorkIds.map((providerId) => {
      const value = String(providerId ?? '').trim();
      const separator = value.indexOf(':');
      const provider = separator > 0 ? value.slice(0, separator).toLowerCase() : '';

      return WORK_ID_PROVIDERS.has(provider) ? normalizeProviderId(value, provider) : null;
    })
  );
  const foreignBookId = String(book?.foreignBookId ?? '').trim();
  const separator = foreignBookId.indexOf(':');

  if (separator > 0 && WORK_ID_PROVIDERS.has(foreignBookId.slice(0, separator).toLowerCase())) {
    ids.push(normalizeProviderId(foreignBookId, foreignBookId.slice(0, separator).toLowerCase()));
  }

  return Array.from(new Set(ids.filter(Boolean)));
}

function getBookshelfWorkGroups(books) {
  const items = Array.isArray(books) ? books.filter(Boolean) : [];
  const parents = items.map((_, index) => index);

  function find(index) {
    if (parents[index] !== index) {
      parents[index] = find(parents[index]);
    }

    return parents[index];
  }

  function join(left, right) {
    const leftRoot = find(left);
    const rightRoot = find(right);

    if (leftRoot !== rightRoot) {
      parents[rightRoot] = leftRoot;
    }
  }

  const firstIndexByWorkId = new Map();

  items.forEach((book, index) => {
    getWorkProviderIds(book).forEach((workId) => {
      if (firstIndexByWorkId.has(workId)) {
        join(index, firstIndexByWorkId.get(workId));
      } else {
        firstIndexByWorkId.set(workId, index);
      }
    });
  });

  const components = new Map();
  items.forEach((book, index) => {
    const root = find(index);
    const component = components.get(root) || [];
    component.push({ book, index });
    components.set(root, component);
  });

  const groups = [];
  components.forEach((component) => {
    const mediaTypes = new Set(component.map(({ book }) => String(book.mediaType || '').toLowerCase()));
    const hasBothFormats = mediaTypes.has('audiobook') && mediaTypes.has('ebook');

    if (component.length > 1 && hasBothFormats) {
      const groupBooks = component.map(({ book }) => book);
      const firstIndex = Math.min(...component.map(({ index }) => index));

      groups.push({
        key: `work:${groupBooks.map((book) => book.id).sort((a, b) => a - b).join('-')}`,
        title: groupBooks[0].title,
        books: groupBooks,
        firstIndex
      });
      return;
    }

    component.forEach(({ book, index }) => {
      groups.push({
        key: `book:${book.id}`,
        title: book.title,
        books: [book],
        firstIndex: index
      });
    });
  });

  return groups.sort((left, right) => left.firstIndex - right.firstIndex);
}

module.exports = { getBookshelfWorkGroups, getWorkProviderIds };
