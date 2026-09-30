import type { IconProps } from './icons/props.ts'
import { CortexMark } from './CortexMark.tsx'

const WIDTH = 116
const NAME_X = 29

/** Cortex wordmark presentation, including independently slotted marks. */
export interface BrandWordmarkProps extends IconProps {
  /** Include the leading Cortex node mark; omit it when a separate slot supplies the mark. */
  includeMark?: boolean | undefined
}

/**
 * Render the Cortex name and optional node mark in the surrounding text color.
 * @param props - Size, class, and leading-mark visibility.
 * @returns the decorative Cortex wordmark.
 */
export function BrandWordmark({ size = 24, className, includeMark = true }: BrandWordmarkProps) {
  const width = includeMark ? WIDTH : WIDTH - NAME_X
  return (
    <svg
      width={(size * width) / 24}
      height={size}
      className={className}
      viewBox={`${includeMark ? 0 : NAME_X} 0 ${width} 24`}
      fill="none"
      aria-hidden="true"
    >
      {includeMark && <CortexMark />}
      <text
        x={NAME_X}
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
