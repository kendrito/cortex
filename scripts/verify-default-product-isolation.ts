/**
 * Keep experimental packages outside default installations, runtime imports, and shipped compositions.
 * Bundles the launcher names in `OPTIONAL_BUNDLES` ship switched off with graphs outside the default product.
 * The GUI admits guarded browser and native computer-use providers and checks their dependency graphs.
 */

import { existsSync, globSync, readFileSync, statSync } from 'node:fs'
import { basename, dirname, extname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { JSDOM } from 'jsdom'
import ts from 'typescript'
import { applyEntryPatches, type PatchOptions } from '@cortex/cordis-plugin-include'
import type { EntryOptions } from '@cortex/cordis-plugin-loader'
import { loadOverlayPatches } from '../packages/boot/app-boot/src/index.ts'
import { bundlePatchPaths, composeEntries } from '../packages/boot/app-boot/src/profile.ts'
import type { CortexBundleManifest } from '../packages/util/package-manifest/src/types.ts'
import { isAgentPresetEntry, isCordisGroupEntry, isJsExpr, loadCordisYaml } from './cordis-yaml.ts'
import {
  GUI_BUNDLE_DIRECTORY, GUI_NATIVE_COMPUTER_USE_DIRECTORY, GUI_NATIVE_COMPUTER_USE_PACKAGE,
  GUI_BROWSER_USE_PACKAGE, GUI_BROWSER_USE_DIRECTORY, GUI_BROWSER_RUNTIME_PACKAGE, GUI_BROWSER_RUNTIME_DIRECTORY,
  isGuiProviderDependency,
} from './experimental-package-policy.ts'
import {
  collectRuntimeLocalSourceSpecifiers,
  collectRuntimeSourceSpecifiers,
} from './verify-client-packages.ts'

const EXPERIMENTAL_PREFIX = '@cortex/experimental-'
// The independently published entry package owns platform-engine dependencies.
const EXTERNAL_KIT_PACKAGES = new Set(['@deepseek-ai/libreoffice-kit'])
const PROFILE_SOURCE = 'packages/boot/app-boot/src/profile.ts'
const PRESET_PATTERN = 'packages/bundle/web-app/presets/*.patch.yml'
const RUNTIME_SECTIONS = ['dependencies', 'optionalDependencies', 'peerDependencies'] as const
const GUI_PROVIDER_PATCH = `${GUI_BUNDLE_DIRECTORY}/cordis.patch.yml`
const GUI_DISABLED_EXPRESSION = "!['web', 'desktop'].includes(ctx.get('profileContext')?.name ?? '')"
const NATIVE_DISABLED_EXPRESSION = `process.platform !== 'win32' || ${GUI_DISABLED_EXPRESSION}`
const ENTRY_SOURCE = '__productIsolationSource'

interface Manifest {
  name: string
  icon?: unknown
  exports?: Record<string, unknown>
  dependencies?: Record<string, string>
  optionalDependencies?: Record<string, string>
  peerDependencies?: Record<string, string>
  devDependencies?: Record<string, string>
  cortex?: { bundle?: CortexBundleManifest; configTrees?: Array<{ path: string }> }
}

interface Package {
  directory: string
  manifest: Manifest
}

/** Counts and violations from the default product's source and configuration inputs. */
export interface ProductIsolationResult {
  failures: string[]
  packageCount: number
  sourceCount: number
  configCount: number
  webPluginCount: number
}

/**
 * Check default app installations and their authored runtime/configuration inputs.
 * Experimental opt-in packages and separate Web preview entries are outside these roots.
 * @param root - repository root; no build outputs or installed workspace links are read.
 * @returns violations and the sizes of the checked package, source, and configuration sets.
 */
export function verifyDefaultProductIsolation(root: string): ProductIsolationResult {
  const failures: string[] = []
  const packages = new Map<string, Package>()
  const directories = new Map<string, Package>()
  const display = (path: string): string => relative(root, path).replaceAll('\\', '/')
  for (const path of globSync([
    'apps/*/package.json', 'packages/*/*/package.json', 'vendor/*/package.json',
    'native/system/packages/*/package.json', 'python/sdk-runtime/package.json',
  ], { cwd: root }).sort()) {
    const manifest = JSON.parse(readFileSync(resolve(root, path), 'utf8')) as Manifest
    if (typeof manifest.name !== 'string' || manifest.name === '') throw new Error(`${path}: missing package name`)
    if (packages.has(manifest.name)) throw new Error(`${path}: duplicate package name ${manifest.name}`)
    const pkg = { directory: dirname(resolve(root, path)), manifest }
    packages.set(manifest.name, pkg)
    directories.set(pkg.directory, pkg)
  }
  for (const path of ['apps/cli/package.json', 'apps/web/package.json', 'python/sdk-runtime/package.json']) {
    if (!existsSync(resolve(root, path))) failures.push(`missing default product root ${path}`)
  }
  const cli = directories.get(resolve(root, 'apps/cli'))
  if (cli?.manifest.name !== '@cortex/cortex') {
    failures.push('apps/cli/package.json must identify @cortex/cortex')
  }
  // The bundles the launcher ships switched off: each a runtime dependency of the installation that is a bundle
  // with an icon and locale display metadata for the plugin manager's Official group, none a default.
  const profilePath = resolve(root, PROFILE_SOURCE)
  const selection = existsSync(profilePath) ? profilePackages(readFileSync(profilePath, 'utf8')) : undefined
  const optionalBundles = new Set(selection?.optionalBundles ?? [])
  for (const name of optionalBundles) {
    if (cli?.manifest.dependencies?.[name] === undefined) {
      failures.push(`${PROFILE_SOURCE}: optional bundle ${name} must be a runtime dependency of apps/cli`)
    }
    const manifest = packages.get(name)?.manifest
    if (manifest?.cortex?.bundle?.patch === undefined) {
      failures.push(`${PROFILE_SOURCE}: optional bundle ${name} must declare cortex.bundle.patch`)
    }
    if (typeof manifest?.icon !== 'string') {
      failures.push(`${PROFILE_SOURCE}: optional bundle ${name} must declare an icon`)
    }
    if (manifest?.exports?.['./locale/*.json'] === undefined) {
      failures.push(`${PROFILE_SOURCE}: optional bundle ${name} must export ./locale/*.json display metadata`)
    }
  }

  const queue: Package[] = []
  const visited = new Set<string>()
  const sources = new Set<string>()
  const configs = new Set<string>()
  let webPluginCount = 0
  const composedGuiProviders = new Set<string>()
  const isExperimental = (pkg: Package): boolean => pkg.manifest.name.startsWith(EXPERIMENTAL_PREFIX)
    || display(pkg.directory).startsWith('packages/experimental/')
  const isNamedPackage = (pkg: Package, name: string, directory: string): boolean =>
    pkg.manifest.name === name && display(pkg.directory) === directory
  const isGuiExperimental = (pkg: Package): boolean =>
    isNamedPackage(pkg, GUI_NATIVE_COMPUTER_USE_PACKAGE, GUI_NATIVE_COMPUTER_USE_DIRECTORY)
    || isNamedPackage(pkg, GUI_BROWSER_USE_PACKAGE, GUI_BROWSER_USE_DIRECTORY)
    || isNamedPackage(pkg, GUI_BROWSER_RUNTIME_PACKAGE, GUI_BROWSER_RUNTIME_DIRECTORY)
  const isBrowserRuntimeEdge = (owner: Package, name: string): boolean =>
    composedGuiProviders.has(GUI_BROWSER_USE_PACKAGE)
    && isNamedPackage(owner, GUI_BROWSER_USE_PACKAGE, GUI_BROWSER_USE_DIRECTORY)
    && name === GUI_BROWSER_RUNTIME_PACKAGE
    && owner.manifest.dependencies?.[GUI_BROWSER_RUNTIME_PACKAGE] !== undefined
  const add = (pkg: Package, origin: string, allowGuiExperimental = false): void => {
    if (isExperimental(pkg) && !(allowGuiExperimental && isGuiExperimental(pkg))) {
      failures.push(`${origin} -> ${pkg.manifest.name}: default product must not include experimental packages`)
    } else if (!visited.has(pkg.directory)) {
      visited.add(pkg.directory)
      queue.push(pkg)
    }
  }
  const ownerOf = (path: string): Package | undefined => {
    let directory = dirname(path)
    for (;;) {
      const pkg = directories.get(directory)
      if (pkg !== undefined) return pkg
      const parent = dirname(directory)
      if (parent === directory) return undefined
      directory = parent
    }
  }
  const reference = (name: string, origin: string, owner?: Package, allowGuiExperimental = false): void => {
    if (name.startsWith('.') || name.startsWith('/') || /^(?:file|link):/.test(name)) {
      const target = name.startsWith('file://') ? fileURLToPath(name)
        : resolve(owner?.directory ?? root, name.replace(/^(?:file|link):/, ''))
      const pkg = directories.get(target) ?? ownerOf(target)
      if (pkg !== undefined) add(pkg, origin)
      return
    }
    const packageName = barePackageName(name)
    if (packageName.startsWith(EXPERIMENTAL_PREFIX)
      && !(allowGuiExperimental && packages.has(packageName))) {
      failures.push(`${origin} -> ${name}: default product must not include experimental packages`)
      return
    }
    const pkg = packages.get(packageName)
    if (pkg !== undefined) add(pkg, origin, allowGuiExperimental)
    else if (EXTERNAL_KIT_PACKAGES.has(packageName)) return
    else if (packageName.startsWith('@cortex/') || packageName.startsWith('@deepseek-ai/')) {
      failures.push(`${origin}: unknown workspace package ${name}`)
    }
  }
  const dependency = (name: string, range: string, owner: Package, origin: string, allowGuiExperimental = false): void => {
    reference(name, origin, owner, allowGuiExperimental)
    if (/^(?:file:|link:|workspace:\.)/.test(range)) {
      reference(range.replace(/^workspace:/, ''), origin, owner)
    } else if (/^(?:npm:|workspace:)(?:@|[a-zA-Z])/.test(range)) {
      reference(range.replace(/^(?:npm:|workspace:)/, ''), origin, owner)
    }
  }
  const sourceReference = (specifier: string, path: string): void => {
    const owner = ownerOf(path)
    if (owner === undefined) return
    const name = barePackageName(specifier)
    const allowBrowserRuntime = specifier === `${GUI_BROWSER_RUNTIME_PACKAGE}/mcp` && isBrowserRuntimeEdge(owner, name)
    reference(specifier, display(path), owner, allowBrowserRuntime)
    const range = [...RUNTIME_SECTIONS, 'devDependencies' as const]
      .map(section => owner.manifest[section]?.[name]).find(value => value !== undefined)
    if (range !== undefined) dependency(name, range, owner, display(path), allowBrowserRuntime)
  }
  const scanSource = (path: string, followLocal = false, inlineSource?: string): void => {
    if (sources.has(path)) return
    sources.add(path)
    const source = inlineSource ?? readFileSync(path, 'utf8')
    for (const specifier of collectRuntimeSourceSpecifiers(path, source)) sourceReference(specifier, path)
    for (const specifier of runtimeLocalSpecifiers(path, source)) {
      const local = followLocal && specifier.startsWith('/')
        ? resolve(root, 'apps/web', `.${specifier.replace(/[?#].*$/, '')}`)
        : resolve(dirname(path), specifier.replace(/[?#].*$/, ''))
      const target = resolveSource(local)
      const resolved = target ?? local
      const owner = directories.get(resolved) ?? ownerOf(resolved)
      if (owner !== undefined && isExperimental(owner)) {
        add(owner, display(path), owner === ownerOf(path) && isGuiExperimental(owner))
      }
      if (/cordis[^/]*\.ya?ml$/.test(basename(resolved))) scanConfig(resolved)
      if (followLocal && target !== undefined) scanSource(target, true)
    }
  }
  const scanEntries = (
    document: unknown[], path: string, composedWeb = false, includeStack: ReadonlySet<string> = new Set(),
  ): void => {
    const visit = (entry: unknown, insidePreset = false): void => {
      if (!isRecord(entry)) return
      const source = composedWeb ? entry[ENTRY_SOURCE] : display(path)
      const provider = guiProviderForId(entry.id)
      if (provider !== undefined && entry.name === undefined
        && ('disabled' in entry || 'config' in entry)
        && (source !== GUI_PROVIDER_PATCH || insidePreset
          || 'disabled' in entry && !hasGuiProviderGuard(entry, provider)
          || 'config' in entry && provider === GUI_BROWSER_USE_PACKAGE && !hasBrowserLaunchConfig(entry.config))) {
        failures.push(`${display(path)} -> ${provider}: GUI provider override must preserve its guarded host configuration`)
      }
      if (typeof entry.name === 'string') {
        if (composedWeb) webPluginCount += 1
        const owner = ownerOf(path)
        if (entry.name.startsWith('.')) {
          const target = resolve(dirname(path), entry.name)
          const targetOwner = ownerOf(target)
          if (targetOwner !== undefined) add(targetOwner, display(path))
        } else {
          const guiOwner = ownerOf(resolve(root, GUI_PROVIDER_PATCH))
          const allowGuiProvider = entry.name === provider && !insidePreset
            && source === GUI_PROVIDER_PATCH && guiOwner?.manifest.name === '@cortex/web-app'
            && guiOwner.manifest.dependencies?.[entry.name] !== undefined
            && hasGuiProviderGuard(entry, entry.name)
            && (entry.name !== GUI_BROWSER_USE_PACKAGE || hasBrowserLaunchConfig(entry.config))
          if (composedWeb && allowGuiProvider) composedGuiProviders.add(entry.name)
          reference(entry.name, display(path), owner, allowGuiProvider)
        }
      }
      if (isCordisGroupEntry(entry) || entry.name === 'cordis:group' && Array.isArray(entry.config)) {
        (entry.config as unknown[]).forEach((child) => { visit(child, insidePreset) })
      }
      if (isAgentPresetEntry(entry)) entry.config.plugins.forEach((child) => { visit(child, true) })
      if (Array.isArray(entry.insert)) entry.insert.forEach((child) => { visit(child, insidePreset) })
      if ((entry.name === '@cortex/cordis-plugin-include' || entry.name === 'cordis:include') && isRecord(entry.config)) {
        if (!composedWeb && Array.isArray(entry.config.patches)) {
          entry.config.patches.forEach((child) => { visit(child, insidePreset) })
        }
        const included = entry.config.path
        if (typeof included !== 'string') return
        const filename = included.startsWith('file:') ? fileURLToPath(included) : resolve(dirname(path), included)
        if (includeStack.has(filename)) {
          failures.push(`${display(path)}: cyclic Include path ${display(filename)}`)
          return
        }
        const nestedStack = new Set([...includeStack, filename])
        const initial = Array.isArray(entry.config.initial) ? entry.config.initial : undefined
        if (initial !== undefined) scanEntries(initial, filename, false, nestedStack)
        if (!composedWeb) {
          if (existsSync(filename) || initial === undefined) scanConfig(filename)
          return
        }
        const content = existsSync(filename) ? loadCordisYaml(readFileSync(filename, 'utf8')) : initial
        if (!Array.isArray(content)) {
          failures.push(`${display(filename)}: included composition must contain an entry array`)
          return
        }
        const entries = applyEntryPatches(content as EntryOptions[], entry.config.patches as PatchOptions[] | undefined, () => {})
        scanEntries(entries, filename, true, nestedStack)
      }
    }
    document.forEach((entry) => { visit(entry) })
  }
  const scanConfig = (path: string): void => {
    if (configs.has(path)) return
    configs.add(path)
    const document = loadCordisYaml(readFileSync(path, 'utf8'))
    if (!Array.isArray(document)) {
      failures.push(`${display(path)}: shipped composition must contain an entry array`)
      return
    }
    scanEntries(document, path)
  }

  for (const pkg of packages.values()) {
    const path = display(pkg.directory)
    if (path.startsWith('apps/') || path === 'python/sdk-runtime') add(pkg, path)
  }
  if (selection !== undefined) {
    for (const name of selection.packages) {
      reference(name, PROFILE_SOURCE)
      if (packages.get(name)?.manifest.cortex?.bundle?.patch === undefined) {
        failures.push(`${PROFILE_SOURCE}: default bundle ${name} must declare cortex.bundle.patch`)
      }
      if (optionalBundles.has(name)) failures.push(`${PROFILE_SOURCE}: optional bundle ${name} must not be a default bundle`)
    }
    const webLayers = selection.webBundles.flatMap((name) => {
      const pkg = packages.get(name)
      const bundle = pkg?.manifest.cortex?.bundle
      if (pkg === undefined || bundle === undefined) return []
      return [bundlePatchPaths(pkg.directory, bundle).flatMap((file) => {
        const patches = loadOverlayPatches('verify-default-product-isolation', file)
        markEntrySources(patches, display(file))
        return patches
      })]
    })
    if (webLayers.length !== selection.webBundles.length) {
      failures.push(`${PROFILE_SOURCE}: default Web bundle layers are incomplete`)
    } else {
      const entries = composeEntries(webLayers)
      scanEntries(entries, profilePath, true)
      if (webPluginCount === 0) failures.push(`${PROFILE_SOURCE}: composed Web profile contains no plugins`)
    }
  } else failures.push(`missing default profile source ${PROFILE_SOURCE}`)
  const presets = globSync(PRESET_PATTERN, { cwd: root }).sort()
  if (presets.length === 0) failures.push(`no shipped presets matched ${PRESET_PATTERN}`)
  for (const path of presets) scanConfig(resolve(root, path))

  const html = resolve(root, 'apps/web/index.html')
  if (existsSync(html)) {
    const dom = new JSDOM(readFileSync(html, 'utf8'))
    try {
      const entries = [...dom.window.document.querySelectorAll('script[type="module"]')]
      if (entries.length === 0) failures.push('apps/web/index.html: missing default module entry')
      for (const [index, entry] of entries.entries()) {
        const src = entry.getAttribute('src')
        if (src === null) {
          scanSource(`${html}.inline-${String(index)}.js`, true, entry.textContent)
          continue
        }
        if (/^(?:https?:)?\/\//.test(src)) {
          failures.push(`apps/web/index.html: external default module entry cannot be checked: ${src}`)
          continue
        }
        scanSource(resolve(root, 'apps/web', src.replace(/^\//, '')), true)
      }
    } finally {
      dom.window.close()
    }
  } else failures.push('missing default Web entry apps/web/index.html')

  for (const pkg of queue) {
    const { manifest } = pkg
    for (const section of RUNTIME_SECTIONS) {
      for (const [name, range] of Object.entries(manifest[section] ?? {})) {
        if (pkg === cli && section === 'dependencies' && optionalBundles.has(name)) continue
        dependency(name, range, pkg, `${manifest.name} ${section}`,
          composedGuiProviders.has(name) && isGuiProviderDependency(display(pkg.directory), manifest.name, section, name)
          || section === 'dependencies' && isBrowserRuntimeEdge(pkg, name))
      }
    }
    const patches = manifest.cortex?.bundle === undefined ? [] : bundlePatchPaths(pkg.directory, manifest.cortex.bundle)
    for (const file of patches) scanConfig(file)
    for (const tree of manifest.cortex?.configTrees ?? []) {
      const treePath = resolve(pkg.directory, tree.path)
      const files = existsSync(treePath) && statSync(treePath).isDirectory()
        ? globSync('**/*.{yml,yaml}', { cwd: treePath, exclude: ['**/*.i18n.yaml', '**/preset.yml'] }) : []
      if (files.length === 0) failures.push(`${display(treePath)}: declared config tree has no composition files`)
      for (const path of files) scanConfig(resolve(treePath, path))
    }
    if (display(pkg.directory) === 'apps/web') continue
    const files = globSync('src/**/*.{ts,tsx,mts,cts,js,mjs,cjs}', { cwd: pkg.directory,
      exclude: ['**/*.spec.*', '**/*.test.*', '**/*.d.ts', '**/tests/**', '**/__tests__/**', '**/node_modules/**'] })
    if (display(pkg.directory) === 'apps/cli' && files.length === 0) failures.push('apps/cli: no default runtime sources')
    for (const path of files) {
      const sourcePath = resolve(pkg.directory, path)
      if (statSync(sourcePath).isFile()) scanSource(sourcePath)
    }
  }
  return { failures: [...new Set(failures)], packageCount: visited.size,
    sourceCount: sources.size, configCount: configs.size, webPluginCount }
}

function barePackageName(specifier: string): string {
  return /^(?:@[^/]+\/)?[^/@]+/.exec(specifier)?.[0] ?? specifier
}

function resolveSource(path: string): string | undefined {
  const extension = extname(path)
  if (extension !== '' && !/^\.[cm]?[jt]sx?$/.test(extension)
    && !(existsSync(path) && statSync(path).isDirectory())) return undefined
  const extensions = ['.mjs', '.js', '.mts', '.ts', '.jsx', '.tsx']
  const typed = extension === '.js' ? [path.replace(/\.js$/, '.ts'), path.replace(/\.js$/, '.tsx')]
    : extension === '.mjs' ? [path.replace(/\.mjs$/, '.mts')]
      : extension === '.cjs' ? [path.replace(/\.cjs$/, '.cts')] : []
  return [path, ...typed, ...extension === '' ? extensions.map(suffix => `${path}${suffix}`) : [],
    ...extensions.map(suffix => resolve(path, `index${suffix}`))]
    .find(candidate => existsSync(candidate) && statSync(candidate).isFile())
}

function runtimeLocalSpecifiers(path: string, source: string): Set<string> {
  const specifiers = collectRuntimeLocalSourceSpecifiers(path, source, true)
  const file = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true)
  const visit = (node: ts.Node): void => {
    if (ts.isNewExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'URL') {
      const first = node.arguments?.[0]
      if (first !== undefined && ts.isStringLiteralLike(first)
        && (first.text.startsWith('.') || first.text.startsWith('/'))) specifiers.add(first.text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return specifiers
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** Only these host rows may reach the explicitly admitted GUI providers. */
function guiProviderForId(id: unknown): string | undefined {
  if (id === 'computer-use-cua-driver-native') return GUI_NATIVE_COMPUTER_USE_PACKAGE
  if (id === 'browser-use-playwright-mcp') return GUI_BROWSER_USE_PACKAGE
  return undefined
}

/** Compare the authored predicate without executing configuration code. */
function hasGuiProviderGuard(entry: Record<string, unknown>, provider: string): boolean {
  return isJsExpr(entry.disabled) && entry.disabled.__jsExpr.trim()
    === (provider === GUI_NATIVE_COMPUTER_USE_PACKAGE ? NATIVE_DISABLED_EXPRESSION : GUI_DISABLED_EXPRESSION)
}

/** Default browser sessions launch an isolated background browser rather than attach to an existing one. */
function hasBrowserLaunchConfig(config: unknown): boolean {
  return isRecord(config) && config.mode === 'launch' && config.headless === true
    && Object.keys(config).every(key => key === 'mode' || key === 'headless')
}

/** Preserve each authored row or override's source through offline profile composition. */
function markEntrySources(entries: unknown[], source: string): void {
  for (const entry of entries) {
    if (!isRecord(entry)) continue
    entry[ENTRY_SOURCE] = source
    if (Array.isArray(entry.insert)) markEntrySources(entry.insert, source)
    if (isCordisGroupEntry(entry) || entry.name === 'cordis:group' && Array.isArray(entry.config)) {
      markEntrySources(entry.config as unknown[], source)
    }
    if (isAgentPresetEntry(entry)) markEntrySources(entry.config.plugins, source)
  }
}

/** Read the literal package lists that define installation-owned profile defaults and the optional bundles it ships. */
function profilePackages(source: string): { packages: string[]; webBundles: string[]; optionalBundles: string[] } {
  const file = ts.createSourceFile(PROFILE_SOURCE, source, ts.ScriptTarget.Latest, true)
  const required = new Set(['PROFILE_TEMPLATES', 'DEFAULT_PROFILE_BUNDLES'])
  const found = new Set<string>()
  const packages: string[] = []
  const webBundles: string[] = []
  const optionalBundles: string[] = []
  const literals = (node: ts.Node, path: string[]): void => {
    if (ts.isStringLiteralLike(node)) {
      if (path[0] === 'OPTIONAL_BUNDLES') optionalBundles.push(node.text)
      else if (node.text.startsWith('@')) packages.push(node.text)
      if (path.join('.') === 'PROFILE_TEMPLATES.web.bundles') webBundles.push(node.text)
    } else if (ts.isArrayLiteralExpression(node)) node.elements.forEach((child) => { literals(child, path) })
    else if (ts.isObjectLiteralExpression(node)) node.properties.forEach((child) => { literals(child, path) })
    else if (ts.isPropertyAssignment(node)) {
      if (!ts.isIdentifier(node.name) && !ts.isStringLiteralLike(node.name)) {
        throw new Error(`${PROFILE_SOURCE}: default profile keys must be literal names`)
      }
      literals(node.initializer, [...path, node.name.text])
    }
    else if (ts.isAsExpression(node) || ts.isSatisfiesExpression(node) || ts.isParenthesizedExpression(node)) {
      literals(node.expression, path)
    } else throw new Error(`${PROFILE_SOURCE}: default profile packages must use static literal lists`)
  }
  for (const statement of file.statements) {
    if (!ts.isVariableStatement(statement)) continue
    for (const declaration of statement.declarationList.declarations) {
      if (!ts.isIdentifier(declaration.name)
        || !required.has(declaration.name.text) && declaration.name.text !== 'INSTALLATION_OWNED_PROFILE_TUPLES'
        && declaration.name.text !== 'OPTIONAL_BUNDLES') continue
      if (declaration.initializer === undefined) continue
      if (required.has(declaration.name.text)) found.add(declaration.name.text)
      const before = packages.length
      literals(declaration.initializer, [declaration.name.text])
      if (declaration.name.text !== 'OPTIONAL_BUNDLES' && packages.length === before) {
        throw new Error(`${PROFILE_SOURCE}: ${declaration.name.text} has no default bundles`)
      }
    }
  }
  if (found.size !== required.size) throw new Error(`${PROFILE_SOURCE}: missing default profile declarations`)
  if (webBundles.length === 0) throw new Error(`${PROFILE_SOURCE}: missing default Web bundle list`)
  return { packages, webBundles, optionalBundles }
}

if (import.meta.main) {
  const result = verifyDefaultProductIsolation(resolve(import.meta.dirname, '..'))
  if (result.failures.length > 0) {
    for (const failure of result.failures) console.error(`verify-default-product-isolation: ${failure}`)
    process.exitCode = 1
  } else {
    console.log(`verify-default-product-isolation: ${String(result.packageCount)} packages, `
      + `${String(result.sourceCount)} runtime sources, ${String(result.configCount)} configurations, `
      + `${String(result.webPluginCount)} composed Web plugins checked for experimental isolation.`)
  }
}
