/** Real browser coverage of Cortex's local gateway policy and credential persistence. */
import { createServer, type Server } from 'node:http'
import { readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import type { Browser, Page } from 'playwright'
import { chromium } from 'playwright'
import { afterAll, beforeAll, describe, expect, it, onTestFailed } from 'vitest'
import {
  assertFixtureInventory, captureStableAria, compareOrRefreshGolden,
  launchWebScaffold, watchConsole, webSnapshotMode, type WebScaffold,
} from './scaffold.ts'
import { openSettings, newEnglishPage, saveFailureShot } from './support.ts'

const SNAPSHOT_DIR = fileURLToPath(new URL('./expected/models-settings', import.meta.url))
const MODE = webSnapshotMode()
const LOCAL = 'LiteLLM (litellm)'
const ADMIN = 'Administrator route (cortex-admin-fixture)'

describe('web e2e: Models settings configures only the local gateway', () => {
  let scaffold: WebScaffold
  let browser: Browser
  let page: Page
  let gateway: Server
  let baseURL: string
  let discoveries = 0
  let tripwire: ReturnType<typeof watchConsole>
  const settings = () => page.getByRole('dialog', { name: 'Settings', exact: true })
  const profile = () => readFile(join(scaffold.harnessHome, 'profiles', 'scaffold', 'cordis.patch.yml'), 'utf8')
  const credentialFile = () => readFile(join(scaffold.harnessHome, '.credentials.yaml'), 'utf8').catch(() => '')
  const snapshot = async (name: string, selector = '[role="dialog"][data-shortcut-modal="settings"]') => {
    await compareOrRefreshGolden(join(SNAPSHOT_DIR, `${name}.expected.md`),
      await captureStableAria(page, selector, scaffold.workspaceCwd, { replacements: [[baseURL, '{{localGateway}}']] }), MODE)
  }

  beforeAll(async () => {
    gateway = createServer((request, response) => {
      if (request.url !== '/v1/models') { response.writeHead(404).end(); return }
      discoveries++
      response.writeHead(200, { 'Content-Type': 'application/json' })
      response.end(JSON.stringify({ object: 'list', data: [{ id: 'gpt-local' }, { id: 'gpt-vision' }] }))
    })
    await new Promise<void>((resolve, reject) => { gateway.once('error', reject); gateway.listen(0, '127.0.0.1', resolve) })
    const address = gateway.address()
    if (address === null || typeof address === 'string') throw new Error('Local model catalog did not bind TCP')
    baseURL = `http://127.0.0.1:${String(address.port)}/v1`
    scaffold = await launchWebScaffold({ emptyProviders: true })
    browser = await chromium.launch()
    page = await newEnglishPage(browser)
    page.setDefaultTimeout(10_000)
    tripwire = watchConsole(page)
    await page.goto(scaffold.authenticatedUrl, { waitUntil: 'load' })
    await page.getByRole('button', { name: 'Settings', exact: true }).waitFor({ timeout: 30_000 })
    await openSettings(page, 'en')
    await settings().getByRole('button', { name: 'Models', exact: true }).click()
  }, 120_000)

  afterAll(async () => {
    await browser?.close()
    await scaffold?.close()
    if (gateway !== undefined) await new Promise<void>((resolve, reject) => {
      gateway.close((error) => { if (error) reject(error); else resolve() })
    })
  })

  it('offers only the custom local OpenAI-compatible flow', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-models-empty'))
    await settings().getByText('Provider configuration is limited to a local LiteLLM gateway. Administrator-provisioned external routes are read-only.').waitFor()
    await settings().getByRole('button', { name: 'Add model provider', exact: true }).click()
    expect(await settings().getByRole('tab', { name: 'Third-party model provider' }).count()).toBe(0)
    const protocol = settings().getByLabel('API protocol', { exact: true })
    expect(await protocol.locator('option').evaluateAll(options => options.map(option => (option as HTMLOptionElement).value)))
      .toEqual(['openai-completions', 'openai-responses'])
    await snapshot('empty')
  })

  it('blocks remote discovery, writes, and malformed keys before allowing a local route', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-models-guards'))
    const dialog = settings()
    await dialog.getByLabel('Provider ID', { exact: true }).fill('litellm')
    await dialog.getByLabel('Display name', { exact: true }).fill('LiteLLM')
    await dialog.getByLabel('Base URL', { exact: true }).fill('https://gateway.invalid/v1')
    await dialog.getByRole('button', { name: 'Add model', exact: true }).click()
    await dialog.getByLabel('Model ID 1', { exact: true }).fill('gpt-local')
    await dialog.getByText('Use a localhost OpenAI-compatible endpoint, such as http://127.0.0.1:4000/v1.').first().waitFor()
    expect(await dialog.getByRole('button', { name: 'Fetch available models', exact: true }).isDisabled()).toBe(true)
    const create = dialog.getByRole('button', { name: 'Create provider', exact: true })
    expect(await create.isDisabled()).toBe(true)
    expect(discoveries).toBe(0)
    expect(await profile()).not.toContain('litellm:')
    await dialog.getByLabel('Base URL', { exact: true }).fill(baseURL)
    await dialog.getByLabel('API key', { exact: true }).fill('sk-\u{1F600}litellm')
    await dialog.getByText('This API key is not in a valid format. Please check it.').waitFor()
    expect(await create.isDisabled()).toBe(true)
    await dialog.getByLabel('API key', { exact: true }).fill('')
    await expect.poll(() => create.isEnabled()).toBe(true)
    await create.click()
    await dialog.getByRole('button', { name: `Edit ${LOCAL}`, exact: true }).waitFor()
    expect(await profile()).toContain('litellm:')
    expect(await profile()).toContain(`baseURL: ${baseURL}`)
    expect(await profile()).not.toContain('LITELLM_API_KEY')
  }, 60_000)

  it('keeps discovered models and writes credentials without echoing the secret', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-models-persist'))
    const dialog = settings()
    await dialog.getByRole('button', { name: `Edit ${LOCAL}`, exact: true }).click()
    await dialog.getByLabel('API key', { exact: true }).fill('sk-e2e-litellm')
    await dialog.getByText('Customized settings', { exact: true }).click()
    await dialog.getByRole('button', { name: 'Fetch available models', exact: true }).click()
    const picker = page.getByRole('dialog', { name: 'Choose models to add', exact: true })
    await picker.waitFor()
    expect(discoveries).toBeGreaterThan(0)
    await picker.getByRole('searchbox', { name: 'Search models', exact: true }).fill('vision')
    await picker.getByRole('checkbox', { name: 'gpt-vision', exact: true }).check()
    await picker.getByRole('button', { name: 'Add selected', exact: true }).click()
    await dialog.getByRole('button', { name: 'Apply', exact: true }).click()
    await dialog.getByLabel('API key', { exact: true }).waitFor({ state: 'detached' })
    await expect.poll(credentialFile).toContain('LITELLM_API_KEY: sk-e2e-litellm')
    const document = await profile()
    expect(document).toContain('apiKeyEnv: LITELLM_API_KEY')
    expect(document).toContain('gpt-vision')
    expect(document).not.toContain('sk-e2e-litellm')
    expect(await page.content()).not.toContain('sk-e2e-litellm')
  }, 60_000)

  it('blocks external or cleared URLs during editing and locks administrator routes', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-models-lockdown'))
    const dialog = settings()
    await dialog.getByRole('button', { name: `Edit ${LOCAL}`, exact: true }).click()
    await dialog.getByText('Customized settings', { exact: true }).click()
    const endpoint = dialog.getByLabel('Base URL', { exact: true })
    const before = await profile()
    for (const invalid of ['https://gateway.invalid/v1', '']) {
      await endpoint.fill(invalid)
      expect(await dialog.getByRole('button', { name: 'Apply', exact: true }).isDisabled()).toBe(true)
      expect(await dialog.getByRole('button', { name: 'Fetch available models', exact: true }).isDisabled()).toBe(true)
    }
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
    expect(await profile()).toBe(before)
    await scaffold.ctx.settings.mutate('llm-pi-ai', [{ op: 'set', path: ['providers', 'cortex-admin-fixture'], value: {
      displayName: 'Administrator route', baseURL: 'https://admin.invalid/v1', api: 'openai-completions', models: [{ id: 'admin-model' }],
    } }])
    const edit = dialog.getByRole('button', { name: `Edit ${ADMIN}`, exact: true })
    await edit.waitFor()
    expect(await edit.isDisabled()).toBe(true)
    expect(await dialog.getByRole('button', { name: `Delete ${ADMIN}`, exact: true }).isDisabled()).toBe(true)
    expect(await dialog.getByRole('button', { name: `Edit ${LOCAL}`, exact: true }).isEnabled()).toBe(true)
    await snapshot('configured')
  }, 60_000)

  it('requires confirmation and deletes only the local route and its managed key', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-models-delete'))
    const dialog = settings()
    await dialog.getByRole('button', { name: `Delete ${LOCAL}`, exact: true }).click()
    const confirmation = page.getByRole('dialog', { name: `Delete ${LOCAL}?`, exact: true })
    await confirmation.waitFor()
    await snapshot('delete', `[role="dialog"][aria-label="Delete ${LOCAL}?"]`)
    await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click()
    expect(await profile()).toContain('litellm:')
    await dialog.getByRole('button', { name: `Delete ${LOCAL}`, exact: true }).click()
    await confirmation.getByRole('button', { name: `Delete ${LOCAL}`, exact: true }).click()
    await expect.poll(profile).not.toContain('litellm:')
    expect(await profile()).toContain('cortex-admin-fixture:')
    expect(await credentialFile()).not.toContain('LITELLM_API_KEY')
    expect(tripwire.pageErrors).toEqual([])
  }, 60_000)

  it.skipIf(MODE === 'record')('keeps the fixture inventory closed', async () => {
    await assertFixtureInventory(SNAPSHOT_DIR, ['configured.expected.md', 'delete.expected.md', 'empty.expected.md'])
  })
})
