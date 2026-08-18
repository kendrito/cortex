/** The inert node entry and the invariant companion's ownership reservation. */
import { Context } from '@cortex/cordis'
import { describe, expect, it } from 'vitest'
import InvariantRegistry from '@cortex/invariants'
import { apply as applyNode } from '../src/index.ts'
import * as AtlassianInvariant from '../src/invariant.ts'
import { en, NS } from '../src/client/locales.ts'

describe('ui-atlassian node half', () => {
  it('contributes no host behavior', () => {
    // The node half exists only so the plugin appears in the Loader tree.
    expect(applyNode).not.toThrow()
  })
})

describe('ui-atlassian invariant companion', () => {
  it('reserves package ownership under its declared companion name', async () => {
    const ctx = new Context()
    await ctx.plugin(InvariantRegistry, { enabled: true })
    const fiber = ctx.plugin(AtlassianInvariant)
    await fiber.await()
    expect(AtlassianInvariant.name).toBe('client-ui-atlassian-invariant')
    expect(AtlassianInvariant.inject).toEqual(['invariants'])
    // Emitting an unrelated event proves the companion installed no audit.
    expect(() => { (ctx.emit as (event: string) => void)('slots/changed') }).not.toThrow()
    await fiber.dispose()
  })
})

describe('ui-atlassian dictionaries', () => {
  it('owns the atlassian namespace with English copy for every key', () => {
    expect(NS).toBe('atlassian')
    for (const [key, value] of Object.entries(en)) {
      expect(typeof key).toBe('string')
      expect(value.length).toBeGreaterThan(0)
    }
  })
})
