/** Shipped GUI browser tools use the real Loader, upstream server, and Agent scopes. */
import { assembleContextFor } from '@cortex/agent'
import BrowserUse from '@cortex/browser-use'
import { BrowserUseProviderName } from '@cortex/browser-use/brand'
import * as PlaywrightProvider from '@cortex/experimental-browser-use-playwright-mcp'
import { expect, it } from 'vitest'
import { bootGuiCapability, standardAgentOn } from './fixtures/gui-capability.ts'

function boot(profile: string | undefined = 'web', disabled?: boolean, bundles?: readonly string[]) {
  return bootGuiCapability({
    profile, ...bundles === undefined ? {} : { bundles },
    plugins: {
      '@cortex/browser-use': BrowserUse,
      '@cortex/experimental-browser-use-playwright-mcp': PlaywrightProvider,
    },
    patches: disabled === undefined ? [] : [{ id: 'browser-use-playwright-mcp', disabled }],
  })
}

it.each(['web', 'desktop'])('enables isolated headless browser control in the %s profile', async (profile) => {
  const ctx = await boot(profile)
  const entry = [...ctx.loader.entries()].find(entry => entry.options.id === 'browser-use-playwright-mcp')!
  expect(entry.disabled).toBe(false)
  expect(entry.options.config).toEqual({ mode: 'launch', headless: true })
  expect(ctx.browserUse.providerName).toBe('playwright-mcp')
  expect(() => ctx.browserUse.register(BrowserUseProviderName('another-browser'))).toThrow('already registered')
})

it.each(['headless', 'sdk', 'sdk-minimal', 'acp'])('leaves the composed %s profile without browser tools', async (profile) => {
  const ctx = await boot(profile)
  expect(ctx.get('browserUse')).toBeUndefined()
  const { agent } = await standardAgentOn(ctx, `no-browser-${profile}`)
  expect(ctx.tools.schemas(agent)).toEqual([])
})

it.each(['headless', 'sdk', 'acp', 'custom-web'])('keeps browser control off if %s includes the Web bundle', async (profile) => {
  const ctx = await boot(profile, undefined, ['@cortex/base', '@cortex/web-app'])
  expect([...ctx.loader.entries()].find(entry => entry.options.id === 'browser-use-playwright-mcp')!.disabled).toBe(true)
  expect(ctx.browserUse.providerName).toBeUndefined()
})

it('exposes upstream tools in standard Sessions and disposes each browser connection independently', async () => {
  const ctx = await boot()
  const first = await standardAgentOn(ctx, 'browser-first')
  const second = await standardAgentOn(ctx, 'browser-second')
  for (const { agent } of [first, second]) {
    const assembly = await ctx.systemPrompt.assemble(assembleContextFor(agent))
    expect(assembly.tools.map(tool => tool.name)).toContain('mcp__playwright-mcp__browser_navigate')
  }
  await first.dispose()
  expect(ctx.tools.schemas(first.agent)).toEqual([])
  expect(ctx.tools.schemas(second.agent).map(tool => tool.name)).toContain('mcp__playwright-mcp__browser_navigate')
  await [...ctx.loader.entries()].find(entry => entry.options.id === 'browser-use-playwright-mcp')!.update({ disabled: true })
  await ctx.loader.await()
  expect(ctx.tools.schemas(second.agent)).toEqual([])
  expect(ctx.browserUse.providerName).toBeUndefined()
})

it('respects a user disable and only initializes future Sessions after re-enabling', async () => {
  const ctx = await boot('web', true)
  const existing = await standardAgentOn(ctx, 'browser-existing')
  expect(ctx.tools.schemas(existing.agent)).toEqual([])
  const entry = [...ctx.loader.entries()].find(entry => entry.options.id === 'browser-use-playwright-mcp')!
  await entry.update({ disabled: false })
  await ctx.loader.await()
  await entry.fiber?.await()
  const next = await standardAgentOn(ctx, 'browser-after-enable')
  expect(ctx.tools.schemas(existing.agent)).toEqual([])
  expect(ctx.tools.schemas(next.agent).map(tool => tool.name)).toContain('mcp__playwright-mcp__browser_navigate')
  await existing.dispose()
  const resumed = await standardAgentOn(ctx, 'browser-existing')
  expect(ctx.tools.schemas(resumed.agent).map(tool => tool.name)).toContain('mcp__playwright-mcp__browser_navigate')
})
