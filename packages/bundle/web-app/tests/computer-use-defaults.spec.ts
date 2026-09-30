/** Shipped GUI desktop control uses the real Loader and live Agent scopes. */
import { assembleContextFor } from '@cortex/agent'
import ComputerUse from '@cortex/computer-use'
import { ComputerUseProviderName } from '@cortex/computer-use/brand'
import * as NativeProvider from '@cortex/experimental-computer-use-cua-driver-native'
import { ToolCallId } from '@cortex/llm'
import { expect, it, onTestFinished, vi } from 'vitest'
import { fixture, resetFixture } from '../../../experimental/computer-use-cua-driver-native/tests/fixtures/cua-driver.ts'
import { bootGuiCapability, standardAgentOn } from './fixtures/gui-capability.ts'

vi.mock('@trycua/cua-driver', async () => import('../../../experimental/computer-use-cua-driver-native/tests/fixtures/cua-driver.ts'))

async function boot(platform: NodeJS.Platform, disabled?: boolean, profile: string | undefined = 'web', bundles?: readonly string[]) {
  resetFixture()
  const originalPlatform = Object.getOwnPropertyDescriptor(process, 'platform')!
  Object.defineProperty(process, 'platform', { ...originalPlatform, value: platform })
  onTestFinished(() => { Object.defineProperty(process, 'platform', originalPlatform) })
  return bootGuiCapability({
    profile, ...bundles === undefined ? {} : { bundles },
    plugins: {
      '@cortex/computer-use': ComputerUse,
      '@cortex/experimental-computer-use-cua-driver-native': NativeProvider,
    },
    patches: disabled === undefined ? [] : [{ id: 'computer-use-cua-driver-native', disabled }],
  })
}

it.each(['win32', 'darwin', 'linux'] as const)('selects native desktop control by default only on Windows: %s', async (platform) => {
  const ctx = await boot(platform)
  expect([...ctx.loader.entries()].find(entry => entry.options.id === 'computer-use-cua-driver-native')!.disabled).toBe(platform !== 'win32')
  expect(ctx.computerUse.providerName).toBe(platform === 'win32' ? 'cua-driver-native' : undefined)
  expect(fixture.creates).toBe(platform === 'win32' ? 1 : 0)
})

it('publishes native tools and guidance into standard Agent scopes and respects provider exclusivity', async () => {
  const ctx = await boot('win32')
  const { agent } = await standardAgentOn(ctx, 'native-default')
  expect(ctx.tools.schemas(agent).map(tool => tool.name)).toContain('cua_driver_native__check_permissions')
  const prompt = await ctx.systemPrompt.assemble(assembleContextFor(agent))
  expect(prompt.sections.map(section => section.name)).toContain('computer-use:cua-driver-native')
  const result = await ctx.tools.execute({
    agent, name: 'cua_driver_native__check_permissions', arguments: {},
    callId: ToolCallId('permissions'), signal: new AbortController().signal,
  })
  expect(result.isError).toBe(false)
  expect(fixture.calls).toMatchObject([{ name: 'check_permissions', args: {} }])
  expect(() => ctx.computerUse.register(ComputerUseProviderName('second-driver'))).toThrow('already registered')
})

it('disables desktop control through a profile patch and updates existing and new session scopes on re-enable', async () => {
  const ctx = await boot('win32', true)
  const { agent: existing } = await standardAgentOn(ctx, 'already-open')
  expect(ctx.tools.schemas(existing)).toEqual([])
  expect(fixture.creates).toBe(0)
  const entry = [...ctx.loader.entries()].find(entry => entry.options.id === 'computer-use-cua-driver-native')!
  await entry.update({ disabled: false })
  await ctx.loader.await()
  await entry.fiber?.await()
  const { agent: newAgent } = await standardAgentOn(ctx, 'newly-open')
  for (const agent of [existing, newAgent]) {
    expect(ctx.tools.schemas(agent).map(tool => tool.name)).toContain('cua_driver_native__check_permissions')
  }
  await entry.update({ disabled: true })
  await ctx.loader.await()
  for (const agent of [existing, newAgent]) expect(ctx.tools.schemas(agent)).toEqual([])
  expect(ctx.computerUse.providerName).toBeUndefined()
  expect(fixture.shutdowns).toBe(1)
  expect(fixture.destroys).toBe(1)
})

it('enables native desktop control in the Windows Desktop profile', async () => {
  const ctx = await boot('win32', undefined, 'desktop')
  expect(ctx.computerUse.providerName).toBe('cua-driver-native')
})

it.each(['headless', 'sdk', 'sdk-minimal', 'acp'])('leaves the composed %s profile without desktop control', async (profile) => {
  const ctx = await boot('win32', undefined, profile)
  expect(ctx.get('computerUse')).toBeUndefined()
  expect(fixture.creates).toBe(0)
})

it.each(['headless', 'sdk', 'acp', 'custom-web'])('does not start the native driver if %s includes the Web bundle', async (profile) => {
  const ctx = await boot('win32', undefined, profile, ['@cortex/base', '@cortex/web-app'])
  expect([...ctx.loader.entries()].find(entry => entry.options.id === 'computer-use-cua-driver-native')!.disabled).toBe(true)
  expect(ctx.computerUse.providerName).toBeUndefined()
  expect(fixture.creates).toBe(0)
})
