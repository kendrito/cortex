/**
 * The staged card form: what a draft shows before it is written, which wire
 * call a save reaches, and what happens to drafts the Host did not accept.
 */

import { describe, expect, it, vi } from 'vitest'
import { stubSettingsScope, type StubSettingsScope } from '@cortex/client-test-runtime'
import { CardForm, numberField, textField } from '../src/client/card-form.ts'
import { AgentLoopCardController, type AgentLoopSettings } from '../src/client/agent-loop-card-controller.ts'
import { BashCardController, type BashSettings } from '../src/client/bash-card-controller.ts'

/** Make the stub behave like a Host that accepts every write. */
function acceptWrites<T>(host: StubSettingsScope<T>): void {
  const section = (): Record<string, unknown> => ({ ...host.scope.getSnapshot().value as object })
  const layer = (): Record<string, unknown> => ({ ...host.scope.getSnapshot().user as object })
  host.set.mockImplementation((field: string, value: unknown) => {
    host.publish({ value: { ...section(), [field]: value } as T, user: { ...layer(), [field]: value } })
  })
  host.unset.mockImplementation((field: string) => {
    const user = Object.fromEntries(Object.entries(layer()).filter(([key]) => key !== field))
    const base = host.scope.getSnapshot().base as Record<string, unknown> | undefined
    host.publish({ value: { ...section(), [field]: base?.[field] } as T, user })
  })
}


describe('CardForm', () => {
  function form() {
    const host = stubSettingsScope<Record<string, unknown>>()
    const subject = new CardForm(host.scope, [numberField('timeoutMs'), textField('baseURL')])
    host.publish({
      status: 'ready',
      writable: true,
      value: { timeoutMs: 60_000, baseURL: 'https://search.test/v1' },
      base: { timeoutMs: 60_000, baseURL: 'https://search.test/v1' },
      user: {},
    })
    return { host, subject }
  }

  it('shows the effective value and stays clean until something is staged', () => {
    const { subject } = form()

    expect(subject.field('timeoutMs')).toEqual({ text: '60000', overridden: false, invalid: false })
    expect(subject.shell()).toMatchObject({ available: true, writable: true, dirty: false, invalid: false })
  })

  it('marks a field the user layer carries as overridden', () => {
    const { host, subject } = form()

    host.publish({ value: { timeoutMs: 60_000 }, user: { timeoutMs: 60_000 } })

    // An override equal to the composition default is still an override.
    expect(subject.field('timeoutMs').overridden).toBe(true)
  })

  it('writes nothing until the form is saved', async () => {
    const { host, subject } = form()
    acceptWrites(host)

    subject.actions().edit('timeoutMs', '9000')

    expect(subject.field('timeoutMs')).toEqual({ text: '9000', overridden: true, invalid: false })
    expect(subject.shell().dirty).toBe(true)
    expect(host.set).not.toHaveBeenCalled()

    await subject.save()

    expect(host.set.mock.calls).toEqual([['timeoutMs', 9_000]])
    expect(subject.shell()).toMatchObject({ dirty: false, failed: false, saving: false })
  })

  it('drops a draft that settles back on the value already shown', async () => {
    const { host, subject } = form()

    subject.actions().edit('timeoutMs', '9000')
    subject.actions().edit('timeoutMs', '60000')

    expect(subject.shell().dirty).toBe(false)
    await subject.save()

    expect(host.set).not.toHaveBeenCalled()
  })

  it('refuses to save while a draft is not a value the field accepts', async () => {
    const { host, subject } = form()

    subject.actions().edit('timeoutMs', 'soon')

    expect(subject.field('timeoutMs')).toEqual({ text: 'soon', overridden: false, invalid: true })
    expect(subject.shell()).toMatchObject({ dirty: true, invalid: true })

    await subject.save()

    expect(host.set).not.toHaveBeenCalled()
    expect(subject.field('timeoutMs').text).toBe('soon')
  })

  it('stages a reset that clears the field only once saved', async () => {
    const { host, subject } = form()
    acceptWrites(host)
    host.publish({ value: { timeoutMs: 9_000 }, user: { timeoutMs: 9_000 } })

    subject.actions().resetField('timeoutMs')

    // The badge previews the save: the field will no longer be overridden.
    expect(subject.field('timeoutMs')).toEqual({ text: '60000', overridden: false, invalid: false })
    expect(host.unset).not.toHaveBeenCalled()

    await subject.save()

    expect(host.unset.mock.calls).toEqual([['timeoutMs']])
    expect(subject.shell()).toMatchObject({ dirty: false, failed: false })
  })

  it('treats resetting an inherited field as no change at all', async () => {
    const { host, subject } = form()

    subject.actions().resetField('timeoutMs')

    expect(subject.shell().dirty).toBe(false)
    await subject.save()

    expect(host.unset).not.toHaveBeenCalled()
  })

  it('clears a number field by emptying it', async () => {
    const { host, subject } = form()
    acceptWrites(host)
    host.publish({ user: { timeoutMs: 9_000 } })

    subject.actions().edit('timeoutMs', '')

    expect(subject.field('timeoutMs')).toEqual({ text: '', overridden: false, invalid: false })
    await subject.save()

    expect(host.unset.mock.calls).toEqual([['timeoutMs']])
  })

  it('clears a text field by emptying it', async () => {
    const { host, subject } = form()
    acceptWrites(host)
    host.publish({ user: { baseURL: 'https://search.test/v1' } })

    subject.actions().edit('baseURL', '   ')
    await subject.save()

    expect(host.unset.mock.calls).toEqual([['baseURL']])
  })

  it('writes the trimmed text of a text field', async () => {
    const { host, subject } = form()
    acceptWrites(host)

    subject.actions().edit('baseURL', '  https://other.test  ')
    await subject.save()

    expect(host.set.mock.calls).toEqual([['baseURL', 'https://other.test']])
  })

  it('keeps the drafts a save did not land, and reports the failure', async () => {
    const { host, subject } = form()

    subject.actions().edit('timeoutMs', '9000')
    await subject.save()

    // The stub Host accepted the call without storing it, exactly as a
    // validator that refuses the value does.
    expect(host.set).toHaveBeenCalledWith('timeoutMs', 9_000)
    expect(subject.shell()).toMatchObject({ dirty: true, failed: true, saving: false })
    expect(subject.field('timeoutMs').text).toBe('9000')
  })

  it('reports a reset the Host did not apply as a failure', async () => {
    const { host, subject } = form()
    host.publish({ user: { timeoutMs: 9_000 } })

    subject.actions().resetField('timeoutMs')
    await subject.save()

    expect(host.unset).toHaveBeenCalledWith('timeoutMs')
    expect(subject.shell().failed).toBe(true)
  })

  it('clears the failure as soon as the user edits again', async () => {
    const { subject } = form()

    subject.actions().edit('timeoutMs', '9000')
    await subject.save()
    expect(subject.shell().failed).toBe(true)

    subject.actions().edit('timeoutMs', '9001')

    expect(subject.shell().failed).toBe(false)
  })

  it('discards every staged edit', async () => {
    const { host, subject } = form()

    subject.actions().edit('timeoutMs', '9000')
    subject.actions().discard()

    expect(subject.field('timeoutMs').text).toBe('60000')
    expect(subject.shell()).toMatchObject({ dirty: false, failed: false })

    // A discard with nothing staged publishes nothing.
    const before = subject.shell()
    subject.actions().discard()
    expect(subject.shell()).toEqual(before)

    await subject.save()
    expect(host.set).not.toHaveBeenCalled()
  })

  it('refuses a second save while one is in flight', async () => {
    const { host, subject } = form()
    acceptWrites(host)

    subject.actions().edit('timeoutMs', '9000')
    const first = subject.save()
    expect(subject.shell().saving).toBe(true)
    const second = subject.save()
    await Promise.all([first, second])

    expect(host.set).toHaveBeenCalledTimes(1)
  })

  it('publishes a projection whenever the scope or a draft changes', () => {
    const { host, subject } = form()
    const store = subject.bind(() => subject.field('timeoutMs').text)
    expect(store.getSnapshot()).toBe('60000')

    host.publish({ value: { timeoutMs: 1_000 } })
    expect(store.getSnapshot()).toBe('1000')

    subject.actions().edit('timeoutMs', '2000')
    expect(store.getSnapshot()).toBe('2000')
  })

  it('refuses to address a field the card never declared', () => {
    const { subject } = form()

    expect(() => subject.field('nope')).toThrow('plugin card has no field nope')
  })

  it('renders an absent section value as an empty draft', () => {
    const host = stubSettingsScope<Record<string, unknown>>()
    const subject = new CardForm(host.scope, [numberField('timeoutMs'), textField('baseURL')])

    host.publish({ status: 'ready', writable: true, value: {}, base: {}, user: undefined })

    expect(subject.field('timeoutMs').text).toBe('')
    expect(subject.field('baseURL').text).toBe('')
    expect(subject.shell().available).toBe(true)
  })

  it('stays unavailable while the namespace is not served', () => {
    const host = stubSettingsScope<Record<string, unknown>>()
    const subject = new CardForm(host.scope, [numberField('timeoutMs')])

    host.publish({ status: 'unavailable' })

    expect(subject.shell()).toMatchObject({ available: false, writable: false })
  })
})

describe('BashCardController', () => {
  it('projects both fields and saves them in one write pass', async () => {
    const host = stubSettingsScope<BashSettings>()
    acceptWrites(host)
    const controller = new BashCardController(host.scope)
    host.publish({
      status: 'ready',
      writable: true,
      value: { timeoutMs: 5_000, maxOutputBytes: 64_000 },
      base: { timeoutMs: 60_000, maxOutputBytes: 64_000 },
      user: { timeoutMs: 5_000 },
    })
    const face = controller.inject()

    expect(face.hooks.bashCard.getSnapshot()).toMatchObject({
      available: true,
      writable: true,
      dirty: false,
      timeoutMs: { text: '5000', overridden: true },
      maxOutputBytes: { text: '64000', overridden: false },
    })

    face.edit('timeoutMs', '9000')
    face.edit('maxOutputBytes', '1024')
    expect(face.hooks.bashCard.getSnapshot().dirty).toBe(true)

    face.save()
    await vi.waitFor(() => { expect(host.set).toHaveBeenCalledTimes(2) })

    expect(host.set.mock.calls).toEqual([['timeoutMs', 9_000], ['maxOutputBytes', 1_024]])
    expect(face.hooks.bashCard.getSnapshot().dirty).toBe(false)
  })

  it('stages a reset and applies it on save', async () => {
    const host = stubSettingsScope<BashSettings>()
    acceptWrites(host)
    const controller = new BashCardController(host.scope)
    host.publish({
      status: 'ready',
      writable: true,
      value: { timeoutMs: 5_000 },
      base: { timeoutMs: 60_000 },
      user: { timeoutMs: 5_000 },
    })
    const face = controller.inject()

    face.resetField('timeoutMs')
    expect(face.hooks.bashCard.getSnapshot().timeoutMs.text).toBe('60000')

    face.save()
    await vi.waitFor(() => { expect(host.unset).toHaveBeenCalledWith('timeoutMs') })

    expect(face.hooks.bashCard.getSnapshot()).toMatchObject({
      dirty: false,
      timeoutMs: { text: '60000', overridden: false },
    })
  })

  it('discards staged edits without writing', () => {
    const host = stubSettingsScope<BashSettings>()
    const controller = new BashCardController(host.scope)
    host.publish({ status: 'ready', writable: true, value: { timeoutMs: 5_000 }, user: {} })
    const face = controller.inject()

    face.edit('timeoutMs', '9000')
    face.discard()

    expect(face.hooks.bashCard.getSnapshot().timeoutMs.text).toBe('5000')
    expect(host.set).not.toHaveBeenCalled()
  })
})

describe('AgentLoopCardController', () => {
  it('saves the only field it owns', async () => {
    const host = stubSettingsScope<AgentLoopSettings>()
    acceptWrites(host)
    const controller = new AgentLoopCardController(host.scope)
    host.publish({
      status: 'ready',
      writable: true,
      value: { maxParallelToolCalls: 10 },
      base: { maxParallelToolCalls: 10 },
      user: {},
    })
    const face = controller.inject()

    face.edit('maxParallelToolCalls', '4')
    face.save()
    await vi.waitFor(() => { expect(host.set).toHaveBeenCalledWith('maxParallelToolCalls', 4) })

    expect(face.hooks.agentLoopCard.getSnapshot()).toMatchObject({
      dirty: false,
      maxParallelToolCalls: { text: '4', overridden: true },
    })
  })

  it('reports a read-only document so the card can disable its controls', () => {
    const host = stubSettingsScope<AgentLoopSettings>()
    const controller = new AgentLoopCardController(host.scope)

    host.publish({ status: 'ready', writable: false, value: { maxParallelToolCalls: 10 } })

    expect(controller.inject().hooks.agentLoopCard.getSnapshot().writable).toBe(false)
  })
})
