export const DEFAULT_LANG = 'en_US'

const dict = {
  // main.ts
  'Starting BookshelfNG!': 0,
  'Web Interface': 1,
  'The web interface is ready': 2,
  'The web interface is not ready': 3,

  // interfaces.ts
  'Web UI': 4,
  'The web interface of BookshelfNG': 5,

  // actions/configureMigration.ts
  'Configure BookshelfNG migration': 6,
  'Choose whether the existing library is converted automatically on first startup.': 7,
  'Automatically migrate BookshelfNG data on startup': 8,
  'When disabled, ChaptarrNG waits for you to run the documented manual migration.': 9,
} as const

/**
 * Plumbing. DO NOT EDIT.
 */
export type I18nKey = keyof typeof dict
export type LangDict = Record<(typeof dict)[I18nKey], string>
export default dict
