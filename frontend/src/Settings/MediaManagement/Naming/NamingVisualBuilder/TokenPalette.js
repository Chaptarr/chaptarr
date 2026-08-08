import React from 'react';
import { useDraggable } from '@dnd-kit/core';
import translate from 'Utilities/String/translate';
import styles from './TokenPalette.css';

const tokenCategories = [
  {
    get title() {
      return translate('NamingTokenCategoryAuthor');
    },
    color: 'blue',
    tokens: [
      { tokenKey: 'AuthorName', get label() {
        return translate('NamingTokenAuthorName');
      }, get example() {
        return translate('NamingTokenAuthorNameExample');
      } },
      { tokenKey: 'AuthorSortName', get label() {
        return translate('NamingTokenAuthorSortName');
      }, get example() {
        return translate('NamingTokenAuthorSortNameExample');
      } },
      { tokenKey: 'AuthorCleanName', get label() {
        return translate('NamingTokenAuthorCleanName');
      }, get example() {
        return translate('NamingTokenAuthorCleanNameExample');
      } },
      { tokenKey: 'AuthorNameFirstCharacter', get label() {
        return translate('NamingTokenAuthorNameFirstCharacter');
      }, example: 'A' }
    ]
  },
  {
    get title() {
      return translate('NamingTokenCategoryBook');
    },
    color: 'purple',
    tokens: [
      { tokenKey: 'BookTitle', get label() {
        return translate('NamingTokenBookTitle');
      }, get example() {
        return translate('NamingTokenBookTitleExample');
      } },
      { tokenKey: 'BookSubtitle', get label() {
        return translate('NamingTokenBookSubtitle');
      }, get example() {
        return translate('NamingTokenBookSubtitleExample');
      } },
      { tokenKey: 'ReleaseYear', get label() {
        return translate('NamingTokenReleaseYear');
      }, example: '2024' },
      { tokenKey: 'PartNumber', get label() {
        return translate('NamingTokenPartNumber');
      }, example: '1', args: { format: '0' } }
    ]
  },
  {
    get title() {
      return translate('NamingTokenCategorySeries');
    },
    color: 'green',
    tokens: [
      { tokenKey: 'BookSeriesTitle', get label() {
        return translate('NamingTokenBookSeriesTitle');
      }, get example() {
        return translate('NamingTokenBookSeriesTitleExample');
      } },
      { tokenKey: 'BookSeries', get label() {
        return translate('NamingTokenBookSeries');
      }, get example() {
        return translate('NamingTokenBookSeriesExample');
      } },
      { tokenKey: 'BookSeriesPosition', get label() {
        return translate('NamingTokenBookSeriesPosition');
      }, example: '1' }
    ]
  },
  {
    get title() {
      return translate('NamingTokenCategoryNarrator');
    },
    color: 'orange',
    tokens: [
      { tokenKey: 'NarratorName', get label() {
        return translate('NamingTokenNarratorName');
      }, get example() {
        return translate('NamingTokenNarratorNameExample');
      } },
      { tokenKey: 'NarratorCleanName', get label() {
        return translate('NamingTokenNarratorCleanName');
      }, example: 'AliceReader' },
      { tokenKey: 'NarratorFirst', get label() {
        return translate('NamingTokenNarratorFirst');
      }, example: 'Alice' },
      { tokenKey: 'NarratorLast', get label() {
        return translate('NamingTokenNarratorLast');
      }, example: 'Reader' },
      { tokenKey: 'NarratorInitials', get label() {
        return translate('NamingTokenNarratorInitials');
      }, example: 'AR' },
      { tokenKey: 'Narrators', get label() {
        return translate('NamingTokenNarrators');
      }, get example() {
        return translate('NamingTokenNarratorsExample');
      } }
    ]
  },
  {
    get title() {
      return translate('NamingTokenCategorySeparators');
    },
    color: 'gray',
    tokens: [
      { tokenKey: 'Dash', label: '-', separator: true, value: '-' },
      { tokenKey: 'Parentheses', label: '( )', separator: true, value: '()' },
      { tokenKey: 'FolderSeparator', label: '(/)', separator: true, value: '/' },
      { tokenKey: 'Space', get label() {
        return translate('NamingTokenSpace');
      }, separator: true, value: ' ' },
      { tokenKey: 'Underscore', label: '_', separator: true, value: '_' }
    ]
  }
];

function DraggableToken({ token, color }) {
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    isDragging
  } = useDraggable({
    id: `palette-${token.tokenKey}`,
    data: {
      source: 'palette',
      tokenKey: token.tokenKey,
      label: token.label,
      separator: token.separator,
      value: token.value,
      args: token.args,
      color: color
    }
  });

  const style = transform ? {
    transform: `translate3d(${transform.x}px, ${transform.y}px, 0)`,
    opacity: isDragging ? 0.5 : 1
  } : undefined;

  return (
    <button
      ref={setNodeRef}
      style={style}
      {...listeners}
      {...attributes}
      type="button"
      title={token.example}
    >
      {token.label}
    </button>
  );
}

function TokenPalette() {
  return (
    <div className={styles.palette}>
      {tokenCategories.map((category) => (
        <div
          key={category.title}
          className={styles.category}
        >
          <div className={styles.categoryHeader}>{category.title}</div>
          <div className={styles.tokens}>
            {category.tokens.map((token) => (
              <div key={token.tokenKey} className={`${styles.token} ${styles[category.color]}`}>
                <DraggableToken
                  token={token}
                  color={category.color}
                />
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

export default TokenPalette;
