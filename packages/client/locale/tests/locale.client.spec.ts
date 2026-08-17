// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Context } from '@cortex/cordis'
import { stubSettingsScope, type StubSettingsScope } from '@cortex/client-test-runtime'
import type { LocaleSettings, LocaleSnapshot } from '@cortex/client-locale/client'
import { LocaleRuntime } from '@cortex/client-locale/client'

const make = (host?: StubSettingsScope<LocaleSettings>): {
  ctx: Context
  svc: LocaleRuntime
  events: LocaleSnapshot[]
} => {
  const ctx = new Context()
  const events: LocaleSnapshot[] = []
  ctx.on('locale/change', (snapshot) => { events.push(snapshot) })
  return { ctx, svc: new LocaleRuntime(ctx, host?.scope), events }
}

/**
 * Pin the browser environment a fresh service reads its initial locale from.
 * This package's own specs stub the globals directly instead of using
 * `usePinnedBrowserLanguages` (cortex-client-test-runtime): they need the shapes
 * that helper deliberately cannot express — a missing `languages` list, a
 * list decoupled from `language`, and a non-browser run with no `window`.
 */
const stubLanguages = (...tags: string[]): void => {
  vi.stubGlobal('navigator', { languages: tags, language: tags[0] ?? '' })
}

describe('LocaleRuntime', () => {
  beforeEach(() => {
    // A browser asking for an unshipped language is the baseline: the service
    // opens on the en fallback, and every spec asserts its en state from there.
    stubLanguages('fr-FR')
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('translates through the active-locale -> en -> key chain', () => {
    const { svc } = make()
    // A dictionary for a locale this app does not ship is inert: it is neither
    // the active locale nor the fallback, so the chain never reads it.
    svc.register('ns', 'fr', { hello: 'Bonjour', onlyFr: 'Seulement en francais' })
    svc.register('ns', 'en', { hello: 'Hello' })
    const t = svc.bind('ns')
    expect(svc.getLocale().active).toBe('en')
    expect(t('hello')).toBe('Hello')
    expect(t('onlyFr')).toBe('onlyFr')
    expect(t('missing.key')).toBe('missing.key')
  })

  it('falls through to the common vocabulary after the namespace misses (production keys)', () => {
    const { svc } = make()
    // The shipped common dictionary is registered by apply; the bench registers it
    // directly to pin the production chain: ns -> common -> en -> key.
    svc.register('common', 'en', { retry: 'Retry' })
    svc.register('ns', 'en', { own: 'Own text' })
    const t = svc.bind('ns')
    expect(t('retry')).toBe('Retry')
    expect(t('own')).toBe('Own text')
    // common itself must not recurse: a miss inside common echoes the key.
    // (Wide-string ns hits the untyped bind overload — the typed one rejects
    // unknown keys at compile time, which is the point of the typed registry contract.)
    expect(svc.bind('common' as string)('nope')).toBe('nope')
  })

  it('interpolates {name} params and leaves unknown placeholders intact', () => {
    const { svc } = make()
    svc.register('ns', 'en', { greet: 'Hello, {name}! Attempt {n}', partial: '{known} and {unknown}' })
    const t = svc.bind('ns')
    expect(t('greet', { name: 'World', n: 2 })).toBe('Hello, World! Attempt 2')
    expect(t('partial', { known: 'A' })).toBe('A and {unknown}')
  })

  it('bind returns a stable per-namespace function identity', () => {
    const { svc } = make()
    expect(svc.bind('a')).toBe(svc.bind('a'))
    expect(svc.bind('a')).not.toBe(svc.bind('b'))
  })

  it('rejects duplicate (ns, locale) and disposer only removes its own dict', () => {
    const { svc } = make()
    const dispose = svc.register('ns', 'en', { k: 'v1' })
    expect(() => svc.register('ns', 'en', { k: 'v2' })).toThrow('already has locale')
    dispose()
    const t = svc.bind('ns')
    expect(t('k')).toBe('k')
    svc.register('ns', 'en', { k: 'v2' })
    expect(t('k')).toBe('v2')
    dispose()
    expect(t('k')).toBe('v2')
  })

  it('serves the LocaleFace: snapshot revision moves on registration and disposal, subscribers fire, unsubscribe stops them', () => {
    const { svc } = make()
    const seen: number[] = []
    const off = svc.subscribe(() => { seen.push(svc.getSnapshot().revision) })
    expect(svc.getSnapshot()).toBe(svc.getLocale())
    const r0 = svc.getSnapshot().revision
    const dispose = svc.register('ns', 'en', { k: 'v' })
    expect(svc.getSnapshot().revision).toBe(r0 + 1)
    dispose()
    expect(seen).toEqual([r0 + 1, r0 + 2])
    // Re-selecting the already-active locale is not a change: no republish.
    svc.setLocale('en')
    expect(seen).toHaveLength(2)
    off()
    svc.register('other', 'en', { k: 'v' })
    expect(seen).toHaveLength(2)
  })

  it('isolates a throwing subscriber: the rest still see the new revision', () => {
    const { svc } = make()
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    try {
      const seen: number[] = []
      svc.subscribe(() => { throw new Error('boom') })
      svc.subscribe(() => { seen.push(svc.getSnapshot().revision) })
      svc.register('ns', 'en', { k: 'v' })
      expect(seen).toEqual([1])
      expect(spy).toHaveBeenCalledOnce()
    } finally {
      spy.mockRestore()
    }
  })

  it('register disposer republishes (mounted outlets drop the dead dictionary)', () => {
    const { svc } = make()
    const dispose = svc.register('ns', 'en', { k: 'v' })
    const before = svc.getSnapshot().revision
    dispose()
    expect(svc.getSnapshot().revision).toBe(before + 1)
    // Second run hits the idempotent arm: nothing removed, no republish.
    dispose()
    expect(svc.getSnapshot().revision).toBe(before + 1)
  })

  it('setLocale on the already-active locale is a no-op: no scope write, no event, same snapshot', () => {
    const host = stubSettingsScope<LocaleSettings>()
    const { svc, events } = make(host)
    const before = svc.getLocale()
    svc.setLocale('en')
    expect(svc.getLocale()).toBe(before)
    expect(svc.getLocale().active).toBe('en')
    expect(host.set).not.toHaveBeenCalled()
    expect(events).toHaveLength(0)
  })

  it('setLocale without a host scope stays process-local and accepts the shipped id', () => {
    const { svc, events } = make()
    svc.setLocale('en')
    expect(svc.getLocale().active).toBe('en')
    expect(events).toHaveLength(0)
  })

  it('throws on unknown locale ids', () => {
    const { svc } = make()
    expect(() => { svc.setLocale('fr') }).toThrow('not registered')
  })

  it('adopts a Host preference for the shipped locale without writing it back or republishing', () => {
    const host = stubSettingsScope<LocaleSettings>()
    const { svc, events } = make(host)
    host.publish({ status: 'ready', value: { preference: 'en' }, revision: 1, writable: true })
    expect(svc.getLocale().active).toBe('en')
    expect(events).toHaveLength(0)
    expect(host.set).not.toHaveBeenCalled()
    host.publish({ value: { preference: 'en' }, revision: 2 })
    expect(events).toHaveLength(0)
  })

  it('an absent Host preference leaves the browser-derived locale standing', () => {
    const host = stubSettingsScope<LocaleSettings>()
    const { svc, events } = make(host)
    host.publish({ status: 'ready', value: { preference: 'en' }, revision: 1, writable: true })
    expect(svc.getLocale().active).toBe('en')
    host.publish({ value: {}, revision: 2 })
    expect(svc.getLocale().active).toBe('en')
    expect(events).toHaveLength(0)
  })

  it('adopts a section already standing at construction and releases its subscription on dispose', async () => {
    const host = stubSettingsScope<LocaleSettings>()
    host.publish({ status: 'ready', value: { preference: 'en' }, revision: 1, writable: true })
    const { ctx, svc } = make(host)
    expect(svc.getLocale().active).toBe('en')
    expect(host.listenerCount()).toBe(1)
    await ctx.fiber.dispose()
    expect(host.listenerCount()).toBe(0)
  })

  it('opens provisionally in the browser language, matching regional variants on their primary subtag', () => {
    stubLanguages('en-GB', 'fr-FR')
    expect(make().svc.getLocale().active).toBe('en')
    stubLanguages('en-US')
    expect(make().svc.getLocale().active).toBe('en')
    // An unshipped language walks the list to the first one this app ships.
    stubLanguages('fr-FR', 'en-US')
    expect(make().svc.getLocale().active).toBe('en')
    // Only `language` populated: an empty ordered list, and a host that
    // exposes no `languages` property at all.
    vi.stubGlobal('navigator', { languages: [], language: 'en-US' })
    expect(make().svc.getLocale().active).toBe('en')
    vi.stubGlobal('navigator', { language: 'en-US' })
    expect(make().svc.getLocale().active).toBe('en')
    // No shipped language anywhere in the browser's preferences: en remains
    // the product default rather than an arbitrary near-match.
    stubLanguages('fr-FR', 'de')
    expect(make().svc.getLocale().active).toBe('en')
  })

  it('runs outside a browser (node boots): the fallback decides and the machine language does not', () => {
    vi.stubGlobal('window', undefined)
    // Node exposes its own global navigator; without a window it must not
    // reach the resolution at all.
    stubLanguages('en-US')
    const { svc } = make()
    expect(svc.getLocale().active).toBe('en')
    svc.setLocale('en')
    expect(svc.getLocale().active).toBe('en')
  })

  it('exposes the single shipped locale with its self-described label', () => {
    const { svc } = make()
    expect(svc.getLocale().locales).toEqual([
      { id: 'en', label: 'English' },
    ])
  })
})
