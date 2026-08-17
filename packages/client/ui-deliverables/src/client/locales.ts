/** `deliverables` namespace dictionaries. */

/** Dictionary namespace owned by this plugin. */
export const NS = 'deliverables'

/** English dictionary (the key-set source of truth). */
export const en = {
  'produced.label': 'Produced',
  'produced.moreOne': '+ 1 file',
  'produced.more': '+ {count} files',
  'produced.open': 'Open {name}',
  'produced.showInFolder': 'Show in folder',
  'touched.label': 'This turn',
  'touched.read': '{count} read',
  'touched.search': '{count} searched',
  'touched.execute': '{count} ran',
  'touched.fetch': '{count} fetched',
  'touched.delete': '{count} deleted',
  'touched.move': '{count} moved',
  'touched.other': '{count} other',
  'touched.failed': '{count} failed',
}

/** Union of this namespace's dictionary keys. */
export type DeliverablesKey = keyof typeof en
