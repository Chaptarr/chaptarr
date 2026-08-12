import find from 'lodash/find';
import isArray from 'lodash/isArray';
import { createSelector } from 'reselect';
import selectSettings from 'Store/Selectors/selectSettings';

function createProviderSettingsSelector(sectionName) {
  return createSelector(
    (state, { id }) => id,
    (state) => state.settings && state.settings[sectionName] ? state.settings[sectionName] : {},
    (id, section) => {
      if (!section) {
        console.warn(`createProviderSettingsSelector: section is undefined for ${sectionName}`);
        return {
          isFetching: false,
          isPopulated: false,
          error: null,
          isSaving: false,
          saveError: null,
          isTesting: false,
          successMessages: [],
          pendingChanges: {},
          item: { name: '' }
        };
      }

      if (!id) {
        const item = isArray(section.schema) ? section.selectedSchema : (section.schema || {});
        const settings = selectSettings(Object.assign({ name: '' }, item), section.pendingChanges, section.saveError);

        const isFetching = section.isSchemaFetching ?? section.isFetching ?? false;
        const isPopulated = section.isSchemaPopulated ?? section.isPopulated ?? false;
        const error = section.schemaError ?? section.error ?? null;

        const {
          isSaving,
          saveError,
          isTesting,
          isDeleting,
          deleteError,
          pendingChanges
        } = section;

        return {
          isFetching,
          isPopulated,
          error,
          isSaving,
          saveError,
          isTesting,
          isDeleting,
          deleteError,
          pendingChanges,
          successMessages: section.successMessages || [],
          ...settings,
          item: settings.settings
        };
      }

      const {
        isFetching,
        isPopulated,
        error,
        isSaving,
        saveError,
        isTesting,
        isDeleting,
        deleteError,
        pendingChanges,
        items = []
      } = section;

      const foundItem = find(items, { id });
      
      // Debug logging
      if (id && !foundItem) {
        console.warn(`createProviderSettingsSelector: No item found with id ${id} in ${sectionName}. Items:`, items);
      }
      
      const settings = selectSettings(foundItem, pendingChanges, saveError);

      return {
        isFetching,
        isPopulated,
        error,
        isSaving,
        saveError,
        isTesting,
        isDeleting,
        deleteError,
        successMessages: section.successMessages || [],
        ...settings,
        item: settings.settings
      };
    }
  );
}

export default createProviderSettingsSelector;
