/**
 * Keyboard-accessible disclosure attributes shared by the collapsible rows and
 * cards: the row is a button that toggles on click, Enter, and Space.
 */
import type { KeyboardEvent } from 'react'

/** Attributes to spread onto a clickable disclosure header. */
export interface DisclosureAttributes {
  role: 'button'
  tabIndex: 0
  'aria-expanded': boolean
  onClick: () => void
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void
}

/**
 * Build the disclosure attributes for one header.
 * @param expanded - current open state.
 * @param toggle - flips the state.
 * @returns the attributes to spread.
 */
export function disclosure(expanded: boolean, toggle: () => void): DisclosureAttributes {
  return {
    role: 'button',
    tabIndex: 0,
    'aria-expanded': expanded,
    onClick: toggle,
    onKeyDown: (event) => {
      if (event.key !== 'Enter' && event.key !== ' ') return
      event.preventDefault()
      toggle()
    },
  }
}
