// Utility functions for the visual builder

export function createId() {
  return Math.random().toString(36).substr(2, 9);
}

export function getTokenColor(tokenKey) {
  const authorTokens = ['AuthorName', 'AuthorSortName', 'AuthorCleanName', 'AuthorNameFirstCharacter', 'AuthorNameThe', 'AuthorDisambiguation'];
  const bookTokens = ['BookTitle', 'BookTitleNoSub', 'BookSubtitle', 'BookCleanTitle', 'ReleaseYear', 'PartNumber', 'BookCleanTitleNoSub', 'BookTitleThe', 'BookTitleTheNoSub', 'BookSubtitleThe', 'BookCleanSubtitle', 'BookDisambiguation', 'EditionYear', 'ReleaseYearFirst'];
  const seriesTokens = ['BookSeries', 'BookSeriesPosition', 'BookSeriesTitle'];
  const narratorTokens = ['NarratorName', 'NarratorNameMultiple'];

  if (authorTokens.includes(tokenKey)) return 'blue';
  if (bookTokens.includes(tokenKey)) return 'purple';
  if (seriesTokens.includes(tokenKey)) return 'green';
  if (narratorTokens.includes(tokenKey)) return 'orange';
  
  return 'gray';
}

export function getNodeDisplayLabel(node) {
  if (node.kind === 'token') {
    return getTokenDisplayLabel(node.tokenKey);
  } else if (node.kind === 'separator') {
    return getSeparatorDisplayLabel(node.value);
  } else if (node.kind === 'group') {
    return `(${node.children.length} items)`;
  }
  return node.id;
}

export function getTokenDisplayLabel(tokenKey) {
  const labels = {
    AuthorName: 'First Last',
    AuthorSortName: 'Last First',
    AuthorCleanName: 'firstlast',
    AuthorNameFirstCharacter: 'F Last',
    AuthorNameThe: 'First Last, The',
    AuthorDisambiguation: 'Disambiguation',
    BookTitle: 'Title: Subtitle',
    BookTitleNoSub: 'Title',
    BookSubtitle: 'Subtitle',
    BookCleanTitle: 'Clean Title',
    ReleaseYear: 'Published Year',
    PartNumber: 'Part Number',
    BookSeries: 'Series',
    BookSeriesPosition: 'Series Position',
    BookSeriesTitle: 'Series Title #1',
    NarratorName: 'First Last',
    NarratorNameMultiple: 'Single/Full Cast'
  };

  return labels[tokenKey] || tokenKey;
}

export function getSeparatorDisplayLabel(value) {
  const labels = {
    '/': '/ Folder',
    '-': '-',
    ' ': 'Space',
    '.': '.',
    '_': '_',
    '()': '( )'
  };

  return labels[value] || value;
}

export function validateAst(ast) {
  const errors = [];

  // Check for consecutive folder separators
  let consecutiveFolders = 0;
  for (const rootId of ast.rootIds) {
    const node = ast.nodesById[rootId];
    if (node && node.kind === 'separator' && node.value === '/') {
      consecutiveFolders++;
      if (consecutiveFolders > 1) {
        errors.push({
          code: 'CONSECUTIVE_FOLDERS',
          message: 'Cannot have consecutive folder separators',
          path: rootId
        });
      }
    } else {
      consecutiveFolders = 0;
    }
  }

  // Check for empty groups
  for (const [nodeId, node] of Object.entries(ast.nodesById)) {
    if (node.kind === 'group' && (!node.children || node.children.length === 0)) {
      errors.push({
        code: 'EMPTY_GROUP',
        message: 'Group cannot be empty',
        path: nodeId
      });
    }
  }

  // Check for valid token keys
  for (const [nodeId, node] of Object.entries(ast.nodesById)) {
    if (node.kind === 'token' && !node.tokenKey) {
      errors.push({
        code: 'INVALID_TOKEN',
        message: 'Token key cannot be empty',
        path: nodeId
      });
    }
  }

  return {
    isValid: errors.length === 0,
    errors
  };
}