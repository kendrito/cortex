import type { BeforePackContext } from 'app-builder-lib'

/**
 * Install source-relative PE and complete Testy runtime patterns in the active builder configuration.
 * @param context Active builder configuration and cleanup owner.
 * @param sourceRoot Verified prepared cortex directory; external sources receive a build-owned staging copy.
 * @returns Required unpacked paths relative to the original prepared directory.
 */
export function prepareWindowsAsarUnpack(context: BeforePackContext, sourceRoot: string): Promise<string[]>

/**
 * Reject inline, absent, linked, or changed native runtime files in the assembled application.
 * @param sourceRoot Original signed and sealed cortex directory.
 * @param resourcesDir Assembled application resources directory.
 * @param files Required unpacked paths returned by prepareWindowsAsarUnpack.
 * @returns Resolves after every required file has an unpacked entry and identical bytes.
 */
export function verifyWindowsAsarUnpack(sourceRoot: string, resourcesDir: string, files: string[]): Promise<void>
