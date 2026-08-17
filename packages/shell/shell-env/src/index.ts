/**
 * Tool-independent shell environment plugin: owns the `ctx.shellEnv` registry of
 * trusted, per-execution `CORTEX_*` variables consumed by the model-facing shell
 * tools (`cortex-tool-bash`, `cortex-tool-pwsh`). Built-in shell facts are owned by
 * the registry itself while plugins can register additional, enumerable facts
 * with effect-scoped disposal.
 *
 * @module @cortex/shell-env
 */

import { Service, type Context } from '@cortex/cordis'
import z from '@cortex/schemastery'
import { CORTEX_ENV_PREFIX } from '@cortex/shell'
import type { CortexEnvironment, CortexEnvironmentKey } from '@cortex/shell'
import { CORTEX_HOME_ENV, resolveCortexHome } from '@cortex/home-paths'
import type { ToolExecution } from '@cortex/tools'
import type {} from '@cortex/session-persistence'

declare module '@cortex/cordis' {
  interface Context {
    shellEnv: ShellEnvRegistry
  }
}

export const name = 'shell-env'
export const inject: string[] = []

/** Plugin config (all optional — the built-in facts resolve without defaults). */
export interface Config {
  /** Cortex home directory exposed as `CORTEX_HOME`; defaults to `$CORTEX_HOME` or `~/.cortex`. */
  cortexHome?: string
}

/** Runtime configuration schema for the shell-env plugin. */
export const Config: z<Config> = z.object({
  cortexHome: z.string(),
})

/** Model-visible metadata for one managed `CORTEX_*` environment variable. */
export interface BashEnvVariable {
  /** Concise description of the environment fact represented by the variable. */
  description: string
}

/**
 * A plugin contribution to the managed environment of each model shell call.
 * Declared keys make ownership conflicts detectable before the first command;
 * `resolve` computes only the values available for the current execution.
 */
export interface BashEnvContributor {
  /** Stable contributor name used in diagnostics and duplicate detection. */
  name: string
  /** Complete set of `CORTEX_*` keys this contributor may return. */
  variables: Readonly<Record<CortexEnvironmentKey, BashEnvVariable>>
  /**
   * Resolve this contributor's available values for one tool execution.
   * @param execution - the shell tool execution and its optional calling agent.
   * @returns a partial map containing only keys declared in {@link variables}.
   */
  resolve(execution: ToolExecution): Readonly<Partial<Record<CortexEnvironmentKey, string>>>
}

/** An enumerable declaration returned by {@link ShellEnvRegistry.list}. */
export interface BashEnvVariableInfo extends BashEnvVariable {
  /** Contributor that owns the variable. */
  contributor: string
  /** Declared `CORTEX_*` environment variable name. */
  key: CortexEnvironmentKey
}

const CORTEX_SHELL_KEY = `${CORTEX_ENV_PREFIX}SHELL` as const
const CORTEX_SESSION_ID_KEY = `${CORTEX_ENV_PREFIX}SESSION_ID` as const
const CORTEX_SESSION_JSONL_KEY = `${CORTEX_ENV_PREFIX}SESSION_JSONL` as const
const RESERVED_BASH_ENV_KEYS = new Set<CortexEnvironmentKey>([
  CORTEX_HOME_ENV,
  CORTEX_SHELL_KEY,
  CORTEX_SESSION_ID_KEY,
])
const BASH_ENV_KEY_SUFFIX = /^[A-Z][A-Z0-9_]*$/

/**
 * Registry (`ctx.shellEnv`) for trusted, per-execution `CORTEX_*` variables.
 * The namespace is rebuilt for every model shell call: ambient `CORTEX_*` values
 * are discarded by the executor, then the registry's current snapshot is
 * injected. Built-in shell facts remain owned by the registry itself while
 * plugins can register additional, enumerable facts with effect-scoped
 * disposal.
 */
export class ShellEnvRegistry extends Service {
  private readonly contributors = new Map<string, BashEnvContributor>()
  private readonly keyOwners = new Map<CortexEnvironmentKey, string>()
  private readonly cortexHome: string

  /**
   * Create and install the `ctx.shellEnv` service.
   * @param ctx - Cordis context that owns the service and registrations.
   * @param config - home-directory configuration for the built-in variables.
   */
  constructor(ctx: Context, config: Config = {}) {
    super(ctx, 'shellEnv')
    this.cortexHome = resolveCortexHome(config.cortexHome)
  }

  /**
   * Register one environment contributor. Names and keys are unique; built-in
   * keys are reserved. Registration is disposed with the calling plugin fiber.
   * @param contributor - declared key ownership and per-execution resolver.
   * @returns the disposer that unregisters the contribution.
   */
  register(contributor: BashEnvContributor): () => void {
    const dispose = this.ctx.effect(function* (this: ShellEnvRegistry) {
      if (contributor.name.trim().length === 0) {
        throw new Error('bash env contributor name must be non-empty')
      }
      if (this.contributors.has(contributor.name)) {
        throw new Error(`bash env contributor "${contributor.name}" is already registered`)
      }

      const variables = Object.entries(contributor.variables) as [CortexEnvironmentKey, BashEnvVariable][]
      for (const [key, variable] of variables) {
        if (!key.startsWith(CORTEX_ENV_PREFIX)
          || !BASH_ENV_KEY_SUFFIX.test(key.slice(CORTEX_ENV_PREFIX.length))) {
          throw new Error(`bash env contributor "${contributor.name}" declared invalid key "${key}"`)
        }
        if (RESERVED_BASH_ENV_KEYS.has(key)) {
          throw new Error(`bash env contributor "${contributor.name}" cannot own reserved key "${key}"`)
        }
        if (variable.description.trim().length === 0) {
          throw new Error(`bash env contributor "${contributor.name}" must describe "${key}"`)
        }
        const owner = this.keyOwners.get(key)
        if (owner !== undefined) {
          throw new Error(`bash env key "${key}" is already owned by contributor "${owner}"; contributor "${contributor.name}" cannot also own it`)
        }
      }

      this.contributors.set(contributor.name, contributor)
      for (const [key] of variables) this.keyOwners.set(key, contributor.name)
      yield () => {
        this.contributors.delete(contributor.name)
        for (const [key] of variables) this.keyOwners.delete(key)
      }
    }.bind(this), 'bashEnv.register()')
    return () => void dispose()
  }

  /**
   * Build the trusted `CORTEX_*` snapshot for one shell tool execution.
   * @param execution - the current tool execution.
   * @returns an immutable environment overlay containing built-ins and current contributions.
   */
  collect(execution: ToolExecution): CortexEnvironment {
    const values: Record<CortexEnvironmentKey, string> = {
      [CORTEX_HOME_ENV]: this.cortexHome,
      [CORTEX_SHELL_KEY]: '1',
    }
    if (execution.agent !== undefined) {
      values[CORTEX_SESSION_ID_KEY] = execution.agent.session.header.id
    }

    for (const contributor of [...this.contributors.values()].sort((left, right) => left.name.localeCompare(right.name))) {
      const resolved = contributor.resolve(execution)
      for (const [rawKey, value] of Object.entries(resolved)) {
        const key = rawKey as CortexEnvironmentKey
        if (!Object.hasOwn(contributor.variables, key)) {
          throw new Error(`bash env contributor "${contributor.name}" returned undeclared key "${key}"`)
        }
        if (typeof value !== 'string') {
          throw new Error(`bash env contributor "${contributor.name}" returned a non-string value for "${key}"`)
        }
        values[key] = value
      }
    }

    return Object.freeze(Object.fromEntries(Object.entries(values).sort(([left], [right]) => left.localeCompare(right))))
  }

  // TODO(bash-env-list-builtins): Include registry-owned built-ins before diagnostics,
  // prompt, or UI code treats list() as an exhaustive environment catalog.
  /**
   * Enumerate plugin-contributed variables without executing their resolvers.
   * @returns declarations sorted by environment variable name.
   */
  list(): BashEnvVariableInfo[] {
    return [...this.contributors.values()]
      .flatMap(contributor => Object.entries(contributor.variables).map(([key, variable]) => ({
        contributor: contributor.name,
        description: variable.description,
        key: key as CortexEnvironmentKey,
      })))
      .sort((left, right) => left.key.localeCompare(right.key))
  }
}

/**
 * Load the shell-env plugin: register the `ctx.shellEnv` service and the
 * shell-agnostic persistence contributor (`CORTEX_SESSION_JSONL`).
 * @param ctx - Cordis context that owns the service and registrations.
 * @param config - home-directory configuration for the built-in variables.
 */
export function apply(ctx: Context, config: Config = {}): void {
  const registry = new ShellEnvRegistry(ctx, config)
  registry.register({
    name: 'session-persistence',
    variables: {
      [CORTEX_SESSION_JSONL_KEY]: {
        description: 'Absolute target path of the current session JSONL when the active persistence backend provides one.',
      },
    },
    resolve(execution) {
      const agent = execution.agent
      if (agent === undefined) return {}
      const location = ctx.get('sessionPersistence')?.locate(agent.session.header)
      return location?.kind === 'jsonl' ? { [CORTEX_SESSION_JSONL_KEY]: location.path } : {}
    },
  })
}
