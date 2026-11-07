const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const propType = () => propType;
propType.isRequired = propType;
propType.oneOf = () => propType;

const PropTypes = new Proxy({}, {
  get() {
    return propType;
  }
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

function loadComponent() {
  const filePath = path.join(__dirname, 'BookshelfFormatGroupingHelp.js');
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [
      [
        '@babel/preset-env',
        {
          targets: { browsers: ['last 2 versions'] },
          loose: true,
          modules: 'commonjs'
        }
      ],
      '@babel/preset-react'
    ]
  });

  const moduleMocks = new Map([
    ['react', { __esModule: true, default: React }],
    ['prop-types', { __esModule: true, default: PropTypes }],
    ['Components/Icon', { __esModule: true, default: 'Icon' }],
    ['Components/Tooltip/Popover', { __esModule: true, default: 'Popover' }],
    ['Helpers/Props', {
      icons: { INFO: 'info' },
      kinds: { INFO: 'info' },
      tooltipPositions: { BOTTOM: 'bottom' }
    }],
    ['Utilities/String/translate', { __esModule: true, default: (key) => key }]
  ]);

  const sandbox = {
    exports: {},
    require(moduleName) {
      if (!moduleMocks.has(moduleName)) {
        throw new Error(`Unexpected import: ${moduleName}`);
      }

      return moduleMocks.get(moduleName);
    }
  };

  vm.runInNewContext(code, sandbox, { filename: filePath });
  return sandbox.exports.default;
}

test('format grouping help stays out of single-format views', () => {
  const renderHelp = loadComponent();

  assert.equal(renderHelp({ selectedMediaType: 'audiobook' }), null);
  assert.equal(renderHelp({ selectedMediaType: 'ebook' }), null);
});

test('Both view has an accessible popover explaining work-ID grouping', () => {
  const renderHelp = loadComponent();
  const popover = renderHelp({
    className: 'formatGroupingHelp',
    selectedMediaType: 'both'
  });

  assert.equal(popover.type, 'Popover');
  assert.equal(popover.props.className, 'formatGroupingHelp');
  assert.equal(popover.props.title, 'BookshelfFormatGroupingHelpTitle');
  assert.equal(popover.props.body, 'BookshelfFormatGroupingHelpText');
  assert.equal(popover.props.isAnchorFocusable, true);
  assert.equal(popover.props.anchor.type, 'Icon');
});
