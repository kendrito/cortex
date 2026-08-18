/**
 * Package-owned invariant companion for `@cortex/atlassian`.
 * @module @cortex/atlassian/invariant
 */

/* jscpd:ignore-start */
import type { Context } from '@cortex/cordis'
import type { InvariantInstaller } from '@cortex/invariants'

const PACKAGE_NAME = '@cortex/atlassian'

/** Cordis companion plugin name. */
export const name = 'atlassian-invariant'
/** Service required before the companion can reserve package ownership. */
export const inject = ['invariants']

/**
 * No runtime invariant: the package's durable state is a pure fold over its
 * own `atlassian/*` log events (validated by the projection registry's wire
 * schema on every emission), its tool registrations and MCP child mounts are
 * effects owned by the tool registry and the child fibers, and every REST
 * call is a user-visible action whose failure surfaces in its own result.
 */
const install: InvariantInstaller = () => {}

/**
 * Register this package's invariant companion.
 * @param ctx - Cordis context carrying the invariant service.
 * @returns the installed registration's disposer after setup succeeds.
 */
export const apply = (ctx: Context): Promise<() => void> =>
  Promise.resolve(ctx.invariants.register(PACKAGE_NAME, install))
/* jscpd:ignore-end */
