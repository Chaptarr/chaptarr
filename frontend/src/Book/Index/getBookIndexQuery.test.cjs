const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

function loadModule(fileName, requireOverrides = {}) {
  const filePath = path.join(__dirname, fileName);
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: ['@babel/preset-env']
  });
  const sandbox = {
    exports: {},
    require: (moduleName) => {
      if (Object.prototype.hasOwnProperty.call(requireOverrides, moduleName)) {
        return requireOverrides[moduleName];
      }

      return module.require(moduleName);
    }
  };

  vm.runInNewContext(code, sandbox, { filename: filePath });

  return sandbox.exports;
}

const getBookIndexQuery = loadModule('getBookIndexQuery.js').default;
const getBookIndexItemMediaType = loadModule('getBookIndexItemMediaType.js').default;
const createBookQualityProfileSelector = loadModule('../../Store/Selectors/createBookQualityProfileSelector.js', {
  './createBookAuthorSelector': () => () => undefined,
  './createBookSelector': () => () => undefined
}).default;

test('both-format library queries use the unfiltered API and keep a separate cache key', () => {
  const query = getBookIndexQuery({ selectedFilterKey: 'all' }, 'both');

  assert.equal(query.queryParams.filters.mediaType, 'all');
  assert.equal(query.queryKey, 'cleanTitle_ascending_all_both');
  assert.equal(query.useClientSideBooks, false);
});

test('both-format custom filters fetch all records for client-side filtering', () => {
  const query = getBookIndexQuery({ selectedFilterKey: 'custom' }, 'both');

  assert.equal(query.queryParams.filters.mediaType, 'all');
  assert.equal(query.useClientSideBooks, true);
});

test('both-format rows use their own media type while single-format views stay scoped', () => {
  assert.equal(getBookIndexItemMediaType('both', 'ebook'), 'ebook');
  assert.equal(getBookIndexItemMediaType('both', 'audiobook'), 'audiobook');
  assert.equal(getBookIndexItemMediaType('ebook', 'audiobook'), 'ebook');
});

test('server-paged rows resolve the quality profile from their embedded format and author', () => {
  const selectQualityProfile = createBookQualityProfileSelector();
  const state = {
    settings: {
      qualityProfiles: {
        items: [
          { id: 3, name: 'Audiobook quality' },
          { id: 8, name: 'eBook quality' }
        ]
      }
    }
  };
  const author = { audiobookQualityProfileId: 3, ebookQualityProfileId: 8 };
  const book = { id: 42, mediaType: 'ebook', author };
  const audiobook = { id: 43, mediaType: 'audiobook', author };

  assert.equal(selectQualityProfile(state, { bookId: book.id, book }).name, 'eBook quality');
  assert.equal(selectQualityProfile(state, { bookId: audiobook.id, book: audiobook }).name, 'Audiobook quality');
});
