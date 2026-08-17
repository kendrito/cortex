import { describe, expect, it, vi } from 'vitest'
import { Context } from '@cortex/cordis'
import { apply, inject, name } from '../src/invariant.ts'

describe('@cortex/session-log-export/invariant', () => {
  it('registers the package-owned empty companion', async () => {
    const register = vi.fn(() => vi.fn())
    const ctx = new Context()
    ctx.provide('invariants', { register })
    const dispose = await apply(ctx)
    expect(name).toBe('session-export-invariant')
    expect(inject).toEqual(['invariants'])
    expect(register).toHaveBeenCalledWith('@cortex/session-log-export', expect.any(Function))
    dispose()
  })
})
