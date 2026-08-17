
/** Base dictionary for the common namespace (the key-set source of truth). */
export const en = {
  'ok': 'OK',
  'cancel': 'Cancel',
  'close': 'Close',
  'copy': 'Copy',
  'copied': 'Copied',
  'retry': 'Retry',
  'loading': 'Loading…',
  'load.failed': 'Failed to load',
  'submit': 'Submit',
  'submitting': 'Submitting…',
  'next': 'Next',
  'previous': 'Previous',
  'skip': 'Skip',
  'delete': 'Delete',
  'edit': 'Edit',
  'save': 'Save',
  'search': 'Search',
  'more': 'More',
  'collapse': 'Collapse',
  'expand': 'Expand',
  'back': 'Back',
  'unknown': 'Unknown',
  'none': 'None',
  'truncated': 'Truncated',
} satisfies Record<string, string>

/** The common vocabulary key union. */
export type CommonKey = keyof typeof en
