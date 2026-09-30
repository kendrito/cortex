/** Dependency-free IPC names shared with the sandboxed mandatory-update preload. */
export const MANDATORY_IPC = {
  status: 'cortex-desktop:mandatory-status', state: 'cortex-desktop:mandatory-state', action: 'cortex-desktop:mandatory-action',
} as const
