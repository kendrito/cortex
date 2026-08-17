/** `feedback` namespace dictionaries. */

/** The feedback namespace key union. */
export type MessageFeedbackKey = keyof typeof en

declare module '@cortex/client-ui-slots' {
  interface LocaleNamespaceMap {
    /** The per-message feedback controls' copy. */
    feedback: MessageFeedbackKey
  }
}

/** English dictionary, the key-set source of truth. */
export const en = {
  'action.like': 'Good response',
  'action.likeActive': 'Remove rating',
  'action.dislike': 'Bad response',
  'action.dislikeActive': 'Remove rating',
  'note.open': 'Add a note',
  'note.placeholder': 'What was good, or what went wrong? (optional)',
  'note.save': 'Save',
  'note.cancel': 'Cancel',
  'note.aria': 'Feedback note',
  'error.conflict': 'This feedback changed elsewhere; the latest state is shown',
  'error.load': 'Could not load feedback',
  'error.generic': 'Could not save feedback',
} satisfies Record<string, string>
