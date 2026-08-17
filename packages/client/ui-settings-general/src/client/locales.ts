/** Shell chrome and General-nav dictionaries; feature rows own their copy. */

/** The settings namespace key union. */
export type SettingsKey = keyof typeof en

/** English dictionary, the key-set source of truth. */
export const en = {
  'trigger': 'Settings',
  'title': 'Settings',
  'close': 'Close',
  'openDocument': 'Open configuration file',
  'openDocument.error': 'Could not open configuration file',
  'general.nav': 'General',
} satisfies Record<string, string>
