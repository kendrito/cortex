/** Cordis Loader configuration file discovery. */

import { globSync } from 'node:fs'

/**
 * Return repository-relative Cordis Loader YAML paths under `root`.
 *
 * @param root Repository root to scan.
 * @returns Sorted repository-relative Loader configuration paths.
 */
export function cordisConfigFiles(root: string): string[] {
  return globSync(['**/*cordis*.yml', '**/*cordis*.yaml'], {
    cwd: root,
    exclude: ['.claude/**', 'node_modules/**', 'vendor/**'],
  }).sort()
}
