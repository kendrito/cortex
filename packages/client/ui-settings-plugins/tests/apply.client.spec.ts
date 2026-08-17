/** What the browser half registers, and that it all leaves with the fiber. */

import { Context } from '@cortex/cordis'
import { describe, expect, it, vi } from 'vitest'
import { resolveSlotLabel } from '@cortex/client-ui-slots'
import { SlotRegistry } from '@cortex/client-runtime/client'
import { LocaleRuntime } from '@cortex/client-locale/client'
import { TestRemote, usePinnedBrowserLanguages } from '@cortex/client-test-runtime'
import { SettingsScopeBinder } from '@cortex/client-ui-settings/client'
import { apply, inject } from '@cortex/client-ui-settings-plugins/client'
import type {
  ConfigurablePluginsTabInjected, PluginsSettingsSectionInjected,
} from '@cortex/client-ui-settings-plugins/client'

// The service reads its initial locale from the browser; these specs assert
// the shipped copy, so they state the browser they assume.
usePinnedBrowserLanguages('en-US')

async function bench() {
  const ctx = new Context()
  await ctx.plugin(SlotRegistry).await()
  const locale = new LocaleRuntime(ctx)
  ctx.provide('locale', locale)
  const describeCredentials = vi.fn(() => Promise.resolve({ rpcId: 'c', result: { ok: false, error: {} } }))
  // The section binds its scopes through the Settings surface's service, and
  // forwarded Host events reach it through the same `$dispatch` handoff the
  // connection sink makes.
  new TestRemote(ctx)
  ctx.provide('connection', {
    isLoopback: true,
    api: {
      settings: { describe: vi.fn(() => Promise.resolve({ rpcId: 's', result: { ok: false, error: {} } })) },
      credentials: { describe: describeCredentials },
    },
  } as never)
  await ctx.plugin(SettingsScopeBinder).await()
  return { ctx, slots: ctx.get('slots') as SlotRegistry, describeCredentials }
}

function declareRoot(slots: SlotRegistry): () => void {
  return slots.register({
    name: 'root',
    children: { 'settings.section': { kind: 'list', scope: 'root' } },
  } as never, () => null)
}

describe('ui-settings-plugins apply', () => {
  it('declares the services it uses', () => {
    expect(inject).toEqual(['slots', 'locale', 'connection', 'remote', 'settingsScope'])
  })

  it('registers one Plugins section and declares the tab and card slots', async () => {
    const { ctx, slots } = await bench()
    declareRoot(slots)

    await ctx.plugin({ inject: [...inject], apply }).await()

    const section = slots.entries('settings.section')[0]!
    expect(section.options).toMatchObject({ id: 'plugins', order: 15 })
    // The nav label is a locale-following thunk; owners resolve it at read time.
    expect(resolveSlotLabel(section.options.label)).toBe('Plugins')
    expect(slots.spec('settings.plugins.tab')).toMatchObject({ kind: 'list', scope: 'root' })
    const tab = slots.entries('settings.plugins.tab')[0]!
    expect(tab.options).toMatchObject({ id: 'configurable', order: 0 })
    expect(resolveSlotLabel(tab.options.label)).toBe('Plugin configuration')
    expect(slots.spec('settings.plugin.item')).toMatchObject({ kind: 'list', scope: 'root' })
  })

  it('registers one card per host-plane section it ships, in a stable order', async () => {
    const { ctx, slots } = await bench()
    declareRoot(slots)

    await ctx.plugin({ inject: [...inject], apply }).await()

    expect(slots.entries('settings.plugin.item').map(entry => entry.options.id))
      .toEqual(['bash', 'agent-loop'])
  })

  it('injects a live tab projection, a card count, and one business face per card', async () => {
    const { ctx, slots } = await bench()
    declareRoot(slots)
    await ctx.plugin({ inject: [...inject], apply }).await()

    const section = slots.entries('settings.section')[0]!
    const sectionFace = (section.inject as unknown as () => PluginsSettingsSectionInjected)()
    const initialTabs = sectionFace.hooks.tabs.getSnapshot()
    expect(initialTabs).toEqual([
      { id: 'configurable', order: 0, label: 'Plugin configuration' },
    ])
    expect(sectionFace.hooks.tabs.getSnapshot()).toBe(initialTabs)

    const listener = vi.fn()
    const unsubscribe = sectionFace.hooks.tabs.subscribe(listener)
    slots.register({ name: 'settings.plugins.tab', id: 'plain' } as never, () => null)
    expect(sectionFace.hooks.tabs.getSnapshot()).toEqual([
      { id: 'configurable', order: 0, label: 'Plugin configuration' },
      { id: 'plain', order: 0, label: '' },
    ])
    unsubscribe()

    const tab = slots.entries('settings.plugins.tab')[0]!
    expect((tab.inject as unknown as () => ConfigurablePluginsTabInjected)()).toEqual({ cardCount: 2 })
    for (const entry of slots.entries('settings.plugin.item')) {
      const face = (entry as { inject?: () => unknown }).inject?.() as { hooks: Record<string, unknown> }
      // Each card injects exactly one snapshot store plus its own actions.
      expect(Object.keys(face.hooks)).toHaveLength(1)
    }
  })

  it('reads no credential: neither card watches a reference, so a credential change reaches nothing', async () => {
    const { ctx, slots, describeCredentials } = await bench()
    declareRoot(slots)
    await ctx.plugin({ inject: [...inject], apply }).await()
    expect(describeCredentials).not.toHaveBeenCalled()

    // A key written on another surface changes no settings section, and no
    // shipped card holds a secret field, so the event finds no subscriber.
    ctx.remote.$dispatch('credentials/updated', ['SOME_KEY'])
    await Promise.resolve()

    expect(describeCredentials).not.toHaveBeenCalled()
  })

  it('registers into a declaration that arrives after apply', async () => {
    const { ctx, slots } = await bench()
    await ctx.plugin({ inject: [...inject], apply }).await()

    declareRoot(slots)

    await vi.waitFor(() => { expect(slots.entries('settings.section')).toHaveLength(1) })
  })

  it('collapses every contribution on teardown', async () => {
    const { ctx, slots } = await bench()
    declareRoot(slots)
    const fiber = ctx.plugin({ inject: [...inject], apply })
    await fiber.await()
    expect(slots.entries('settings.plugin.item')).toHaveLength(2)

    await fiber.dispose()

    expect(slots.entries('settings.section')).toHaveLength(0)
    expect(slots.spec('settings.plugins.tab')).toBeUndefined()
    expect(slots.spec('settings.plugin.item')).toBeUndefined()
  })
})
