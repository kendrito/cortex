/** Small outline glyphs matching native Studio's Fluent icons. */
const PATHS = {
  tests: 'M9 6h11M9 12h11M9 18h11M3.5 6l1.2 1.2L7 4.8M3.5 12l1.2 1.2L7 10.8M3.5 18l1.2 1.2L7 16.8',
  results: 'M3.5 3.5v17h17M7.5 16v-4M12 16V7.5M16.5 16v-6',
  automate: 'M12 7v5l3 2M3.5 12a8.5 8.5 0 1 0 2.5-6M3.5 4v4h4',
  inspect: 'M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M10 9.5l6 2.2-2.6 1.1-1.1 2.6z',
  app: 'M3.5 5.5h17v11h-17zM9 20h6M12 16.5V20',
  spark: 'M11 3l2.1 5.9L19 11l-5.9 2.1L11 19l-2.1-5.9L3 11l5.9-2.1zM19 2v4M17 4h4',
  help: 'M9.2 9.2a2.9 2.9 0 1 1 4.1 2.6c-.8.4-1.3 1.1-1.3 2v.7M12 17.6v.1M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z',
  settings: 'M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4zM10.3 3.5h3.4l.5 2.4 1.7 1 2.3-.8 1.7 2.9-1.8 1.6v2l1.8 1.6-1.7 2.9-2.3-.8-1.7 1-.5 2.4h-3.4l-.5-2.4-1.7-1-2.3.8-1.7-2.9 1.8-1.6v-2L4.1 9l1.7-2.9 2.3.8 1.7-1z',
  sample: 'M13.5 4.5H19.5V10.5M19.5 4.5L11 13M17.5 13.5v5a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1h5',
  search: 'M15.5 15.5L20.5 20.5M10.5 17a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13z',
  activity: 'M3.5 12a8.5 8.5 0 1 0 2.4-5.9M3.5 3.5v3.6h3.6M12 7.5V12l3 1.8',
  plus: 'M12 4.5v15M4.5 12h15',
  play: 'M7 4.8v14.4a.6.6 0 0 0 .9.5l11.4-7.2a.6.6 0 0 0 0-1L7.9 4.3a.6.6 0 0 0-.9.5z',
  stop: 'M6.5 5.5h11a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1z',
  chevronDown: 'M6 9.5l6 6 6-6',
  chevronRight: 'M9.5 6l6 6-6 6',
  chevronLeft: 'M14.5 6l-6 6 6 6',
  edit: 'M14.5 5.5l4 4M4.5 19.5l1-4.3L15.8 4.9a1.4 1.4 0 0 1 2 0l1.3 1.3a1.4 1.4 0 0 1 0 2L8.8 18.5z',
  save: 'M5 3.5h11.5l4 4V19a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 19V5A1.5 1.5 0 0 1 5 3.5zM7.5 3.5v5h8v-5M7.5 20.5v-6h9v6',
  copy: 'M8.5 8.5h10a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1h-10a1 1 0 0 1-1-1v-10a1 1 0 0 1 1-1zM15.5 8.5V5.5a1 1 0 0 0-1-1h-9a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h3',
  delete: 'M4.5 6.5h15M9.5 6.5V4.5h5v2M6.5 6.5l.9 12.1a1.5 1.5 0 0 0 1.5 1.4h6.2a1.5 1.5 0 0 0 1.5-1.4l.9-12.1M10 10.5v6M14 10.5v6',
  more: 'M5.5 12h.01M12 12h.01M18.5 12h.01',
  close: 'M6 6l12 12M18 6L6 18',
  info: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v5.5M12 7.8v.1',
  warning: 'M12 4l9 15.5H3zM12 10v4.5M12 17.2v.1',
  refresh: 'M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4',
  report: 'M6.5 3.5h7l4 4v12a1 1 0 0 1-1 1h-10a1 1 0 0 1-1-1v-15a1 1 0 0 1 1-1zM13.5 3.5v4h4M8.5 12.5h7M8.5 16h5',
  view: 'M2.5 12s3.5-6.5 9.5-6.5 9.5 6.5 9.5 6.5-3.5 6.5-9.5 6.5S2.5 12 2.5 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
  up: 'M12 19.5v-15M6 10.5l6-6 6 6',
  down: 'M12 4.5v15M6 13.5l6 6 6-6',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  back: 'M19.5 12h-15M10.5 6l-6 6 6 6',
  notes: 'M5.5 4.5h13a1 1 0 0 1 1 1v9.5l-4.5 4.5h-9.5a1 1 0 0 1-1-1v-13a1 1 0 0 1 1-1zM19.5 15h-4.5v4.5M8 9h8M8 12.5h5',
  folder: 'M3.5 7V5.5a1 1 0 0 1 1-1h5l2 2h8a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1z',
} as const

/** Glyph names. */
export type StudioIconKind = keyof typeof PATHS

/** A 20 px (or `size`) outline glyph that follows the current text color. */
export function StudioIcon({ kind, size = 20 }: { kind: StudioIconKind; size?: number }) {
  const solid = kind === 'play' || kind === 'stop'
  return <svg width={size} height={size} viewBox="0 0 24 24" fill={solid ? 'currentColor' : 'none'} stroke="currentColor"
    strokeWidth={kind === 'more' ? 3 : 1.5} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
    <path d={PATHS[kind]} />
  </svg>
}
