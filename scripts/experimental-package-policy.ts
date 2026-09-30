/** Experimental packages excluded from public releases and npm baselines. */
export const PRIVATE_EXPERIMENTAL_PACKAGE_DIRECTORIES: readonly string[] = []

/** Native provider admitted only by the Windows GUI host composition. */
export const GUI_NATIVE_COMPUTER_USE_PACKAGE = '@cortex/experimental-computer-use-cua-driver-native'

/** Source directory of the admitted native provider. */
export const GUI_NATIVE_COMPUTER_USE_DIRECTORY = 'packages/experimental/computer-use-cua-driver-native'

/** Browser provider admitted only by the interactive GUI host composition. */
export const GUI_BROWSER_USE_PACKAGE = '@cortex/experimental-browser-use-playwright-mcp'

/** Source directory of the admitted browser provider. */
export const GUI_BROWSER_USE_DIRECTORY = 'packages/experimental/browser-use-playwright-mcp'

/** Internal browser lifecycle library reached only through the admitted Playwright provider. */
export const GUI_BROWSER_RUNTIME_PACKAGE = '@cortex/experimental-browser-use-runtime'

/** Source directory of the admitted browser lifecycle library. */
export const GUI_BROWSER_RUNTIME_DIRECTORY = 'packages/experimental/browser-use-runtime'

/** GUI bundle that owns the profile-guarded provider rows. */
export const GUI_BUNDLE_DIRECTORY = 'packages/bundle/web-app'

/**
 * Identify the two experimental provider dependencies required by the interactive GUI.
 * Composition guards and runtime imports are checked separately by product isolation.
 * @param directory - repository-relative consumer directory.
 * @param owner - consumer package name.
 * @param section - manifest dependency section.
 * @param dependency - dependency package name.
 * @returns Whether this is a named provider dependency of the GUI bundle.
 */
export function isGuiProviderDependency(
  directory: string, owner: string | undefined, section: string, dependency: string,
): boolean {
  return directory === GUI_BUNDLE_DIRECTORY && owner === '@cortex/web-app'
    && section === 'dependencies'
    && (dependency === GUI_NATIVE_COMPUTER_USE_PACKAGE || dependency === GUI_BROWSER_USE_PACKAGE)
}

/**
 * Whether an experimental package publishes under the default-public policy.
 * @param directory - repository-relative package directory.
 * @param privateDirectories - experimental directories excluded from publication.
 * @returns Whether the package publishes with the cortex family.
 */
export function isPublicExperimentalPackageDirectory(
  directory: string,
  privateDirectories: readonly string[] = PRIVATE_EXPERIMENTAL_PACKAGE_DIRECTORIES,
): boolean {
  return /^packages\/experimental\/[^/]+$/.test(directory)
    && !privateDirectories.includes(directory)
}
