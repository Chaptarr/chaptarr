const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

function loadConnector() {
  const filePath = path.join(__dirname, 'BookIndexItemConnector.js');
  const { code } = transformSync(fs.readFileSync(filePath, 'utf8'), {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [
      ['@babel/preset-env', { targets: { node: 'current' }, modules: 'commonjs' }],
      '@babel/preset-react'
    ]
  });
  const modules = {
    react: require('react'),
    'prop-types': require('prop-types'),
    reselect: require('reselect'),
    'react-redux': {
      connect: (mapStateFactory) => (Component) => ({ mapStateFactory, Component })
    },
    'Commands/commandNames': {},
    'Store/Actions/commandActions': { executeCommand: (command) => command },
    'Store/Selectors/createBookAuthorSelector': () => (state) => state.author,
    'Store/Selectors/createBookQualityProfileSelector': () => (state) => state.qualityProfile,
    'Store/Selectors/createBookSelector': () => (state, { bookId }) => state.books[bookId],
    'Store/Selectors/createExecutingCommandsSelector': () => (state) => state.commands
  };
  const module = { exports: {} };
  vm.runInNewContext(code, {
    module,
    exports: module.exports,
    require(name) {
      assert.ok(Object.hasOwn(modules, name), `Unexpected module: ${name}`);
      return modules[name];
    }
  }, { filename: filePath });
  return module.exports.default;
}

for (const mediaType of ['audiobook', 'ebook']) {
  test(`paged ${mediaType} cards receive Both selection without losing their own format`, () => {
    const { mapStateFactory, Component } = loadConnector();
    const mapState = mapStateFactory();
    const state = {
      bookIndex: {
        view: 'posters',
        selectedMediaType: 'both',
        posterOptions: { showSearchAction: true }
      },
      books: {},
      author: { id: 21, authorName: 'Same author' },
      qualityProfile: { id: mediaType === 'ebook' ? 2 : 1 },
      commands: []
    };
    const ownProps = {
      bookId: 32,
      book: { id: 32, authorId: 21, title: 'Same work', mediaType },
      component: 'book-poster'
    };
    const render = (currentState) => new Component({
      ...ownProps,
      ...mapState(currentState, ownProps)
    }).render();

    const both = render(state);
    assert.equal(both.props.selectedMediaType, 'both');
    assert.equal(both.props.mediaType, mediaType);
    assert.equal(both.props.id, 32);

    const single = render({
      ...state,
      bookIndex: { ...state.bookIndex, selectedMediaType: mediaType }
    });
    assert.equal(single.props.selectedMediaType, mediaType);
    assert.equal(single.props.mediaType, mediaType);
  });
}
