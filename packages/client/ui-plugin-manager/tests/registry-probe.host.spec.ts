import { Context } from '@cortex/cordis'
import { afterEach, expect, it, onTestFinished, vi } from 'vitest'
import PluginRegistryProbe from '../src/index.ts'

afterEach(() => { vi.unstubAllGlobals() })

it.each([undefined, false, true])('never contacts public registries even with a legacy probe setting of %s', async (enabled) => {
  const fetcher = vi.fn()
  vi.stubGlobal('fetch', fetcher)
  const ctx = new Context()
  onTestFinished(() => ctx.fiber.dispose())
  const config = PluginRegistryProbe.Config(enabled === undefined ? {} : { registryProbeEnabled: enabled })
  await ctx.plugin(PluginRegistryProbe, config)
  const probe = ctx.pluginRegistryProbe
  expect(await probe.fastest()).toBeNull()
  expect(await probe.fastest()).toBeNull()
  expect(fetcher).not.toHaveBeenCalled()
})
