/** Filesystem ownership for the Electron-managed desktop installation. */

import { join } from 'node:path'
import { resolveCortexHome } from '@cortex/home-paths'

/** Stable desktop installation paths under the shared Harness home. */
export interface DesktopPaths {
  readonly profile: string
  readonly lock: string
}

/**
 * Resolve every Electron-owned path without changing the shared data roots.
 * @param cortexHome - Harness home shared with npm-installed cortex.
 * @returns immutable desktop path set.
 */
export function resolveDesktopPaths(cortexHome: string = resolveCortexHome()): DesktopPaths {
  return {
    profile: join(cortexHome, 'profiles', 'desktop'),
    lock: join(cortexHome, 'profiles', 'desktop', 'lock'),
  }
}
