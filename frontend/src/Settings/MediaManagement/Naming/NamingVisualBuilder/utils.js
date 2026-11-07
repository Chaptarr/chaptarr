// Utility functions for the visual builder

import translate from 'Utilities/String/translate';

const tokenTranslationKeys = {
  AuthorName: 'NamingTokenAuthorName',
  AuthorSortName: 'NamingTokenAuthorSortName',
  AuthorCleanName: 'NamingTokenAuthorCleanName',
  AuthorNameFirstCharacter: 'NamingTokenAuthorNameFirstCharacter',
  BookTitle: 'NamingTokenBookTitle',
  BookSubtitle: 'NamingTokenBookSubtitle',
  ReleaseYear: 'NamingTokenReleaseYear',
  PartNumber: 'NamingTokenPartNumber',
  BookSeriesTitle: 'NamingTokenBookSeriesTitle',
  BookSeries: 'NamingTokenBookSeries',
  BookSeriesPosition: 'NamingTokenBookSeriesPosition',
  NarratorName: 'NamingTokenNarratorName',
  NarratorCleanName: 'NamingTokenNarratorCleanName',
  NarratorFirst: 'NamingTokenNarratorFirst',
  NarratorLast: 'NamingTokenNarratorLast',
  NarratorInitials: 'NamingTokenNarratorInitials',
  Narrators: 'NamingTokenNarrators'
};

export function createId() {
  return Math.random().toString(36).substr(2, 9);
}

export function getTokenColor(tokenKey) {
  const authorTokens = ['AuthorName', 'AuthorSortName', 'AuthorCleanName', 'AuthorNameFirstCharacter', 'AuthorNameThe', 'AuthorDisambiguation'];
  const bookTokens = ['BookTitle', 'BookTitleNoSub', 'BookSubtitle', 'BookCleanTitle', 'ReleaseYear', 'PartNumber', 'BookCleanTitleNoSub', 'BookTitleThe', 'BookTitleTheNoSub', 'BookSubtitleThe', 'BookCleanSubtitle', 'BookDisambiguation', 'EditionYear', 'ReleaseYearFirst'];
  const seriesTokens = ['BookSeries', 'BookSeriesPosition', 'BookSeriesTitle'];
  const narratorTokens = ['NarratorName', 'NarratorNameMultiple', 'NarratorCleanName', 'NarratorFirst', 'NarratorLast', 'NarratorInitials', 'Narrators'];

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
    const count = node.children.length;
    const key = count === 1 ? 'NamingBuilderGroupItem' : 'NamingBuilderGroupItems';
    return `(${translate(key, { count })})`;
  }
  return node.id;
}

export function getTokenDisplayLabel(tokenKey) {
  const translationKey = tokenTranslationKeys[tokenKey];
  return translationKey ? translate(translationKey) : tokenKey;
}

export function getSeparatorDisplayLabel(value) {
  const labels = {
    '/': '(/)',
    '-': '-',
    ' ': translate('NamingTokenSpace'),
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
        path: nodeId
      });
    }
  }

  // Check for valid token keys
  for (const [nodeId, node] of Object.entries(ast.nodesById)) {
    if (node.kind === 'token' && !node.tokenKey) {
      errors.push({
        code: 'INVALID_TOKEN',
        path: nodeId
      });
    }
  }

  return {
    isValid: errors.length === 0,
    errors
  };
}
