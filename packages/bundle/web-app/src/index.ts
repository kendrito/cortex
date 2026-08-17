/**
 * @cortex/web-app — the browser-surface bundle's runtime glue plugin
 * plus the bundle patch (`cordis.patch.yml`, declared by the `cortex.bundle.patch`
 * manifest field). The plugin owns the browser-surface glue: it resolves
 * the built frontend dist (workspace knowledge of this bundle, never user
 * config), mounts the `frontend-static` fallback owner over it, registers the
 * harness-source and web-surface prompt sections, the bash-visible web runtime
 * variable, and the URL line. App command-line values arrive through the
 * `webStartup` service expressions in the bundle patch.
 * @module @cortex/web-app
 */

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'
import { connect } from 'node:net'
import { networkInterfaces } from 'node:os'
import { fileURLToPath } from 'node:url'
import type { Context } from '@cortex/cordis'
import z from '@cortex/schemastery'
import { addHarnessSourceSection } from '@cortex/app-boot'
import * as FrontendStatic from '@cortex/host-frontend-static'
import type {} from '@cortex/cordis-plugin-loader'
import type {} from '@cortex/host-webserver'
import type {} from '@cortex/system-prompt'
import type {} from '@cortex/shell-env'

/** Stable Cordis plugin name. */
export const name = 'web-app'

/** This cortex installation's root, from either this package's source or built entry. */
const SOURCE_ROOT = fileURLToPath(new URL('../../../..', import.meta.url))

/** Runtime service that releases Web rows after bind-dependent values resolve. */
const WEB_RUNTIME_SERVICE = 'webRuntime'

/** Services required before the web runtime can mount. */
export const inject = ['webServer']

/** Plugin config: composed deployment settings plus per-invocation command-line values. */
export interface Config {
  /** Print the URL line on activation; a non-interactive layer can turn it off. */
  printUrl: boolean
  /**
   * Register the model-visible surface context (the `app:web-surface` prompt
   * section and the `CORTEX_WEB_URL` bash variable). A one-shot non-interactive
   * layer can turn it off when its user is not in the GUI, so the
   * orientation text would be false.
   */
  surfaceContext: boolean
  /** Explicit `--trusted-host` authorities from this invocation. */
  trustedHosts: string[]
  /** Embedded editor sidecar: 'auto' starts a local VS Code web server for the Code view; 'off' skips it. */
  editor: 'auto' | 'off'
  /** Local port the editor sidecar binds; the Code view frames this origin. */
  editorPort: number
}

export const Config: z<Config> = z.object({
  printUrl: z.boolean().default(true),
  surfaceContext: z.boolean().default(true),
  trustedHosts: z.array(String).default([]),
  editor: z.union(['auto', 'off']).default('auto'),
  editorPort: z.number().default(3082),
})

/** A found editor server binary and the argv that serves it on a port. */
interface EditorLauncher {
  command: string
  args: (port: number, dataDir: string) => string[]
  label: string
}

/** The sidecar's own persistent data directory; its settings mirror the GUI theme. */
const EDITOR_DATA_DIR = join(homedir(), '.cortex', 'editor')

/** GUI theme preference values the sidecar mirrors (ui-theme's vocabulary). */
type ThemePreference = 'light' | 'dark' | 'system'

/**
 * Mirror the harness theme into the sidecar's VS Code user settings, merging
 * over whatever else the file holds so a hand-added editor setting survives.
 * An explicit preference pins the matching Modern theme; `system` hands the
 * choice to the workbench's own scheme detection — the same browser the
 * harness renders in, so the two stay in step by construction.
 * @param preference - the GUI theme preference to mirror.
 */
function writeEditorTheme(preference: ThemePreference): void {
  const userDir = join(EDITOR_DATA_DIR, 'data', 'User')
  const file = join(userDir, 'settings.json')
  let current: Record<string, unknown> = {}
  try {
    current = JSON.parse(readFileSync(file, 'utf8')) as Record<string, unknown>
  } catch {
    // First run or a hand-broken file: start from the mirrored keys alone.
  }
  const mirrored = preference === 'system'
    ? {
      'window.autoDetectColorScheme': true,
      'workbench.preferredDarkColorTheme': 'Default Dark Modern',
      'workbench.preferredLightColorTheme': 'Default Light Modern',
    }
    : {
      'window.autoDetectColorScheme': false,
      'workbench.colorTheme': preference === 'dark' ? 'Default Dark Modern' : 'Default Light Modern',
    }
  mkdirSync(userDir, { recursive: true })
  writeFileSync(file, `${JSON.stringify({ ...current, ...mirrored }, null, 2)}\n`)
}

/** Narrow an unknown settings value to a theme preference; anything else is `system`. */
function themePreferenceOf(value: unknown): ThemePreference {
  const preference = (value as { preference?: unknown } | undefined)?.preference
  return preference === 'light' || preference === 'dark' ? preference : 'system'
}

/** PATH lookup that never throws; empty on any failure. */
function which(binary: string): string | undefined {
  const probe = spawnSync('which', [binary], { encoding: 'utf8' })
  const found = probe.status === 0 ? probe.stdout.trim() : ''
  return found === '' ? undefined : found
}

/**
 * Discover an installed editor server, preferring code-server (built for
 * embedding) over VS Code's own `serve-web`, including the macOS app bundle
 * CLI for machines where the `code` shell command was never installed.
 * @returns the launcher, or undefined when no editor binary exists.
 */
function findEditorLauncher(): EditorLauncher | undefined {
  const codeServer = which('code-server')
  if (codeServer !== undefined) {
    return {
      command: codeServer,
      label: 'code-server',
      args: (port, dataDir) => [
        '--auth', 'none', '--bind-addr', `127.0.0.1:${String(port)}`,
        '--user-data-dir', join(dataDir, 'data'),
        '--disable-telemetry', '--disable-update-check', '--disable-workspace-trust',
      ],
    }
  }
  const serveWebArgs = (port: number, dataDir: string): string[] => [
    'serve-web', '--host', '127.0.0.1', '--port', String(port),
    '--without-connection-token', '--accept-server-license-terms',
    '--server-data-dir', dataDir, '--disable-telemetry',
  ]
  const code = which('code')
  if (code !== undefined) return { command: code, label: 'code serve-web', args: serveWebArgs }
  const appBundle = '/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code'
  if (process.platform === 'darwin' && existsSync(appBundle)) {
    return { command: appBundle, label: 'code serve-web', args: serveWebArgs }
  }
  return undefined
}

/**
 * Probe whether something already listens on the editor port, so a second
 * `cortex web` (or a hand-started server) is reused instead of collided with.
 * @param port - local port to probe.
 * @returns true when a listener accepted the connection.
 */
function editorAlreadyUp(port: number): Promise<boolean> {
  return new Promise((resolvePort) => {
    const socket = connect({ host: '127.0.0.1', port, timeout: 400 })
    socket.once('connect', () => { socket.destroy(); resolvePort(true) })
    socket.once('error', () => { resolvePort(false) })
    socket.once('timeout', () => { socket.destroy(); resolvePort(false) })
  })
}

/**
 * Start the embedded-editor sidecar for the Code view: reuse a live server,
 * else spawn the discovered launcher and dispose it with the plugin.
 * @param ctx - plugin context owning the child's lifetime.
 * @param port - local port the sidecar serves.
 */
function startEditorSidecar(ctx: Context, port: number): void {
  void editorAlreadyUp(port).then((up) => {
    if (up) {
      console.log(`cortex editor: http://127.0.0.1:${String(port)} (existing server)`)
      return
    }
    const launcher = findEditorLauncher()
    if (launcher === undefined) {
      console.log('cortex editor: no VS Code or code-server found; the Code view will show setup guidance')
      return
    }
    const child = spawn(launcher.command, launcher.args(port, EDITOR_DATA_DIR), { stdio: 'ignore' })
    child.once('error', () => {
      console.log(`cortex editor: failed to start ${launcher.label}`)
    })
    child.once('spawn', () => {
      console.log(`cortex editor: http://127.0.0.1:${String(port)} (${launcher.label})`)
    })
    ctx.effect(() => () => { child.kill() }, 'web-app: editor sidecar')
  })
}

/** Bind-dependent Web values shared by the trust fence and URL display. */
export interface WebRuntimeValues {
  /** LAN IPv4 literals sampled once when the server binds all interfaces. */
  lanAddresses: string[]
  /** LAN literals followed by explicit invocation authorities. */
  trustedHosts: string[]
}

/** Environment variable naming the canonical local URL of this Web GUI. */
const CORTEX_WEB_URL = 'CORTEX_WEB_URL' as const

// Display-only mirror of the webserver schema's loopback host: the address the
// local URL always prints. Not a source of truth — the schema is.
const LOOPBACK_HOST = '127.0.0.1'
/** The webserver schema's all-interfaces bind literal. */
const ALL_INTERFACES_HOST = '0.0.0.0'

/**
 * Resolve one LAN-trust snapshot from the active server bind.
 *
 * Derived entries are port-less IP literals: DNS rebinding needs an
 * attacker-controlled name, while an IP-literal Host is safe on any port and
 * an OS-assigned port is unknowable before bind.
 * @param bindHost - the active webserver bind host.
 * @param extra - explicit `--trusted-host` values, in argument order.
 * @returns the LAN display addresses and invocation-derived fence authorities.
 */
export function resolveLanTrust(bindHost: string, extra: readonly string[]): WebRuntimeValues {
  const lanAddresses = bindHost === ALL_INTERFACES_HOST
    ? Object.values(networkInterfaces()).flat()
      .filter((iface): iface is NonNullable<typeof iface> => iface !== undefined && iface.family === 'IPv4' && !iface.internal)
      .map(iface => iface.address)
    : []
  return { lanAddresses, trustedHosts: [...lanAddresses, ...extra] }
}

/** Model-visible orientation and acceptance boundary for sessions created through `cortex web`. */
function webSurfacePrompt(webUrl: string): string {
  const updateContract = 'The client-plugin HMR receiver is active, but client-plugin changes reload without a refresh only while '
    + '`pnpm run dev:web` is also running from this same checkout to rebuild their bundles; verify that watcher before promising automatic updates. '
    + 'Every other change — the apps/web shell and plain packages — requires rebuilding the affected Web artifacts and verifying this existing URL after a page refresh. '
  return `You are interacting with the user through the Cortex Web GUI at ${webUrl}. `
    + 'When the user refers to "this page", "this GUI", or "this app" without naming another target, they mean this GUI. '
    + 'The browser provides no implicit DOM, route, or screenshot context. '
    + updateContract
    + 'Starting another server does not update this GUI. '
    + 'The apps/web Vite entry builds the shell but is not a standalone application because only cortex web injects window.__CORTEX_BOOT__. '
    + 'Do not start a replacement server unless the user asks; if one is needed, use a managed background job and verify its exact URL.'
}

/** Resolve the canonical loopback URL from the active Web server. */
function localWebUrl(ctx: Context): string {
  const port = ctx.get('webServer')?.port
  if (port === undefined) throw new Error('web-app: webServer service missing while resolving Web runtime')
  return `http://${LOOPBACK_HOST}:${String(port)}`
}

/** Dist location is workspace knowledge of this bundle: resolved through the frontend package exports, not configured. */
function resolveDistIndex(): string {
  const require = createRequire(import.meta.url)
  try {
    return require.resolve('@cortex/web-frontend/dist/index.html')
  } catch {
    /* v8 ignore next 2 -- reachable only on a checkout without a built dist; the test tree builds it */
    throw new Error('web-app: frontend dist not built; run pnpm run build from the repository root first')
  }
}

/** Test hook: hosts with no built frontend dist substitute the resolver; production never touches this. */
export const internals: { resolveDistIndex: () => string } = { resolveDistIndex }

/**
 * Mount the Web runtime: dist serving, surface prompt, the bash runtime
 * variable, and the URL line.
 * @param ctx - plugin context carrying the webServer service.
 * @param config - validated {@link Config}.
 */
export function apply(ctx: Context, config: Config): void {
  const runtime = resolveLanTrust(ctx.webServer.host, config.trustedHosts)
  // Release dependent rows only after bind-dependent trust has been sampled once.
  ctx.provide(WEB_RUNTIME_SERVICE, runtime)
  ctx.plugin(FrontendStatic, { distIndex: internals.resolveDistIndex() })
  if (config.surfaceContext) {
    ctx.inject(['systemPrompt'], (promptCtx) => {
      addHarnessSourceSection(promptCtx, SOURCE_ROOT)
      promptCtx.systemPrompt.section({
        name: 'app:web-surface',
        order: -98,
        text: () => webSurfacePrompt(localWebUrl(promptCtx)),
      })
    })
    ctx.inject(['shellEnv'], (runtimeCtx) => {
      runtimeCtx.shellEnv.register({
        name: 'web-runtime',
        variables: {
          [CORTEX_WEB_URL]: { description: 'Canonical local URL of the Cortex Web GUI serving this session.' },
        },
        resolve: () => ({ [CORTEX_WEB_URL]: localWebUrl(runtimeCtx) }),
      })
    })
  }
  if (config.editor === 'auto') {
    // Mirror every committed theme change; the Code view reloads its frame on
    // the same commit. The initial seed waits for Loader settlement, because
    // this row can activate before ui-theme has registered its namespace —
    // seeding earlier reads undefined and mis-writes the `system` shape.
    ctx.on('settings/updated', (ns: unknown, next: unknown) => {
      if (String(ns) === 'ui-theme') writeEditorTheme(themePreferenceOf(next))
    })
    const seed = (): void => {
      const settingsService = ctx.get('settings') as { get(ns: string): unknown } | undefined
      writeEditorTheme(themePreferenceOf(settingsService?.get('ui-theme')))
      startEditorSidecar(ctx, config.editorPort)
    }
    const settledBoot = ctx.get('loader')?.await()
    if (settledBoot === undefined) seed()
    else void settledBoot.then(() => { seed() }, () => { seed() })
  }

  if (config.printUrl) {
    // The URL line is a readiness signal: supervisors (and the keyless CLI
    // smoke) RPC as soon as they observe it, so it must not print while
    // sibling rows (the /api route owner) are still mounting. Await Loader
    // settlement first; a hand-built tree without a Loader prints at once.
    const printUrl = (): void => {
      // Reuse the exact LAN snapshot provided to the /api trust fence.
      const lanCandidate = runtime.lanAddresses[0]
      const port = ctx.webServer.port
      console.log(`cortex web: ${localWebUrl(ctx)}${lanCandidate === undefined ? '' : ` (LAN: http://${lanCandidate}:${String(port)})`}`)
    }
    // This row's own activation can precede a sibling failure. The app owns
    // readiness by waiting for its Loader tree, or prints at once in a
    // hand-built context without Loader.
    const settled = ctx.get('loader')?.await()
    if (settled === undefined) printUrl()
    else {
      void settled.then(() => {
        // The tree can be disposed while the boot was in flight (early
        // SIGTERM); a URL line for a dead server would only mislead, and
        // reading the torn-down port would turn a clean shutdown into a crash.
        if (ctx.get('webServer') !== undefined) printUrl()
      // Loader reports a failed boot; this row only stays quiet.
      }, () => {})
    }
  }
}
