/** Durable settings namespace for product-wide GUI onboarding facts. */
export const WELCOME_NOTICE_SETTINGS_NAMESPACE = 'ui-onboarding'

/** Field storing the last welcome notice version the user acknowledged. */
export const WELCOME_NOTICE_ACK_FIELD = 'welcomeNoticeVersion'

/**
 * Bump only when the notice changes materially and every user should see it
 * again. The acknowledgement is compared for exact equality.
 */
export const WELCOME_NOTICE_VERSION = '2026-08-13.1'

/** The complete editable internal-testing notice for the GUI. */
export const WELCOME_NOTICE_COPY = {
  en: {
    title: 'Internal Testing Notice',
    body: "Cortex 0.1 remains in testing for Harness developers. Many areas need further improvement, and we welcome feedback from the developer community. Cortex's core plugins and foundational APIs will continue to evolve rapidly over the coming months.\n\nWe look forward to exploring the limits of intelligence with developers around the world, building on open-source, open, reusable, and composable infrastructure. We welcome Harness developers everywhere to join the Cortex plugin ecosystem.",
    continueLabel: 'Continue',
  },
} as const
