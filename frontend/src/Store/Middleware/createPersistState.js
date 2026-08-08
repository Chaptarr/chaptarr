import _ from 'lodash';
import persistState from 'redux-localstorage';
import actions from 'Store/Actions';
import migrate from 'Store/Migrators/migrate';

const columnPaths = [];

const paths = _.reduce([...actions], (acc, action) => {
  if (action.persistState) {
    action.persistState.forEach((path) => {
      if (typeof path === 'string' && path.toLowerCase().endsWith('columns')) {
        columnPaths.push(path);
      }

      acc.push(path);
    });
  }

  return acc;
}, []);

function mergeColumns(path, initialState, persistedState, computedState) {
  const initialColumns = _.get(initialState, path);
  const persistedColumns = _.get(persistedState, path);

  if (!Array.isArray(initialColumns) || !Array.isArray(persistedColumns) || !persistedColumns.length) {
    return;
  }

  const columns = [];
  const seenNames = new Set();

  // Add persisted columns in the same order they're currently in
  // as long as they haven't been removed.

  persistedColumns.forEach((persistedColumn) => {
    if (!persistedColumn || !persistedColumn.name || seenNames.has(persistedColumn.name)) {
      return;
    }

    seenNames.add(persistedColumn.name);

    const column = initialColumns.find((i) => i.name === persistedColumn.name);

    if (column) {
      const newColumn = {};

      // We can't use a spread operator or Object.assign to clone the column
      // or any accessors are lost and can break translations.
      for (const prop of Object.keys(column)) {
        Object.defineProperty(newColumn, prop, Object.getOwnPropertyDescriptor(column, prop));
      }

      newColumn.isVisible = persistedColumn.isVisible;

      columns.push(newColumn);
    }
  });

  // Add any columns added to the app in the initial position.
  initialColumns.forEach((initialColumn, index) => {
    const persistedColumnIndex = persistedColumns.findIndex((i) => i.name === initialColumn.name);
    const column = Object.assign({}, initialColumn);

    if (persistedColumnIndex === -1) {
      columns.splice(index, 0, column);
    }
  });

  _.set(computedState, path, columns);
}

function slicer(paths_) {
  return (state) => {
    const subset = {};

    paths_.forEach((path) => {
      _.set(subset, path, _.get(state, path));
    });

    return subset;
  };
}

function serialize(obj) {
  return JSON.stringify(obj, null, 2);
}

function merge(initialState, persistedState) {
  if (!persistedState) {
    return initialState;
  }

  migrate(persistedState);

  const computedState = {};

  _.merge(computedState, initialState, persistedState);

  columnPaths.forEach((columnPath) => {
    mergeColumns(columnPath, initialState, persistedState, computedState);
  });

  return computedState;
}

function tryMigrateLocalStorageKey(fromKey, toKey) {
  try {
    if (typeof window === 'undefined' || !window.localStorage) {
      return;
    }

    if (window.localStorage.getItem(toKey) !== null) {
      return;
    }

    const legacyState = window.localStorage.getItem(fromKey);
    if (legacyState === null) {
      return;
    }

    window.localStorage.setItem(toKey, legacyState);
  } catch (e) {
    // Best-effort only: localStorage may be unavailable/blocked in some environments.
  }
}

const config = {
  slicer,
  serialize,
  merge,
  key: 'chaptarr'
};

export default function createPersistState() {
  tryMigrateLocalStorageKey('readarr', config.key);
  tryMigrateLocalStorageKey('audioarr', config.key);
  return persistState(paths, config);
}
