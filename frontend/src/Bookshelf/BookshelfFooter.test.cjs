const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const filePath = path.join(__dirname, 'BookshelfFooter.js');
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
    '@babel/preset-react'
  ]
});

function Component(props, context) {
  this.props = props;
  this.context = context;
  this.state = {};
}

const React = {
  Component,
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

const propType = () => propType;
propType.isRequired = propType;
propType.oneOf = () => propType;

function translate(key) {
  return key;
}

function findElements(element, predicate, matches = []) {
  if (Array.isArray(element)) {
    element.forEach((child) => findElements(child, predicate, matches));
    return matches;
  }

  if (!element || typeof element !== 'object') {
    return matches;
  }

  if (predicate(element)) {
    matches.push(element);
  }

  findElements(element.props?.children, predicate, matches);
  return matches;
}

const sandbox = {
  exports: {},
  require(moduleName) {
    if (moduleName === 'react') {
      return { __esModule: true, default: React, Component };
    }

    if (moduleName === 'prop-types') {
      return {
        number: propType,
        string: propType,
        bool: propType,
        object: propType,
        func: propType,
        oneOf: () => propType
      };
    }

    if (moduleName === 'Components/Alert') {
      return { __esModule: true, default: 'Alert' };
    }

    if (moduleName === 'Components/Form/MonitorBooksSelectInput' ||
        moduleName === 'Components/Icon' ||
        moduleName === 'Components/Link/SpinnerButton' ||
        moduleName === 'Components/Page/PageContentFooter' ||
        moduleName === 'Components/Tooltip/Popover') {
      return { __esModule: true, default: moduleName };
    }

    if (moduleName === 'Helpers/Props') {
      return {
        icons: { INFO: 'info' },
        kinds: { DANGER: 'danger', PRIMARY: 'primary' },
        tooltipPositions: { RIGHT: 'right' }
      };
    }

    if (moduleName === 'Utilities/Object/getErrorMessage') {
      return {
        __esModule: true,
        default(xhr, fallback) {
          return xhr?.responseJSON?.message || fallback;
        }
      };
    }

    if (moduleName === 'Utilities/String/translate') {
      return {
        __esModule: true,
        default: translate,
        translateWithFallback(key, fallback) {
          return fallback || key;
        }
      };
    }

    if (moduleName === './BookshelfFooter.css') {
      return {
        __esModule: true,
        default: {
          pageContentFooter: 'pageContentFooter',
          saveErrorContainer: 'saveErrorContainer',
          inputContainer: 'inputContainer',
          label: 'label',
          labelIcon: 'labelIcon',
          updateSelectedButton: 'updateSelectedButton'
        }
      };
    }

    return module.require(moduleName);
  }
};

vm.runInNewContext(code, sandbox, { filename: filePath });

function renderFooter(saveError, onUpdateSelectedPress = () => undefined) {
  const Footer = sandbox.exports.default;
  const footer = new Footer({
    selectedCount: 2,
    selectedMediaType: 'both',
    isSaving: false,
    saveError,
    onUpdateSelectedPress
  });

  footer.state.monitor = 'monitored';
  return footer.render();
}

test('bulk monitoring failures are announced and leave the update action retryable', () => {
  const updates = [];
  const rendered = renderFooter(
    { responseJSON: { message: 'The server rejected the update.' } },
    (payload) => updates.push(payload)
  );
  const alerts = findElements(rendered, (element) => element.type === 'Alert');
  const buttons = findElements(rendered, (element) => element.type === 'Components/Link/SpinnerButton');

  assert.equal(alerts.length, 1);
  assert.equal(alerts[0].props.role, 'alert');
  assert.equal(alerts[0].props.children[0].props.children, 'The server rejected the update.');
  assert.equal(buttons.length, 1);
  assert.equal(buttons[0].props.isDisabled, false);
  assert.equal(buttons[0].props.children, 'UpdateSelected');

  buttons[0].props.onPress();
  assert.equal(updates.length, 1);
  assert.equal(updates[0].monitor, 'monitored');
});

test('bulk monitoring failures use an actionable fallback message', () => {
  const rendered = renderFooter({ status: 500 });
  const alerts = findElements(rendered, (element) => element.type === 'Alert');
  const alertText = findElements(alerts[0], (element) => element.type === 'div')
    .map((element) => element.props.children)
    .flat();

  assert.equal(alertText[0], 'Unable to update book monitoring.');
  assert.equal(alertText[1], 'Your selection is still here. You can try the update again below.');
});

test('the bookshelf footer stays quiet when no update failed', () => {
  const rendered = renderFooter(null);

  assert.equal(findElements(rendered, (element) => element.type === 'Alert').length, 0);
});
