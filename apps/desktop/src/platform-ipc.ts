/** Shared names for the desktop Platform bridge. */
/** Private desktop channels; the Platform renderer receives bootstrap and locale updates. */
export const PLATFORM_IPC = {
  bootstrap: 'cortex-platform:bootstrap',
  localeChanged: 'cortex-platform:locale-changed',
  open: 'cortex-platform:open',
  bounds: 'cortex-platform:bounds',
  close: 'cortex-platform:close',
} as const

/** Resolved Platform language; Desktop resolves the system preference before sending it. */
export type PlatformLocale = 'en_US' | 'zh_CN'
