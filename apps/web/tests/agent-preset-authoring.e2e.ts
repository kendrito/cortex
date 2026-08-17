// Web e2e scenario: the agent-preset settings section as copy-only authoring.
// The browser never edits composition text — a shipped preset opens in a
// read-only viewer, the copy dialog collects an id and an optional display
// name, and the host copies the whole directory. The section's other job is
// getting the user TO the files: this lane pins `nativeOpen: false` (see the
// overlay), so the location affordance answers the preset directory as text —
// the deterministic branch a golden can hold on every platform.
//
// Zero model calls: no replay fixture mounts, so a stray stream fails loud.
import { existsSync } from 'node:fs'
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import type { Browser, Page } from 'playwright'
import { chromium } from 'playwright'
import { afterAll, beforeAll, describe, expect, it, onTestFailed } from 'vitest'
import type { Locator } from 'playwright'
import {
  captureStableAria, compareOrRefreshGolden, launchWebScaffold, watchConsole,
  webSnapshotMode, type WebScaffold,
} from './scaffold.ts'
import { connectFreshWorkspace, newEnglishPage, saveFailureShot } from './support.ts'

const SNAPSHOT_DIR = fileURLToPath(new URL('./snapshots/agent-preset-authoring', import.meta.url))
const SECTION_EXPECTED = join(SNAPSHOT_DIR, 'section.expected.md')
const COPY_DIALOG_EXPECTED = join(SNAPSHOT_DIR, 'copy-dialog.expected.md')
const CREATED_EXPECTED = join(SNAPSHOT_DIR, 'created.expected.md')
const DAMAGED_EXPECTED = join(SNAPSHOT_DIR, 'damaged.expected.md')
/** The shipped roster, beside the composition that names it. */
const SHIPPED_PRESETS = fileURLToPath(new URL('../../cli/config/agent-presets', import.meta.url))
const OVERLAY = fileURLToPath(new URL('./agent-preset-authoring.overlay.yml', import.meta.url))
const MODE = webSnapshotMode()

describe('web e2e: agent-preset authoring is a host-side copy', () => {
  let scaffold: WebScaffold
  let browser: Browser
  let page: Page
  let tripwire: ReturnType<typeof watchConsole>
  let userRoot: string

  /** The settings dialog, opened on the Agent-presets section. */
  function settingsDialog(): Locator {
    return page.getByRole('dialog', { name: 'Settings' })
  }

  /** Tokenize the lane-owned preset root after general aria normalization. */
  function withPresetRoot(snapshot: string): string {
    const rootSuffix = `/${userRoot.split('/').pop()!}`
    return snapshot.split('\n').map((line) => {
      const rootStart = line.indexOf(rootSuffix)
      if (rootStart === -1) return line
      const pathStart = line.lastIndexOf(' ', rootStart) + 1
      return `${line.slice(0, pathStart)}{{presetRoot}}${line.slice(rootStart + rootSuffix.length)}`
    }).join('\n')
  }

  beforeAll(async () => {
    userRoot = await realpath(await mkdtemp(join(tmpdir(), 'cortex-web-e2e-presets-')))
    scaffold = await launchWebScaffold({
      extraOverlayPath: OVERLAY,
      agentPresets: {
        roots: [
          { path: SHIPPED_PRESETS, trust: 'system' },
          { path: userRoot, trust: 'user' },
        ],
        default: 'standard',
      },
    })
    browser = await chromium.launch()
    page = await newEnglishPage(browser)
    tripwire = watchConsole(page)
    await page.goto(scaffold.baseUrl, { waitUntil: 'load' })
    await page.waitForSelector('[class*="frame"]', { timeout: 30_000 })
  }, 120_000)

  afterAll(async () => {
    await browser?.close()
    await scaffold?.close()
  })

  it('offers the roster with copy as the only way to create', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-preset-authoring-section'))
    await page.getByRole('button', { name: 'Settings', exact: true }).click()
    const dialog = settingsDialog()
    await dialog.waitFor({ timeout: 10_000 })
    await dialog.getByRole('button', { name: 'Agent presets' }).click()
    await dialog.getByRole('heading', { name: 'Agent presets' }).waitFor({ timeout: 10_000 })
    await dialog.getByText('Standard mode').first().waitFor({ timeout: 10_000 })

    const snapshot = await captureStableAria(page, '[role="dialog"]', scaffold.workspaceCwd)

    await compareOrRefreshGolden(SECTION_EXPECTED, snapshot, MODE)
    // The intro carries the guidance a create button used to imply, and the
    // shipped rows offer view/copy but never delete or a location — their
    // install is overwritten by upgrades and is not the user's to manage.
    expect(snapshot).toContain('or let the agent draft one for you in Creator mode.')
    expect(snapshot).not.toContain('New preset')
    expect(snapshot).toContain('View: Standard mode')
    expect(snapshot).not.toContain('Delete: Standard mode')
    expect(snapshot).not.toContain('Open folder')
  }, 60_000)

  it('views a shipped composition read-only instead of editing it', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-preset-authoring-view'))
    const dialog = settingsDialog()
    await dialog.getByRole('button', { name: 'View: Standard mode' }).click()
    const viewer = page.getByRole('dialog', { name: 'View · Standard mode' })
    await viewer.waitFor({ timeout: 10_000 })

    // The real shipped composition, not a golden: the viewer shows whatever
    // the deployment ships, and this lane only asserts it is shown read-only.
    const shipped = await readFile(join(SHIPPED_PRESETS, 'standard', 'agent.cordis.yml'), 'utf8')
    expect(await viewer.locator('pre').textContent()).toBe(shipped)
    expect(await viewer.getByRole('textbox').count()).toBe(0)
    // The header X and the footer button share the Close name; the footer one
    // is last in the dialog.
    await viewer.getByRole('button', { name: 'Close' }).last().click()
    await viewer.waitFor({ state: 'detached', timeout: 10_000 })
  }, 60_000)

  it('copies Minimal mode whole under a new id and lands in its files', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-preset-authoring-copy'))
    const dialog = settingsDialog()
    await dialog.getByRole('button', { name: 'Duplicate: Minimal mode' }).click()
    const copyDialog = page.getByRole('dialog', { name: 'Duplicate preset · Copied from Minimal mode' })
    await copyDialog.waitFor({ timeout: 10_000 })

    const dialogSnapshot = await captureStableAria(
      page, '[role="dialog"][aria-label^="Duplicate preset"]', scaffold.workspaceCwd)
    await compareOrRefreshGolden(COPY_DIALOG_EXPECTED, dialogSnapshot, MODE)
    // Two fields and nothing else: the id is the directory name the host
    // needs up front; description and composition live in the files.
    expect(dialogSnapshot).toContain('Identifier')
    expect(dialogSnapshot).not.toContain('Description')

    await copyDialog.getByPlaceholder('my-agent').fill('my-agent')
    await copyDialog.getByPlaceholder('Shown in the picker; defaults to the identifier').fill('My mode')
    await copyDialog.getByRole('button', { name: 'Create' }).click()
    await copyDialog.waitFor({ state: 'detached', timeout: 10_000 })

    // The new row lands in the custom group, and — with no desktop opener —
    // its directory is revealed as text right away: landing in the files is
    // the completion of a copy, not a follow-up.
    await dialog.getByText('My mode').first().waitFor({ timeout: 10_000 })
    await dialog.getByText('Preset files:').waitFor({ timeout: 10_000 })
    // The copy dialog is detached, so the settings dialog is the only one
    // left (it names itself via aria-labelledby, which a CSS attribute
    // selector cannot address).
    const snapshot = withPresetRoot(
      await captureStableAria(page, '[role="dialog"]', scaffold.workspaceCwd))
    await compareOrRefreshGolden(CREATED_EXPECTED, snapshot, MODE)
    expect(snapshot).toContain('{{presetRoot}}/my-agent')

    // The host copied the whole directory and rewrote only the display
    // metadata: the composition is byte-identical to the shipped source, the
    // description rides along for the user to edit in place, and neither the
    // source's name nor its roster order survives into the copy.
    const composition = await readFile(join(userRoot, 'my-agent', 'agent.cordis.yml'), 'utf8')
    expect(composition).toBe(await readFile(join(SHIPPED_PRESETS, 'minimal', 'agent.cordis.yml'), 'utf8'))
    const metadata = await readFile(join(userRoot, 'my-agent', 'preset.yml'), 'utf8')
    expect(metadata).toContain('name: My mode')
    expect(metadata).toContain('description: A two-tool coding agent offering only persistent bash and str_replace_editor.')
    expect(metadata).not.toContain('order:')
  }, 60_000)

  it('deletes the copy after confirmation and reclaims the roster', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-preset-authoring-delete'))
    const dialog = settingsDialog()
    await dialog.getByRole('button', { name: 'Delete: My mode' }).click()
    const confirm = page.getByRole('dialog', { name: 'Delete this preset?' })
    await confirm.waitFor({ timeout: 10_000 })
    await confirm.getByRole('button', { name: 'Delete', exact: true }).click()
    await confirm.waitFor({ state: 'detached', timeout: 10_000 })

    await expect.poll(async () => dialog.getByText('My mode').count(), { timeout: 10_000 }).toBe(0)
    expect(existsSync(join(userRoot, 'my-agent'))).toBe(false)
    // The custom group outlives its only member: the heading stays with the
    // creator entry so the place to author a preset never disappears.
    expect(await dialog.getByRole('heading', { name: 'Custom' }).count()).toBe(1)
    expect(await dialog.getByRole('button', { name: 'Draft a custom preset with Creator mode' }).count()).toBe(1)
    expect(await dialog.getByText('Standard mode').count()).toBeGreaterThan(0)
  }, 60_000)

  it('marks damaged presets broken and clears a ghost through delete', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-preset-authoring-damaged'))
    // The two hand-edit damage shapes: a composition that no longer parses,
    // and a directory whose composition file was deleted outright.
    await mkdir(join(userRoot, 'broken-yaml'), { recursive: true })
    await writeFile(join(userRoot, 'broken-yaml', 'agent.cordis.yml'), '- id: x\n  name: [unclosed\n')
    await mkdir(join(userRoot, 'ghost'), { recursive: true })
    await writeFile(join(userRoot, 'ghost', 'preset.yml'), 'name: Ghost preset\ndescription: The composition was deleted by hand.\n')

    // The section reads the roster when it mounts; hop away and back.
    const dialog = settingsDialog()
    await dialog.getByRole('button', { name: 'General' }).click()
    await dialog.getByRole('button', { name: 'Agent presets' }).click()
    await dialog.getByText('Failed to load').first().waitFor({ timeout: 10_000 })

    const snapshot = withPresetRoot(
      await captureStableAria(page, '[role="dialog"]', scaffold.workspaceCwd))
    await compareOrRefreshGolden(DAMAGED_EXPECTED, snapshot, MODE)
    // Both damage shapes surface as marked, unselectable, uncopyable cards
    // that still carry their metadata and the discovery-reported reason.
    expect(snapshot).toContain('Failed to load: broken-yaml')
    expect(snapshot).toContain('Failed to load: Ghost preset')
    expect(snapshot).toContain('not valid YAML')
    expect(snapshot).toContain('agent.cordis.yml is missing')
    expect(await dialog.getByRole('button', { name: 'Failed to load: broken-yaml' }).isDisabled()).toBe(true)
    expect(await dialog.getByRole('button', { name: 'Duplicate: Ghost preset' }).isDisabled()).toBe(true)
    // A broken card offers no "set default" affordance at all — the aria name
    // IS the broken marking, so the picking name must not exist.
    expect(await dialog.getByRole('button', { name: 'Set as default: broken-yaml' }).count()).toBe(0)

    // The ghost's way out is the card's own delete — and the id it blocked
    // is claimable again immediately afterwards.
    await dialog.getByRole('button', { name: 'Delete: Ghost preset' }).click()
    const confirm = page.getByRole('dialog', { name: 'Delete this preset?' })
    await confirm.waitFor({ timeout: 10_000 })
    await confirm.getByRole('button', { name: 'Delete', exact: true }).click()
    await confirm.waitFor({ state: 'detached', timeout: 10_000 })
    await expect.poll(async () => dialog.getByText('Ghost preset').count(), { timeout: 10_000 }).toBe(0)
    expect(existsSync(join(userRoot, 'ghost'))).toBe(false)

    await dialog.getByRole('button', { name: 'Duplicate: Minimal mode' }).click()
    const copyDialog = page.getByRole('dialog', { name: 'Duplicate preset · Copied from Minimal mode' })
    await copyDialog.waitFor({ timeout: 10_000 })
    await copyDialog.getByPlaceholder('my-agent').fill('ghost')
    await copyDialog.getByRole('button', { name: 'Create' }).click()
    await copyDialog.waitFor({ state: 'detached', timeout: 10_000 })
    await dialog.getByRole('button', { name: 'Set as default: ghost' }).waitFor({ timeout: 10_000 })

    // Leave the roster as the earlier tests shaped it.
    await dialog.getByRole('button', { name: 'Delete: ghost' }).click()
    const cleanup = page.getByRole('dialog', { name: 'Delete this preset?' })
    await cleanup.waitFor({ timeout: 10_000 })
    await cleanup.getByRole('button', { name: 'Delete', exact: true }).click()
    await cleanup.waitFor({ state: 'detached', timeout: 10_000 })
    await rm(join(userRoot, 'broken-yaml'), { recursive: true, force: true })
  }, 60_000)

  it('starts a creator-mode session from the section', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-preset-authoring-creator'))
    // Without a workspace the flow only stages (there is no session to land
    // in until one is connected); connect first so the gesture carries all
    // the way to a composed host session.
    await settingsDialog().getByRole('button', { name: 'Close' }).last().click()
    await connectFreshWorkspace(page, scaffold.workspaceCwd)
    await page.getByRole('button', { name: 'Settings', exact: true }).click()
    const dialog = settingsDialog()
    await dialog.waitFor({ timeout: 10_000 })
    await dialog.getByRole('button', { name: 'Agent presets' }).click()
    await dialog.getByRole('button', { name: 'Draft a custom preset with Creator mode' }).click()

    // Leaving settings is part of the gesture: the flow lands on the
    // new-session screen with the self-referential preset staged, and the
    // blank session the flow produces composes from it on the host.
    await dialog.waitFor({ state: 'detached', timeout: 10_000 })
    await page.getByRole('button', { name: 'Creator mode' }).waitFor({ timeout: 10_000 })
    await expect.poll(async () => {
      const response = await fetch(`${scaffold.baseUrl}/api/session.list`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          type: 'client-request', rpcId: 'creator-draft-stage', method: 'session.list', payload: {},
        }),
      })
      const body = await response.json() as {
        result: { value?: { sessions: unknown[] } }
      }
      return JSON.stringify(body.result.value?.sessions ?? body.result)
    }, { timeout: 15_000 }).toContain('"agentPreset":"cordis"')
  }, 60_000)

  it('drove every surface without a page error or a stream warning', () => {
    expect(tripwire.pageErrors).toEqual([])
    expect(tripwire.warnings).toEqual([])
  })
})
