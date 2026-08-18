/**
 * Package-owned invariant companion for `@cortex/client-ui-atlassian`.
 * @module @cortex/client-ui-atlassian/invariant
 */

/* jscpd:ignore-start */
import type { Context } from '@cortex/cordis'
import type { InvariantInstaller } from '@cortex/invariants'

const PACKAGE_NAME = '@cortex/client-ui-atlassian'

/** Cordis companion plugin name. */
export const name = 'client-ui-atlassian-invariant'
/** Service required before the companion can reserve package ownership. */
export const inject = ['invariants']

/**
 * No runtime invariant: the browser half only registers slot entries and
 * dictionaries (effects owned by their registries) and renders the `atlassian`
 * projection the host validates on every emission.
 */
const install: InvariantInstaller = () => {}

/**
 * Register this package's invariant companion.
 * @param ctx - Cordis context carrying the invariant service.
 * @returns The installed registration's disposer after setup succeeds.
 */
export const apply = (ctx: Context): Promise<() => void> =>
  Promise.resolve(ctx.invariants.register(PACKAGE_NAME, install))
/* jscpd:ignore-end */
