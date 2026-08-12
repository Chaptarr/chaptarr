import isEqual from 'lodash/isEqual';
import { createSelectorCreator, defaultMemoize } from 'reselect';

const createDeepEqualSelector = createSelectorCreator(
  defaultMemoize,
  isEqual
);

export default createDeepEqualSelector;
