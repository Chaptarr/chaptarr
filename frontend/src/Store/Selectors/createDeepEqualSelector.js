import isEqual from 'lodash/isEqual';
import { createSelectorCreator, lruMemoize } from 'reselect';

const createDeepEqualSelector = createSelectorCreator(
  lruMemoize,
  isEqual
);

export default createDeepEqualSelector;
