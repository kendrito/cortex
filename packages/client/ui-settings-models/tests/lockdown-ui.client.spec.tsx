// @vitest-environment jsdom
/** Shipped Cortex policy over the real Models components and schema operations. */
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Schema from '@cortex/schemastery'
import { Context } from '@cortex/cordis'
import { bindSnapshotSelector } from '@cortex/client-test-runtime'
import type { JsonValue } from '@cortex/util-values'
import type { SettingsNamespaceView } from '@cortex/api-remotes/client'
import { SettingsDescribeMirror } from '@cortex/client-ui-settings/src/client/settings-mirror.ts'
import { CustomProviderCard } from '../src/client/CustomProviderCard.tsx'
import { ModelsSection } from '../src/client/ModelsSection.tsx'
import { ProviderEditor } from '../src/client/ProviderEditor.tsx'
import { ModelsSettingsStore } from '../src/client/store.ts'
import type { ModelsOperations } from '../src/client/operations.ts'
import { en } from '../src/client/locales.ts'
import { settingsSchema } from './settings-schema.client.ts'

afterEach(cleanup)
const t = (key: keyof typeof en): string => en[key]
const protocols = ['openai-completions', 'openai-responses', 'anthropic-messages']
const config = Schema.object({ providers: Schema.dict(Schema.object({
  baseURL: Schema.string(), api: Schema.union(protocols),
  models: Schema.array(Schema.object({ id: Schema.string().required() })),
})) })

function namespace(providers: Record<string, JsonValue>): SettingsNamespaceView {
  return {
    ns: 'llm-pi-ai', schema: JSON.parse(JSON.stringify(config.toJSON())) as JsonValue,
    value: { providers }, user: { providers }, base: { providers: {} },
    autoGenerate: true, applies: 'live', secrets: [], revision: 1,
  }
}

function operations(view: SettingsNamespaceView) {
  return {
    describeCredential: vi.fn(async () => undefined),
    storeCredential: vi.fn(async () => undefined),
    removeCredential: vi.fn(async () => undefined),
    writeSettings: vi.fn<ModelsOperations['writeSettings']>(async () => ({ kind: 'written', view })),
    discoverModels: vi.fn<ModelsOperations['discoverModels']>(async () => ({ kind: 'found', models: [] })),
  } satisfies ModelsOperations
}

describe('Cortex Models UI policy', () => {
  it('locks external rows while retaining the editable local route and custom add flow', () => {
    const view = namespace({
      external: { baseURL: 'https://example.com/v1', api: 'openai-completions' },
      local: { baseURL: 'http://127.0.0.1:4000/v1', api: 'openai-completions' },
    })
    const ctx = new Context()
    const controller = new ModelsSettingsStore(ctx, settingsSchema, new SettingsDescribeMirror(ctx))
    controller.store.set({
      status: 'ready', writable: true, error: null, credentialError: null,
      namespaces: new Map([[view.ns, view]]),
      rows: ['external', 'local'].map(provider => ({
        entry: { provider, displayName: provider, settingsNs: view.ns,
          settingsPath: ['providers', provider], active: true, declared: true },
        configured: true, removable: true, apiKeyEnv: undefined, credential: undefined,
      })),
    })
    render(<ModelsSection controller={controller} useSnapshot={bindSnapshotSelector(controller.store)}
      operations={operations(view)} schema={settingsSchema} t={t} renderSlot={() => null} />)
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Edit external' }).disabled).toBe(true)
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Delete external' }).disabled).toBe(true)
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Edit local' }).disabled).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: en.add }))
    expect(screen.queryByRole('tab', { name: en.addCatalog })).toBeNull()
    const choices = within(screen.getByLabelText(en.customApi)).getAllByRole<HTMLOptionElement>('option')
    expect(choices.map(option => option.value)).toEqual(['openai-completions', 'openai-responses'])
  })

  it('refuses external model discovery and creation, then permits a local gateway', async () => {
    const host = operations(namespace({}))
    render(<CustomProviderCard taken={[]} protocols={protocols} revision={1}
      operations={host} t={t} readOnly={false} onClose={() => {}} />)
    fireEvent.change(screen.getByLabelText(en.customRoute), { target: { value: 'local' } })
    fireEvent.change(screen.getByLabelText(en.baseUrl), { target: { value: 'https://example.com/v1' } })
    fireEvent.click(screen.getByRole('button', { name: en.addModel }))
    fireEvent.change(screen.getByLabelText(`${en.modelId} 1`), { target: { value: 'local-model' } })
    const fetch = screen.getByRole<HTMLButtonElement>('button', { name: en.fetchModels })
    const create = screen.getByRole<HTMLButtonElement>('button', { name: en.create })
    expect(fetch.disabled).toBe(true)
    expect(create.disabled).toBe(true)
    fireEvent.click(fetch)
    fireEvent.click(create)
    expect(host.discoverModels).not.toHaveBeenCalled()
    expect(host.writeSettings).not.toHaveBeenCalled()
    fireEvent.change(screen.getByLabelText(en.baseUrl), { target: { value: 'http://localhost:4000/v1' } })
    expect(create.disabled).toBe(false)
    fireEvent.click(create)
    await waitFor(() => { expect(host.writeSettings).toHaveBeenCalledOnce() })
    expect(host.writeSettings.mock.calls[0]?.[1][0]).toMatchObject({
      value: { baseURL: 'http://localhost:4000/v1', api: 'openai-completions' },
    })
  })

  it('blocks an edited external endpoint and clearing the local endpoint', () => {
    const view = namespace({ local: { baseURL: 'http://localhost:4000/v1', api: 'openai-completions' } })
    const host = operations(view)
    render(<ProviderEditor provider="local" displayName="Local" declared namespace={view}
      schema={settingsSchema} settingsPath={['providers', 'local']} operations={host}
      t={t} readOnly={false} onClose={() => {}} />)
    fireEvent.click(screen.getByText(en.customized))
    const endpoint = screen.getByLabelText(en.baseUrl)
    fireEvent.change(endpoint, { target: { value: 'https://example.com/v1' } })
    expect(screen.getByRole<HTMLButtonElement>('button', { name: en.apply }).disabled).toBe(true)
    expect(screen.getByRole<HTMLButtonElement>('button', { name: en.fetchModels }).disabled).toBe(true)
    fireEvent.change(endpoint, { target: { value: '' } })
    expect(screen.getByRole<HTMLButtonElement>('button', { name: en.apply }).disabled).toBe(true)
    expect(host.writeSettings).not.toHaveBeenCalled()
  })
})
