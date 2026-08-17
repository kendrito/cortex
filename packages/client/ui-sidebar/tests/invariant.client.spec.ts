import { describe, expect, it } from 'vitest'
import { Context } from '@cortex/cordis'
import * as SidebarInvariant from '@cortex/client-ui-sidebar/invariant'
import InvariantRegistry from '@cortex/invariants'

describe('invariant companion', () => {
  it('registers under the package name with an empty installer', async () => {
    const ctx = new Context()
    await ctx.plugin(InvariantRegistry, { enabled: true })
    await expect(ctx.plugin(SidebarInvariant).await()).resolves.toBeDefined()
  })

  it('node-half apply is a no-op host placeholder', async () => {
    const { apply } = await import('@cortex/client-ui-sidebar')
    apply()
    expect(true).toBe(true) // reaching here without throw is the contract
  })
})
