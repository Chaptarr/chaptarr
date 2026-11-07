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

    setState(update) {
      this.state = { ...this.state, ...update };
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

function loadRootFolders(ajaxRequest) {
  const filePath = path.join(__dirname, 'RootFolders.js');
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

  function Stub() {
    return null;
  }

  function SpinnerButton() {
    return null;
  }

  function ConfirmModal() {
    return null;
  }

  const modules = {
    'prop-types': PropTypes,
    react: React,
    'Components/FieldSet': Stub,
    'Components/Icon': Stub,
    'Components/Link/Button': Stub,
    'Components/Link/SpinnerButton': SpinnerButton,
    'Components/Modal/ConfirmModal': ConfirmModal,
    'Components/Page/PageSectionContent': Stub,
    'Components/Table/Table': Stub,
    'Components/Table/TableBody': Stub,
    'Helpers/Props': { icons: { ADD: 'add' }, kinds: { PRIMARY: 'primary', DEFAULT: 'default' } },
    'Helpers/Props/folderTypes': {
      getRootFolderMediaTypeLabel: () => 'Both',
      getRootFolderMediaTypes: () => []
    },
    'Utilities/Array/sortByName': (items) => items,
    'Utilities/createAjaxRequest': ajaxRequest,
    'Utilities/String/translate': (key, args) => {
      return args ? `${key}(${args.join(',')})` : key;
    },
    './EditRootFolderModalConnector': Stub,
    './RootFolder': Stub,
    './RootFolders.css': new Proxy({}, { get: () => '' })
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

  const RootFolders = module.exports.default || module.exports;
  RootFolders.SpinnerButton = SpinnerButton;
  RootFolders.ConfirmModal = ConfirmModal;

  return RootFolders;
}

function findElement(tree, type) {
  if (!tree || typeof tree !== 'object') {
    return null;
  }

  if (tree.type === type) {
    return tree;
  }

  const children = tree.props?.children;
  const childList = Array.isArray(children) ? children : [children];

  for (const child of childList) {
    const match = findElement(child, type);

    if (match) {
      return match;
    }
  }

  return null;
}

function findElementWithProp(tree, name, value) {
  if (!tree || typeof tree !== 'object') {
    return null;
  }

  if (tree.props?.[name] === value) {
    return tree;
  }

  const children = tree.props?.children;
  const childList = Array.isArray(children) ? children : [children];

  for (const child of childList) {
    const match = findElementWithProp(child, name, value);

    if (match) {
      return match;
    }
  }

  return null;
}

function collectText(tree, values = []) {
  if (typeof tree === 'string' || typeof tree === 'number') {
    values.push(String(tree));
    return values;
  }

  if (!tree || typeof tree !== 'object') {
    return values;
  }

  const children = tree.props?.children;
  (Array.isArray(children) ? children : [children]).forEach((child) => collectText(child, values));
  return values;
}

function createAjaxHarness() {
  let resolveRequest = null;
  let rejectRequest = null;
  let requestOptions = null;
  let requestCount = 0;

  return {
    requestOptions: () => requestOptions,
    requestCount: () => requestCount,
    resolve: (value) => resolveRequest(value),
    reject: (error) => rejectRequest(error),
    create(options) {
      requestCount++;
      requestOptions = options;
      const request = new Promise((resolve, reject) => {
        resolveRequest = resolve;
        rejectRequest = reject;
      });

      return {
        request,
        abortRequest() {
          return false;
        }
      };
    }
  };
}

function createComponent(RootFolders, onBackfillAuthorRootFolders) {
  return new RootFolders({
    items: [],
    isFetching: false,
    isPopulated: true,
    isDeleting: false,
    isBackfillingAuthorRootFolders: false,
    qualityProfiles: [],
    metadataProfiles: [],
    onConfirmDeleteRootFolder: () => null,
    onBackfillAuthorRootFolders,
    setRootFolderValue: () => {}
  });
}

test('author root backfill reviews the dry-run counts and only runs after confirmation', async() => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  let backfillCount = 0;
  const component = createComponent(RootFolders, () => backfillCount++);

  component.onReviewAuthorRootBackfillPress();

  const spinnerButton = findElement(component.render(), RootFolders.SpinnerButton);
  assert.equal(spinnerButton.props.children, 'ReviewMissingAuthorRoots');
  assert.equal(spinnerButton.props.isSpinning, true);

  assert.deepEqual(JSON.parse(JSON.stringify(ajax.requestOptions())), {
    url: '/author/editor/backfill-roots?dryRun=true',
    method: 'POST',
    dataType: 'json'
  });
  assert.equal(component.state.isPreviewingAuthorRoots, true);

  ajax.resolve({
    candidates: 5,
    updated: 2,
    skippedManuallyOverridden: 1,
    skippedNoDefault: 1,
    skippedNoProfile: 1
  });
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(component.state.isBackfillPreviewModalOpen, true);
  assert.equal(component.state.backfillPreview.updated, 2);
  assert.equal(backfillCount, 0);

  const previewModal = findElement(component.render(), RootFolders.ConfirmModal);
  assert.equal(previewModal.props.title, 'BackfillAuthorRootFoldersTitle');
  assert.equal(previewModal.props.confirmLabel, 'FillMissingAuthorRoots');
  assert.equal(previewModal.props.hideCancelButton, false);

  const previewText = collectText(component.renderAuthorRootBackfillPreview()).join(' ');
  assert.match(previewText, /BackfillAuthorRootFoldersSummary\(2,5\)/);
  assert.match(previewText, /BackfillAuthorRootFoldersHelpText/);

  component.onConfirmAuthorRootBackfill();
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(backfillCount, 1);
  assert.equal(component.state.isBackfillPreviewModalOpen, false);
});

test('author root backfill cannot run when the preview has no eligible authors', () => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  let backfillCount = 0;
  const component = createComponent(RootFolders, () => backfillCount++);

  component.state.backfillPreview = {
    candidates: 2,
    updated: 0,
    skippedManuallyOverridden: 1,
    skippedNoDefault: 1,
    skippedNoProfile: 0
  };
  component.state.isBackfillPreviewModalOpen = true;

  const previewModal = findElement(component.render(), RootFolders.ConfirmModal);
  assert.equal(previewModal.props.confirmLabel, 'Close');
  assert.equal(previewModal.props.hideCancelButton, true);

  component.onConfirmAuthorRootBackfill();

  assert.equal(backfillCount, 0);
  assert.equal(component.state.isBackfillPreviewModalOpen, false);
});

test('author root backfill cannot run when the preview count is invalid', () => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  let backfillCount = 0;
  const component = createComponent(RootFolders, () => backfillCount++);

  for (const preview of [
    { candidates: 2, updated: 'unknown' },
    { candidates: 'unknown', updated: 1 },
    { candidates: 1, updated: 2 },
    { candidates: 2, updated: 1.5 }
  ]) {
    component.state.backfillPreview = preview;
    component.state.isBackfillPreviewModalOpen = true;

    const previewModal = findElement(component.render(), RootFolders.ConfirmModal);
    assert.equal(previewModal.props.confirmLabel, 'Close');
    assert.equal(previewModal.props.hideCancelButton, true);

    component.onConfirmAuthorRootBackfill();

    assert.equal(backfillCount, 0);
    assert.equal(component.state.isBackfillPreviewModalOpen, false);
  }
});

test('author root backfill prevents duplicate queueing and keeps a retry after queue failure', async() => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  let backfillCount = 0;
  const component = createComponent(RootFolders, () => {
    backfillCount++;

    return backfillCount === 1 ?
      Promise.reject(new Error('offline')) :
      Promise.resolve();
  });

  component.state.backfillPreview = {
    candidates: 3,
    updated: 2,
    skippedManuallyOverridden: 0,
    skippedNoDefault: 1,
    skippedNoProfile: 0
  };
  component.state.isBackfillPreviewModalOpen = true;

  component.onConfirmAuthorRootBackfill();
  component.onConfirmAuthorRootBackfill();
  component.onBackfillPreviewModalClose();
  assert.equal(backfillCount, 0);
  assert.equal(component.state.isBackfillPreviewModalOpen, true);

  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(backfillCount, 1);
  assert.equal(component.state.isBackfillPreviewModalOpen, true);
  assert.equal(component.state.backfillCommandError, 'UnableToQueueAuthorRootFolderBackfill');
  assert.equal(component.state.isSubmittingAuthorRootBackfill, false);
  const previewModal = findElement(component.render(), RootFolders.ConfirmModal);
  assert.ok(findElementWithProp(previewModal.props.message, 'role', 'alert'));

  component.onBackfillPreviewModalClose();
  assert.equal(component.state.isBackfillPreviewModalOpen, false);

  component.onReviewAuthorRootBackfillPress();
  assert.equal(component.state.backfillCommandError, null);
  ajax.resolve({
    candidates: 3,
    updated: 2,
    skippedManuallyOverridden: 0,
    skippedNoDefault: 1,
    skippedNoProfile: 0
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(component.state.isBackfillPreviewModalOpen, true);

  component.onConfirmAuthorRootBackfill();
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(backfillCount, 2);
  assert.equal(component.state.isBackfillPreviewModalOpen, false);
  assert.equal(component.state.backfillCommandError, null);
});

test('author root backfill shows a retryable preview error', async() => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  const component = createComponent(RootFolders, () => null);

  component.onReviewAuthorRootBackfillPress();
  ajax.reject(new Error('offline'));
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(component.state.isPreviewingAuthorRoots, false);
  assert.equal(component.state.backfillPreviewError, 'UnableToLoadAuthorRootFolderBackfillPreview');

  const error = findElementWithProp(component.render(), 'role', 'alert');
  assert.ok(error);
  assert.equal(
    findElement(component.render(), RootFolders.SpinnerButton).props.children,
    'RetryAuthorRootFolderBackfillPreview'
  );
});

test('author root backfill does not issue duplicate preview requests', () => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  const component = createComponent(RootFolders, () => null);

  component.onReviewAuthorRootBackfillPress();
  component.onReviewAuthorRootBackfillPress();

  assert.equal(ajax.requestCount(), 1);
});

test('author root backfill rejects malformed preview counts and offers a retry', async() => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  const component = createComponent(RootFolders, () => null);

  component.onReviewAuthorRootBackfillPress();
  ajax.resolve({
    candidates: 2,
    updated: 1,
    skippedManuallyOverridden: 0,
    skippedNoDefault: 0,
    skippedNoProfile: 0
  });
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(component.state.isBackfillPreviewModalOpen, false);
  assert.equal(component.state.backfillPreview, null);
  assert.equal(component.state.backfillPreviewError, 'UnableToLoadAuthorRootFolderBackfillPreview');
  assert.equal(
    findElement(component.render(), RootFolders.SpinnerButton).props.children,
    'RetryAuthorRootFolderBackfillPreview'
  );
});

test('author root backfill blocks same-turn duplicate queueing before React commits state', async() => {
  const ajax = createAjaxHarness();
  const RootFolders = loadRootFolders(ajax.create);
  let backfillCount = 0;
  const component = createComponent(RootFolders, () => {
    backfillCount++;
    return Promise.resolve();
  });
  const setState = component.setState.bind(component);

  component.state.backfillPreview = {
    candidates: 1,
    updated: 1,
    skippedManuallyOverridden: 0,
    skippedNoDefault: 0,
    skippedNoProfile: 0
  };
  component.state.isBackfillPreviewModalOpen = true;
  component.setState = (update) => {
    if (update.isSubmittingAuthorRootBackfill === true) {
      return;
    }

    setState(update);
  };

  component.onConfirmAuthorRootBackfill();
  component.onConfirmAuthorRootBackfill();
  component.onBackfillPreviewModalClose();

  assert.equal(component.state.isBackfillPreviewModalOpen, true);
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(backfillCount, 1);
  assert.equal(component.state.isBackfillPreviewModalOpen, false);
});
