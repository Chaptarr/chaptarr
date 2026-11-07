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

    setState(update, callback) {
      this.state = { ...this.state, ...update };

      if (callback) {
        return callback();
      }

      return undefined;
    }
  },
  Fragment: Symbol.for('react.fragment'),
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

function loadConnector(fileName, extraModules = {}) {
  const filePath = path.join(__dirname, fileName);
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
    'react-redux': {
      connect: () => (component) => component
    },
    'lodash/map': (items, mapper) => items.map(mapper),
    'Utilities/Array/sortByName': (items) => items,
    'Utilities/createAjaxRequest': () => {
      const request = {
        then() {
          return this;
        },
        catch() {
          return this;
        }
      };

      return {
        request,
        abortRequest() {
          return false;
        }
      };
    },
    'Utilities/String/translate': (key) => key,
    'Helpers/Props': { metadataProfileNames: { NONE: 'None' } },
    './SelectInput': function SelectInput() {
      return null;
    },
    ...extraModules
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

function toPlainValue(value) {
  return JSON.parse(JSON.stringify(value));
}

test('author quality profile can reset to its root folder default as a nullable API value', () => {
  const QualityProfileSelectInputConnector = loadConnector('QualityProfileSelectInputConnector.js');
  const changes = [];
  const connector = new QualityProfileSelectInputConnector({
    name: 'audiobookQualityProfileId',
    value: null,
    includeNone: true,
    noneValue: null,
    noneLabel: 'Use root folder defaults',
    includeNoChange: false,
    includeMixed: false,
    onChange: (change) => changes.push(change)
  });

  connector.state = {
    profiles: [{ id: 41, name: 'Audiobook Default' }],
    isFetching: false
  };

  assert.equal(connector.render().props.value, 'none');
  assert.deepEqual(toPlainValue(connector.getSelectValues().map((item) => item.key)), ['none', '41']);

  connector.onChange({ name: 'audiobookQualityProfileId', value: 'none' });
  connector.onChange({ name: 'audiobookQualityProfileId', value: '41' });

  assert.deepEqual(toPlainValue(changes), [
    { name: 'audiobookQualityProfileId', value: null },
    { name: 'audiobookQualityProfileId', value: 41 }
  ]);
});

test('quality profile reset is unavailable when the selected root folder has no default', () => {
  const QualityProfileSelectInputConnector = loadConnector('QualityProfileSelectInputConnector.js');
  const connector = new QualityProfileSelectInputConnector({
    name: 'ebookQualityProfileId',
    value: null,
    includeNone: true,
    noneValue: null,
    noneLabel: 'No root folder default configured',
    noneDisabled: true,
    includeNoChange: false,
    includeMixed: false,
    onChange: () => {}
  });

  connector.state = { profiles: [], isFetching: false };

  assert.deepEqual(toPlainValue(connector.getSelectValues()[0]), {
    key: 'none',
    value: 'No root folder default configured',
    isDisabled: true
  });
});

test('existing quality profile selectors retain their none sentinel by default', () => {
  const QualityProfileSelectInputConnector = loadConnector('QualityProfileSelectInputConnector.js');
  const changes = [];
  const connector = new QualityProfileSelectInputConnector({
    name: 'qualityProfileId',
    includeNone: true,
    noneValue: 'none',
    includeNoChange: false,
    includeMixed: false,
    onChange: (change) => changes.push(change)
  });

  connector.onChange({ name: 'qualityProfileId', value: 'none' });

  assert.deepEqual(toPlainValue(changes), [{ name: 'qualityProfileId', value: 'none' }]);
});

test('author metadata profile can reset to root defaults without selecting a different profile', () => {
  const MetadataProfileSelectInputConnector = loadConnector('MetadataProfileSelectInputConnector.js');
  const changes = [];
  const connector = new MetadataProfileSelectInputConnector({
    name: 'ebookMetadataProfileId',
    value: null,
    includeNone: true,
    includeRootFolderDefault: true,
    rootFolderDefaultLabel: 'Use root folder defaults',
    includeNoChange: false,
    includeMixed: false,
    onChange: (change) => changes.push(change)
  });

  connector.state = {
    profiles: [
      { id: 17, name: 'Fiction' },
      { id: 18, name: 'None' }
    ],
    isFetching: false
  };

  assert.equal(connector.render().props.value, 'rootFolderDefault');
  assert.deepEqual(toPlainValue(connector.getSelectValues().map((item) => item.key)), ['rootFolderDefault', '17', '18']);

  connector.checkAndSetDefaultValue();
  connector.onChange({ name: 'ebookMetadataProfileId', value: 'rootFolderDefault' });
  connector.onChange({ name: 'ebookMetadataProfileId', value: '17' });

  assert.deepEqual(toPlainValue(changes), [
    { name: 'ebookMetadataProfileId', value: null },
    { name: 'ebookMetadataProfileId', value: 17 }
  ]);
});

test('metadata profile root-default reset is disabled when the selected folder has no default', () => {
  const MetadataProfileSelectInputConnector = loadConnector('MetadataProfileSelectInputConnector.js');
  const connector = new MetadataProfileSelectInputConnector({
    name: 'ebookMetadataProfileId',
    value: null,
    includeNone: true,
    includeRootFolderDefault: true,
    rootFolderDefaultLabel: 'No root folder default configured',
    rootFolderDefaultDisabled: true,
    includeNoChange: false,
    includeMixed: false,
    onChange: () => {}
  });

  connector.state = { profiles: [], isFetching: false };

  assert.deepEqual(toPlainValue(connector.getSelectValues()[0]), {
    key: 'rootFolderDefault',
    value: 'No root folder default configured',
    isDisabled: true
  });
});

test('root folder profile-default lookup is media-type specific and rejects missing IDs', () => {
  const { hasRootFolderProfileDefault } = loadConnector(
    '../../Settings/MediaManagement/RootFolder/rootFolderDefaultUtils.js',
    {
      'Helpers/Props/folderTypes': {
        getRootFolderMediaTypes: (rootFolder) => rootFolder.mediaTypes || []
      }
    }
  );

  assert.equal(hasRootFolderProfileDefault({ audiobookQualityProfileId: 41 }, 'audiobook', 'quality'), true);
  assert.equal(hasRootFolderProfileDefault({ ebook: { metadataProfileId: 18 } }, 'ebook', 'metadata'), true);
  assert.equal(hasRootFolderProfileDefault({ audiobookQualityProfileId: null }, 'audiobook', 'quality'), false);
  assert.equal(hasRootFolderProfileDefault({ ebookMetadataProfileId: 0 }, 'ebook', 'metadata'), false);
  assert.equal(hasRootFolderProfileDefault({ ebookMetadataProfileId: 1.5 }, 'ebook', 'metadata'), false);
  assert.equal(hasRootFolderProfileDefault({ audiobookQualityProfileId: 41 }, 'ebook', 'quality'), false);
});

test('author editor keeps missing format defaults visible without offering a destructive reset', () => {
  const rootFolderUtils = loadConnector(
    '../../Settings/MediaManagement/RootFolder/rootFolderDefaultUtils.js',
    {
      'Helpers/Props/folderTypes': {
        getRootFolderMediaTypes: (rootFolder) => rootFolder.mediaTypes || []
      }
    }
  );
  const EditAuthorModalContent = loadConnector('../../Author/Edit/EditAuthorModalContent.js', {
    'AddAuthor/AuthorMetadataProfilePopoverContent': 'AuthorMetadataProfilePopoverContent',
    'AddAuthor/AuthorMonitoringGatePopoverContent': 'AuthorMonitoringGatePopoverContent',
    'AddAuthor/AuthorMonitorNewItemsOptionsPopoverContent': 'AuthorMonitorNewItemsOptionsPopoverContent',
    'Author/MoveAuthor/MoveAuthorModal': 'MoveAuthorModal',
    'Components/Alert': 'Alert',
    'Components/Form/Form': 'Form',
    'Components/Form/FormGroup': 'FormGroup',
    'Components/Form/FormInputGroup': 'FormInputGroup',
    'Components/Form/FormLabel': 'FormLabel',
    'Components/Form/MediaTypeToggle': 'MediaTypeToggle',
    'Components/Icon': 'Icon',
    'Components/Link/Button': 'Button',
    'Components/Link/SpinnerButton': 'SpinnerButton',
    'Components/Modal/ModalBody': 'ModalBody',
    'Components/Modal/ModalContent': 'ModalContent',
    'Components/Modal/ModalFooter': 'ModalFooter',
    'Components/Modal/ModalHeader': 'ModalHeader',
    'Components/Tooltip/Popover': 'Popover',
    'Helpers/Props': {
      icons: { INFO: 'info' },
      inputTypes: {
        CHECK: 'check',
        SELECT: 'select',
        ROOT_FOLDER_SELECT: 'rootFolderSelect',
        QUALITY_PROFILE_SELECT: 'qualityProfileSelect',
        METADATA_PROFILE_SELECT: 'metadataProfileSelect',
        PATH: 'path',
        TAG: 'tag'
      },
      kinds: { DANGER: 'danger' },
      tooltipPositions: { RIGHT: 'right' }
    },
    'Helpers/Props/folderTypes': {
      coerceFolderType: (folderType) => folderType,
      FolderType: { Mixed: 0, Audiobook: 1, Ebook: 2 }
    },
    'Utilities/Author/monitorNewItemsOptions': {
      __esModule: true,
      default: [],
      normalizeMonitorNewItemsOption: (value) => value
    },
    'Utilities/Object/getErrorMessage': () => 'Unable to save',
    'Utilities/String/translate': (key) => key,
    'Settings/MediaManagement/RootFolder/rootFolderDefaultUtils': {
      __esModule: true,
      ...rootFolderUtils
    },
    './AuthorMediaMoveControl': 'AuthorMediaMoveControl',
    './EditAuthorModalContent.css': new Proxy({}, {
      get(_target, property) {
        return property.toString();
      }
    })
  });

  const item = {
    audiobookMonitored: { value: true },
    audiobookMonitorNewItems: { value: 'all' },
    ebookMonitored: { value: true },
    ebookMonitorNewItems: { value: 'all' },
    syncMonitoredAcrossFormats: { value: false },
    audiobookQualityProfileId: { value: 41 },
    ebookQualityProfileId: { value: 17 },
    audiobookMetadataProfileId: { value: 12 },
    ebookMetadataProfileId: { value: 18 },
    audiobookRootFolderPath: { value: '/books/audio' },
    ebookRootFolderPath: { value: '/books/ebooks' },
    audiobookPath: { value: '/books/audio/author' },
    ebookPath: { value: '/books/ebooks/author' },
    path: { value: '/books/audio/author' },
    tags: { value: [] },
    audiobookTags: { value: [] },
    ebookTags: { value: [] }
  };

  const props = {
    authorId: 1,
    authorName: 'Example Author',
    item,
    rootFolders: [
      {
        path: '/books/audio/',
        folderType: 1,
        audiobookQualityProfileId: 41,
        audiobookMetadataProfileId: null
      },
      {
        path: '/books/ebooks',
        folderType: 2,
        ebookQualityProfileId: 17,
        ebookMetadataProfileId: 18
      }
    ],
    hasAudiobookRootFolder: true,
    hasEbookRootFolder: true,
    showMetadataProfile: true,
    isSaving: false,
    saveError: null,
    isWindows: false,
    onInputChange: () => {},
    onModalClose: () => {},
    onSavePress: () => {},
    onDeleteAuthorPress: () => {}
  };

  const findProfileInput = (tree, name) => {
    const elements = [];
    const visit = (element) => {
      if (Array.isArray(element)) {
        element.forEach(visit);
      } else if (element && typeof element === 'object') {
        if (element.type === 'FormInputGroup' && element.props.name === name) {
          elements.push(element);
        }

        visit(element.props?.children);
      }
    };

    visit(tree);
    return elements[0];
  };

  const authorEditor = new EditAuthorModalContent(props);
  let tree = authorEditor.render();
  let input = findProfileInput(tree, 'audiobookQualityProfileId');

  assert.equal(input.props.noneDisabled, false);
  assert.equal(input.props.noneLabel, 'UseRootFolderDefaults');

  input = findProfileInput(tree, 'audiobookMetadataProfileId');
  assert.equal(input.props.rootFolderDefaultDisabled, true);
  assert.equal(input.props.rootFolderDefaultLabel, 'NoRootFolderProfileDefault');
  assert.equal(input.props.helpText, 'RootFolderProfileDefaultMissingHelpText');

  authorEditor.onMediaTypeChange('ebook');
  tree = authorEditor.render();
  input = findProfileInput(tree, 'ebookQualityProfileId');

  assert.equal(input.props.noneDisabled, false);
  assert.equal(input.props.noneLabel, 'UseRootFolderDefaults');

  input = findProfileInput(tree, 'ebookMetadataProfileId');
  assert.equal(input.props.rootFolderDefaultDisabled, false);
  assert.equal(input.props.rootFolderDefaultLabel, 'UseRootFolderDefaults');
});
