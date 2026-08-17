// Cortex mark: a hub with three satellites, drawn on a 24x24 grid so the
// geometry stays crisp at the 24px rail size and the 34px hero size. Ink rides
// currentColor so one asset serves both themes.

import type { IconProps } from './icons/props.ts'

/**
 * Render the Cortex mark.
 * @param props.size - width in px (default 24; the mark is square).
 * @param props.className - extra class for layout placement.
 * @returns the mark svg (aria-hidden; pair with the wordmark for accessibility).
 */
export function CortexMark({ size = 24, className }: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      aria-hidden="true"
    >
      <path
        d="M12 6.4V9.2M16.85 15.1L14.42 13.7M7.15 15.1L9.58 13.7"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
      <circle cx="12" cy="11.6" r="2.9" fill="currentColor" />
      <circle cx="12" cy="4.3" r="2.1" fill="currentColor" />
      <circle cx="18.3" cy="16.3" r="2.1" fill="currentColor" />
      <circle cx="5.7" cy="16.3" r="2.1" fill="currentColor" />
    </svg>
  )
}
