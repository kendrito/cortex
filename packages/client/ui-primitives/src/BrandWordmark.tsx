// Cortex brand wordmark: the mark plus the product name, laid out on the same
// 24px height the sidebar reserves. The name is set in the interface font
// rather than traced to paths, so it stays editable and inherits the app's
// type stack. Ink rides currentColor.

import type { IconProps } from './icons/props.ts'

/** Native wordmark width at the default 24px height. */
const WIDTH = 116

/**
 * Render the full brand wordmark.
 * @param props.size - height in px (default 24; width keeps the 116:24 ratio).
 * @param props.className - extra class for layout placement.
 * @returns the wordmark svg (aria-hidden decorative brand art).
 */
export function BrandWordmark({ size = 24, className }: IconProps) {
  return (
    <svg
      width={(size * WIDTH) / 24}
      height={size}
      className={className}
      viewBox={`0 0 ${WIDTH} 24`}
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
      <text
        x="29"
        y="17"
        fill="currentColor"
        fontSize="15.5"
        fontWeight="600"
        letterSpacing="-0.3"
        fontFamily="-apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif"
      >
        cortex
      </text>
    </svg>
  )
}
