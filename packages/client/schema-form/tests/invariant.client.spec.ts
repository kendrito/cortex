import { describe, expect, it } from 'vitest'
import { Context } from '@cortex/cordis'
import * as SchemaFormInvariant from '@cortex/client-schema-form/invariant'
import InvariantRegistry from '@cortex/invariants'

describe('invariant companion', () => {
  it('registers under the package name with an empty installer', async () => {
    const ctx = new Context()
    await ctx.plugin(InvariantRegistry, { enabled: true })
    await expect(ctx.plugin(SchemaFormInvariant).await()).resolves.toBeDefined()
  })
})
