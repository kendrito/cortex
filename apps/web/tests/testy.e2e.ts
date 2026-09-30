/** Real web composition, packaged native Testy, and an actual WPF target. */
import { existsSync } from 'node:fs'
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { chromium, type Browser, type Page } from 'playwright'
import { afterAll, beforeAll, describe, expect, it, onTestFailed } from 'vitest'
import type {} from '@cortex/testy'
import { NativeTestyAdapter } from '../../../packages/extensions/testy/tests/native-model-fixture.ts'
import { launchWebScaffold, watchConsole, type WebScaffold } from './scaffold.ts'
import { newEnglishPage, REPO_ROOT, saveFailureShot } from './support.ts'

const RUNTIME = join(REPO_ROOT, 'packages/extensions/testy/runtime/win-x64')
const LAB = join(RUNTIME, 'Testy.TestLab.exe')
const NAME = 'Packaged customer desk check'

describe.skipIf(process.platform !== 'win32' || !existsSync(LAB))('web e2e: packaged Testy', () => {
  let directory: string
  let scaffold: WebScaffold
  let browser: Browser
  let page: Page
  let tripwire: ReturnType<typeof watchConsole>
  let modelCalls = 0
  const adapter = new NativeTestyAdapter()
  const call = (name: string, args = {}) => scaffold.ctx.testy.call(name, args, undefined, new AbortController().signal)

  beforeAll(async () => {
    directory = await mkdtemp(join(tmpdir(), 'cortex-testy-browser-'))
    const overlay = join(directory, 'testy.yml')
    await writeFile(overlay, `- id: testy\n  disabled: false\n  config: ${JSON.stringify({ workspace: join(directory, 'data') })}\n`
      + '- id: agent-default-model\n  config: { provider: testy-browser, model: cortex-selected }\n')
    scaffold = await launchWebScaffold({ extraOverlayPath: overlay, emptyProviders: true })
    adapter.workflow = 'ai-test'
    scaffold.ctx.effect(() => scaffold.ctx.llm.registerAdapter(['testy-browser'], adapter))
    expect((await scaffold.ctx.testy.status()).connected).toBe(true)
    scaffold.ctx.on('llm/stream', async function* (_request, next) {
      modelCalls += 1
      yield* next()
    })
    await call('save_preferences', { liveReview: false })
    browser = await chromium.launch()
    page = await newEnglishPage(browser)
    tripwire = watchConsole(page)
    await page.goto(scaffold.authenticatedUrl, { waitUntil: 'load' })
    await page.getByRole('button', { name: 'Testy', exact: true }).click()
    await page.getByRole('heading', { name: 'Testy', exact: true }).waitFor()
    await page.getByText('cortex-selected', { exact: true }).first().waitFor()
    if (process.env.CORTEX_TEST_ARTIFACT_DIR) await page.screenshot({ path: join(process.env.CORTEX_TEST_ARTIFACT_DIR, 'cortex-testy-studio.png'), fullPage: true })
  }, 120_000)

  afterAll(async () => {
    await browser?.close()
    await scaffold?.close()
    if (directory) await rm(directory, { recursive: true, force: true })
  })

  it('authors and reloads a test, runs the packaged app, and displays real evidence without a model call', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-testy'))
    expect(scaffold.ctx.tools.get('mcp__testy__run_test')).toBeDefined()
    expect(scaffold.ctx.tools.get('mcp__testy__save_project')).toBeUndefined()
    await page.getByRole('button', { name: 'New test', exact: true }).click()
    await page.getByLabel('Test name', { exact: true }).fill(NAME)
    await page.getByLabel('What should this test verify?', { exact: true }).fill('The packaged WPF app exposes its customer form and ready state.')
    // Cortex normally finds the app; this model-free replay pins it through the technical-details override.
    await page.getByRole('switch', { name: 'Show technical details', exact: true }).check()
    await page.getByText('Test details', { exact: true }).click()
    await page.getByLabel('Application path', { exact: true }).fill(LAB)
    await page.getByRole('button', { name: 'Add check', exact: true }).click()
    await page.getByLabel('Step description', { exact: true }).fill('Customer name field is available')
    await page.getByLabel('Control selector', { exact: true }).fill('id:CustomerName')
    await page.getByRole('button', { name: 'Add check', exact: true }).click()
    await page.getByLabel('Step description', { exact: true }).fill('Customer desk is ready')
    await page.getByLabel('Action', { exact: true }).selectOption('assertText')
    await page.getByLabel('Control selector', { exact: true }).fill('id:StatusMessage')
    await page.getByLabel('Value', { exact: true }).fill('Ready for a new customer.')
    await page.getByRole('button', { name: 'Save changes', exact: true }).click()
    const saved = page.getByRole('button', { name: new RegExp(NAME) })
    await saved.waitFor()
    await page.reload({ waitUntil: 'load' })
    await page.getByRole('button', { name: 'Testy', exact: true }).click()
    await page.getByRole('button', { name: new RegExp(NAME) }).click()
    await expect.poll(() => page.getByLabel('Test name', { exact: true }).inputValue()).toBe(NAME)
    expect(await page.getByRole('button', { name: /Customer name field is available/ }).count()).toBe(1)
    expect(await page.getByRole('button', { name: /Customer desk is ready/ }).count()).toBe(1)
    await page.getByLabel('Run mode', { exact: true }).selectOption('replay')
    await page.getByRole('button', { name: 'Run test', exact: true }).click()
    await page.getByRole('heading', { name: 'Run trace', exact: true }).waitFor({ timeout: 45_000 })
    await expect.poll(() => page.locator('[data-status="passed"]').count(), { timeout: 45_000 }).toBeGreaterThan(0)
    await page.getByRole('img', { name: 'Step screenshot', exact: true }).waitFor({ timeout: 15_000 })
    expect(await page.getByRole('button', { name: 'Step 1', exact: true }).count()).toBe(1)
    expect(await page.getByRole('button', { name: 'Step 2', exact: true }).count()).toBe(1)
    const downloaded = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Download report', exact: true }).click()
    const report = await downloaded
    expect(report.suggestedFilename()).toMatch(/^testy-run-.+\.html$/)
    const reportPath = await report.path()
    const html = await readFile(reportPath, 'utf8')
    expect(html).toContain(NAME)
    expect(html).toContain('data:image/png;base64,')
    expect(html).not.toMatch(/<img[^>]+src=["'](?:https?:|file:|\/)/i)
    expect(modelCalls).toBe(0)
    expect(tripwire.pageErrors).toEqual([])
    if (process.env.CORTEX_TEST_ARTIFACT_DIR) await page.screenshot({ path: join(process.env.CORTEX_TEST_ARTIFACT_DIR, 'cortex-testy-results.png'), fullPage: true })
  }, 90_000)

  it('shows native automation forms and retains saved tests across navigation', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-testy-automation'))
    await page.getByRole('switch', { name: 'Show technical details', exact: true }).uncheck()
    await page.getByRole('tab', { name: 'Automate', exact: true }).click()
    await page.getByRole('button', { name: 'Test sets and data', exact: true }).click()
    await page.getByRole('button', { name: 'Save test set', exact: true }).click()
    await page.getByRole('textbox', { name: /^name\b/i }).fill('Packaged regression set')
    await page.getByRole('checkbox', { name: NAME, exact: true }).check()
    await page.getByRole('button', { name: 'Run operation', exact: true }).click()
    await page.getByRole('heading', { name: 'Operation result', exact: true }).waitFor()
    expect(JSON.stringify((await call('list_suites')).structuredContent)).toContain('Packaged regression set')
    if (process.env.CORTEX_TEST_ARTIFACT_DIR) await page.screenshot({ path: join(process.env.CORTEX_TEST_ARTIFACT_DIR, 'cortex-testy-automate.png'), fullPage: true })
    await page.getByRole('tab', { name: 'Tests', exact: true }).click()
    await expect.poll(() => page.getByLabel('Test name', { exact: true }).inputValue()).toBe(NAME)
    expect(tripwire.pageErrors).toEqual([])
  })

  it('reports a real failed assertion without changing the expected result', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-testy-failure'))
    await page.getByRole('tab', { name: 'Tests', exact: true }).click()
    await page.getByRole('tab', { name: /^Steps/ }).click()
    await page.getByRole('button', { name: /Customer desk is ready/ }).click()
    await page.getByLabel('Run mode', { exact: true }).selectOption('replay')
    await page.getByLabel('Value', { exact: true }).fill('This expected state does not exist.')
    await page.getByRole('button', { name: 'Run test', exact: true }).click()
    await expect.poll(() => page.locator('[data-status="failed"]').count(), { timeout: 45_000 }).toBeGreaterThan(0)
    await page.getByRole('img', { name: 'Step screenshot', exact: true }).waitFor()
    expect(modelCalls).toBe(0)
    expect(tripwire.pageErrors).toEqual([])
  }, 60_000)

  it('asks once for an ambiguous app, then creates and verifies an AI test from the description', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-testy-ai'))
    const sample = (await call('launch_sample', { sample: 'testlab' })).structuredContent
    if (sample === null || typeof sample !== 'object' || Array.isArray(sample) || typeof sample.pid !== 'number') {
      throw new Error('The native sample launcher did not return a process id.')
    }
    adapter.targetPid = sample.pid
    adapter.clarify = true
    await page.getByRole('tab', { name: 'Tests', exact: true }).click()
    await page.getByLabel('Describe a test', { exact: true }).fill('Verify the Testy Customer Desk is ready for a new customer.')
    await page.getByRole('button', { name: 'Test with AI', exact: true }).click()
    await page.getByRole('heading', { name: 'Which application should I test?', exact: true }).waitFor({ timeout: 30000 })
    expect(adapter.phases).toEqual(['target'])
    adapter.clarify = false
    await page.getByRole('button', { name: new RegExp(`^Testy TestLab — Customer Desk(?: \\(process ${sample.pid}\\))?$`) }).click()
    await page.getByRole('heading', { name: 'Run trace', exact: true }).waitFor({ timeout: 45000 })
    await expect.poll(() => adapter.phases, { timeout: 45000 }).toEqual(['target', 'draft', 'assert', 'complete'])
    await expect.poll(() => page.getByRole('button', { name: /AI ready-state check/ }).getAttribute('class'), { timeout: 45000 }).toContain('selected')
    await page.getByRole('img', { name: 'Step screenshot', exact: true }).waitFor({ timeout: 15000 })
    expect(adapter.requests.every(request => request.provider === 'testy-browser' && request.model === 'cortex-selected')).toBe(true)
    expect(tripwire.pageErrors).toEqual([])
    if (process.env.CORTEX_TEST_ARTIFACT_DIR) await page.screenshot({ path: join(process.env.CORTEX_TEST_ARTIFACT_DIR, 'cortex-testy-ai-results.png'), fullPage: true })
  }, 90_000)

  it('keeps the description composer visible in a smaller window and follows the Cortex dark theme', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-testy-small'))
    await page.setViewportSize({ width: 1280, height: 800 })
    await page.getByRole('tab', { name: 'Tests', exact: true }).click()
    const composer = page.getByRole('button', { name: 'Test with AI', exact: true })
    const bounds = await composer.boundingBox()
    expect(bounds).not.toBeNull()
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(800)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
    await page.emulateMedia({ colorScheme: 'dark' })
    await expect.poll(() => page.locator('body').getAttribute('data-ds-dark-theme')).not.toBeNull()
    expect(tripwire.pageErrors).toEqual([])
    if (process.env.CORTEX_TEST_ARTIFACT_DIR) await page.screenshot({ path: join(process.env.CORTEX_TEST_ARTIFACT_DIR, 'cortex-testy-dark-small.png'), fullPage: true })
  })
})
