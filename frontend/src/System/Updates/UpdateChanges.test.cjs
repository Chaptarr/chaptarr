const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const filePath = path.join(__dirname, 'UpdateChanges.tsx');
const source = fs.readFileSync(filePath, 'utf8');
const { code } = transformSync(source, {
  filename: filePath,
  babelrc: false,
  configFile: false,
  presets: [
    [
      '@babel/preset-env',
      {
        targets: {
          browsers: ['last 2 versions']
        },
        loose: true,
        modules: 'commonjs'
      }
    ],
    '@babel/preset-react',
    '@babel/preset-typescript'
  ]
});

const React = {
  createElement(type, props, ...children) {
    return {
      type,
      props: {
        ...props,
        children: children.length === 1 ? children[0] : children
      }
    };
  }
};

const sandbox = {
  exports: {},
  require(moduleName) {
    if (moduleName === 'react') {
      return { __esModule: true, default: React };
    }

    if (moduleName === 'Components/Markdown/InlineMarkdown') {
      return { __esModule: true, default: 'InlineMarkdown' };
    }

    if (moduleName === './UpdateChanges.css') {
      return { __esModule: true, default: { title: 'title' } };
    }

    return module.require(moduleName);
  }
};

vm.runInNewContext(code, sandbox, { filename: filePath });

test('release change summaries render each unique change under the browser Babel target', () => {
  const updateChanges = sandbox.exports.default;
  const rendered = updateChanges({
    title: 'Changes',
    changes: ['Fix #42', 'Fix #42', 'Improve matching #43']
  });
  const listItems = rendered.props.children[1].props.children;
  const messages = listItems.map((item) => item.props.children.props.data);

  assert.deepEqual(Array.from(messages), [
    'Fix [#42](https://github.com/snapetech-org/chaptarrng/issues/42)',
    'Improve matching [#43](https://github.com/snapetech-org/chaptarrng/issues/43)'
  ]);
});
