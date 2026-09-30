/** Reject updater feed configuration from Cortex application artifacts. */
import { lstat } from 'node:fs/promises'
import { join } from 'node:path'

/**
 * @param {string} resourcesDir Assembled Electron resources directory, before signing.
 * @returns {Promise<void>} Resolves only when no updater feed is packaged.
 */
export async function verifyDesktopUpdatesDisabled(resourcesDir) {
  try {
    await lstat(join(resourcesDir, 'app-update.yml'))
  } catch (error) {
    if (error?.code === 'ENOENT') return
    throw error
  }
  throw new Error('desktop package: Cortex must not contain app-update.yml; remote updates are disabled')
}
