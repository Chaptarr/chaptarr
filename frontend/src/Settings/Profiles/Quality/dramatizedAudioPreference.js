import translate from 'Utilities/String/translate';

export const DRAMATIZED_AUDIO_FORMAT_NAME = 'Dramatized / Full-Cast Audio';
export const DRAMATIZED_AUDIO_FORMAT_KEY = 'dramatized-full-cast-audio';
export const PREFER_AUDIO_PRODUCTION_SCORE = 50;
export const PREFER_STANDARD_AUDIO_SCORE = -50;
export const REJECT_DRAMATIZED_AUDIO_SCORE = -10000;

export const SELECTED_NARRATOR_FORMAT_NAME = 'Selected Audiobook Narrators';
export const SELECTED_NARRATOR_FORMAT_KEY = 'preferred-narrator';
export const SELECTED_NARRATOR_SCORE = 50;

export const easyCustomFormatPresetModes = {
  PREFER_SELECTED_NARRATOR: 'preferSelectedNarrator',
  PREFER_STANDARD: 'preferStandard',
  PREFER_DRAMATIZED: 'preferDramatized',
  REJECT_DRAMATIZED: 'rejectDramatized',
  RESTORE_DEFAULTS: 'restoreDefaults'
};

export const easyCustomFormatPresetOptions = [
  {
    key: '',
    get value() {
      return translate('ChoosePreset');
    }
  },
  {
    key: easyCustomFormatPresetModes.PREFER_SELECTED_NARRATOR,
    get value() {
      return translate('PreferSelectedNarratorPreset');
    }
  },
  {
    key: easyCustomFormatPresetModes.PREFER_DRAMATIZED,
    get value() {
      return translate('PreferDramatizedFullCastPreset');
    }
  },
  {
    key: easyCustomFormatPresetModes.PREFER_STANDARD,
    get value() {
      return translate('PreferStandardNarrationPreset');
    }
  },
  {
    key: easyCustomFormatPresetModes.REJECT_DRAMATIZED,
    get value() {
      return translate('RejectDramatizedFullCastPreset');
    }
  },
  {
    key: easyCustomFormatPresetModes.RESTORE_DEFAULTS,
    get value() {
      return translate('RestoreAudiobookPreferenceDefaults');
    }
  }
];

export function applyEasyCustomFormatPreset(formatItems, mode, minFormatScore = 0) {
  let nextMinFormatScore = minFormatScore;
  const nextFormatItems = (formatItems || []).map((item) => {
    const isSelectedNarrator = item.builtInKey === SELECTED_NARRATOR_FORMAT_KEY ||
      item.name === SELECTED_NARRATOR_FORMAT_NAME;
    const isDramatized = item.builtInKey === DRAMATIZED_AUDIO_FORMAT_KEY ||
      item.name === DRAMATIZED_AUDIO_FORMAT_NAME;

    if (isSelectedNarrator &&
        (mode === easyCustomFormatPresetModes.PREFER_SELECTED_NARRATOR ||
         mode === easyCustomFormatPresetModes.RESTORE_DEFAULTS)) {
      return {
        ...item,
        score: SELECTED_NARRATOR_SCORE
      };
    }

    if (isDramatized) {
      if (mode === easyCustomFormatPresetModes.PREFER_DRAMATIZED) {
        return { ...item, score: PREFER_AUDIO_PRODUCTION_SCORE };
      }

      if (mode === easyCustomFormatPresetModes.PREFER_STANDARD) {
        return { ...item, score: PREFER_STANDARD_AUDIO_SCORE };
      }

      if (mode === easyCustomFormatPresetModes.REJECT_DRAMATIZED) {
        nextMinFormatScore = Math.max(nextMinFormatScore, 0);
        return { ...item, score: REJECT_DRAMATIZED_AUDIO_SCORE };
      }

      if (mode === easyCustomFormatPresetModes.RESTORE_DEFAULTS) {
        return { ...item, score: 0 };
      }
    }

    return item;
  });

  return {
    formatItems: nextFormatItems,
    minFormatScore: nextMinFormatScore
  };
}
