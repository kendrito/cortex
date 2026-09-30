/** Resolve the native runtime on disk in development and packaged Desktop installations. */
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * Locate Testy's executable beside the complete self-contained Windows runtime.
 * @param configured - explicit deployment executable path, or empty for the bundled runtime.
 * @param moduleUrl - host module URL whose package contains the runtime directory.
 * @returns absolute physical executable path, outside ASAR for a bundled Desktop runtime.
 */
export function resolveTestyExecutable(configured: string, moduleUrl: string): string {
  if (configured.length > 0) return resolve(configured)
  return fileURLToPath(new URL('../runtime/win-x64/Testy.Cli.exe', moduleUrl))
    .replace(/([/\\])app\.asar([/\\])/u, '$1app.asar.unpacked$2')
}
