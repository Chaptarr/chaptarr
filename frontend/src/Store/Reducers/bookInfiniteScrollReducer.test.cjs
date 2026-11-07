const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const filePath = path.join(__dirname, 'bookInfiniteScrollReducer.js');
const source = fs.readFileSync(filePath, 'utf8');
const { code } = transformSync(source, {
  filename: filePath,
  babelrc: false,
  configFile: false,
  presets: [[
    '@babel/preset-env',
    {
      targets: {
        browsers: ['last 2 versions']
      },
      loose: true,
      modules: 'commonjs'
    }
  ]]
});

const sandbox = {
  exports: {},
  require: (moduleName) => {
    if (moduleName === 'redux-actions') {
      return {
        handleActions: (handlers, initialState) => (state = initialState, action) => {
          const handler = handlers[action.type];
          return handler ? handler(state, action) : state;
        }
      };
    }

    if (moduleName === 'Store/Actions/baseActions') {
      return { REMOVE_ITEM: 'removeItem', UPDATE_ITEM: 'updateItem' };
    }

    return module.require(moduleName);
  }
};

vm.runInNewContext(code, sandbox, { filename: filePath });

const reducer = sandbox.exports.default;
const { initialState, selectQueryBooks } = sandbox.exports;

test('paged book results keep numeric ids after deduplication', () => {
  const queryKey = 'title_ascending_all_audiobook';
  const requestId = 'page:title_ascending_all_audiobook:0';
  const book = { id: 30, title: 'Fixture Audiobook' };

  let state = reducer(initialState, {
    type: 'books/setActiveQuery',
    payload: {
      queryKey,
      queryParams: {
        sortKey: 'title',
        sortDirection: 'ascending',
        filters: { mediaType: 'audiobook' }
      }
    }
  });

  state = reducer(state, {
    type: 'books/fetchPageRequest',
    payload: { queryKey, pageIndex: 0, requestId }
  });

  state = reducer(state, {
    type: 'books/fetchPageSuccess',
    payload: {
      queryKey,
      pageIndex: 0,
      requestId,
      ids: [book.id, book.id],
      entities: { [book.id]: book },
      totalCount: 1,
      offset: 0
    }
  });

  const pageIds = Array.from(state.queries[queryKey].pages[0].ids);
  const selectedBooks = Array.from(selectQueryBooks({ bookInfiniteScroll: state }, queryKey));

  assert.deepEqual(pageIds, [book.id]);
  assert.equal(selectedBooks.length, 1);
  assert.equal(selectedBooks[0].id, book.id);
  assert.equal(selectedBooks[0].title, book.title);
});
