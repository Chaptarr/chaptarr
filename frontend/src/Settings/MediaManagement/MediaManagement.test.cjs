const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { transformSync } = require('@babel/core');

const propType = new Proxy(() => propType, {
  get() {
    return propType;
  },
  apply() {
    return propType;
  }
});

const PropTypes = new Proxy({}, {
  get() {
    return propType;
  }
});

const React = {
  Component: class Component {
    constructor(props) {
      this.props = props;
    }
  },
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

function Stub() {
  return null;
}

function loadMediaManagement() {
  const filePath = path.join(__dirname, 'MediaManagement.js');
  const source = fs.readFileSync(filePath, 'utf8');
  const { code } = transformSync(source, {
    filename: filePath,
    babelrc: false,
    configFile: false,
    presets: [
      ['@babel/preset-env', { targets: { node: 'current' }, modules: 'commonjs' }],
      '@babel/preset-react'
    ]
  });

  const modules = {
    'prop-types': PropTypes,
    react: React,
    'Components/Alert': Stub,
    'Components/FieldSet': Stub,
    'Components/Form/Form': Stub,
    'Components/Form/FormGroup': Stub,
    'Components/Form/FormInputGroup': Stub,
    'Components/Form/FormLabel': Stub,
    'Components/Loading/LoadingIndicator': Stub,
    'Components/Page/PageContent': Stub,
    'Components/Page/PageContentBody': Stub,
    'Helpers/Props': {
      inputTypes: { CHECK: 'check', NUMBER: 'number', PATH: 'path', SELECT: 'select', TEXT: 'text', UMASK: 'umask' },
      kinds: { DANGER: 'danger' },
      sizes: { MEDIUM: 'medium' }
    },
    'Settings/DownloadClients/RemotePathMappings/RemotePathMappingsConnector': Stub,
    'Settings/SettingsToolbarConnector': Stub,
    'Utilities/String/translate': (key) => key,
    './Naming/NamingConnector': Stub,
    './RootFolder/RootFoldersConnector': Stub
  };

  const module = { exports: {} };
  vm.runInNewContext(code, {
    module,
    exports: module.exports,
    require(name) {
      assert.ok(Object.prototype.hasOwnProperty.call(modules, name), `Unexpected module: ${name}`);
      return modules[name];
    }
  }, { filename: filePath });

  return module.exports.default || module.exports;
}

function findByProp(tree, name, value) {
  if (!tree || typeof tree !== 'object') {
    return null;
  }

  if (tree.props?.[name] === value) {
    return tree;
  }

  const children = tree.props?.children;
  const childList = Array.isArray(children) ? children : [children];

  for (const child of childList) {
    const match = findByProp(child, name, value);

    if (match) {
      return match;
    }
  }

  return null;
}

test('new-author format-folder setting is visible without advanced settings and defaults off', () => {
  const MediaManagement = loadMediaManagement();
  const component = new MediaManagement({
    advancedSettings: false,
    isFetching: false,
    error: null,
    settings: { importExtraFiles: { value: false } },
    hasSettings: true,
    isWindows: true,
    onInputChange: () => {},
    onSavePress: () => {}
  });

  const tree = component.render();
  const checkbox = findByProp(tree, 'name', 'autoInitializeFormatSpecificAuthorPaths');
  const fieldset = findByProp(tree, 'legend', 'NewAuthorFolders');

  assert.ok(fieldset, 'the setting should be grouped with the new-author folder controls');
  assert.ok(checkbox, 'the setting should be visible when advanced settings are off');
  assert.equal(checkbox.props.value, false, 'format-specific path initialization is opt-in');
  assert.equal(checkbox.props.helpText, 'AutoInitializeFormatSpecificAuthorPathsHelpText');
});

test('new-author format-folder setting reflects the saved admin value', () => {
  const MediaManagement = loadMediaManagement();
  const component = new MediaManagement({
    advancedSettings: false,
    isFetching: false,
    error: null,
    settings: {
      importExtraFiles: { value: false },
      autoInitializeFormatSpecificAuthorPaths: { value: true }
    },
    hasSettings: true,
    isWindows: true,
    onInputChange: () => {},
    onSavePress: () => {}
  });

  const checkbox = findByProp(component.render(), 'name', 'autoInitializeFormatSpecificAuthorPaths');

  assert.equal(checkbox.props.value, true);
});

test('new-author format-folder help explains its server-wide admin scope', () => {
  const localizationPath = path.resolve(__dirname, '../../../../src/NzbDrone.Core/Localization/Core/en.json');
  const translations = JSON.parse(fs.readFileSync(localizationPath, 'utf8'));
  const helpText = translations.AutoInitializeFormatSpecificAuthorPathsHelpText;

  assert.match(helpText, /^Server-wide admin setting\./);
  assert.match(helpText, /every new author, regardless of who adds the author/);
  assert.match(helpText, /Existing author paths and files are not changed or moved\./);
});
