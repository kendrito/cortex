/** Cold saved-history search through the shipped GUI's default local full-text index. */
import { chromium, type Browser, type Page } from 'playwright'
import { afterAll, beforeAll, describe, expect, it, onTestFailed, vi } from 'vitest'
import { createAssistantMessage, createSystemMessage, createUserMessage } from '@cortex/llm'
import { SESSION_FORMAT_VERSION, Session, SessionId } from '@cortex/session'
import type {} from '@cortex/session-projection-cache'
import type {} from '@cortex/session-query'
import type {} from '@cortex/session-title'
import {
  launchWebScaffold, readPersistedEvents, seedSession, watchConsole, type WebScaffold,
} from './scaffold.ts'
import { newEnglishPage, saveFailureShot } from './support.ts'

const SESSION_ID = SessionId('full-text-search-saved-history')
const TITLE = 'Research notes'
const QUERY = 'waterfall token'
const USER_TEXT = 'Keep the waterfall token in these research notes for later.'
const ASSISTANT_TEXT = 'The saved research entry is ready to revisit.'

/** A closed, explicitly titled conversation whose query appears only in message content. */
function savedConversation(): string {
  const session = Session.create(SESSION_ID)
  session.append('turn/start', { turn: 1 })
  session.append('step/start', { turn: 1, step: 1 })
  session.append('system/message', {
    turn: 1, step: 1, message: createSystemMessage('Help organize the research notes.'),
  }, { surfaceOp: 'append' })
  session.append('user/message', createUserMessage({
    content: [{ type: 'text', text: USER_TEXT }], source: { kind: 'user' },
  }), { surfaceOp: 'append' })
  session.append('session/title', { title: TITLE, messageSeqs: [], source: { kind: 'user' } })
  session.append('assistant/message', {
    turn: 1, step: 1, stream: [],
    message: createAssistantMessage({
      content: [{ type: 'text', text: ASSISTANT_TEXT }],
      source: { provider: 'deepseek-official', model: 'deepseek-v4-flash' },
    }),
  }, { surfaceOp: 'append' })
  session.append('step/end', { turn: 1, step: 1 })
  session.append('turn/end', { turn: 1, reason: { kind: 'completed' } })
  return [
    JSON.stringify({
      type: 'session', version: SESSION_FORMAT_VERSION, id: '{{sessionId}}',
      createdAt: 0, cwd: '{{cwd}}', isSeeded: false, delegationDepth: 0,
    }),
    ...session.snapshotEvents().map(event => JSON.stringify(event)),
    '',
  ].join('\n')
}

describe('web e2e: full-text search of saved conversations', () => {
  let scaffold: WebScaffold
  let browser: Browser
  let page: Page
  let tripwire: ReturnType<typeof watchConsole>
  let modelCalls = 0

  beforeAll(async () => {
    // No FTS overlay or replay fixture: the shipped first-search in-memory
    // index reads real JSONL, and the scaffold refuses any model stream.
    scaffold = await launchWebScaffold({})
    scaffold.ctx.on('llm/stream', async function* (_request, next) {
      modelCalls += 1
      yield* next()
    })
    await seedSession(scaffold, savedConversation(), SESSION_ID)
    // Detached JSONL seeding bypasses the normal turn-end title checkpoint.
    // Fold this closed persisted log into that cache without activating an Agent.
    {
      using observation = await scaffold.ctx.sessionQuery.observeSession(SESSION_ID)
      scaffold.ctx.sessionProjectionCache.coldSnapshot(
        observation.header, observation.inheritedEventCount, observation.events,
      )
      await vi.waitFor(() => {
        expect(scaffold.ctx.sessionProjectionCache.cachedSnapshot(observation.header)?.values.title).toBe(TITLE)
      })
    }
    browser = await chromium.launch()
    page = await newEnglishPage(browser)
    tripwire = watchConsole(page)
    await page.goto(scaffold.authenticatedUrl, { waitUntil: 'load' })
    await page.waitForSelector('[class*="frame"]', { timeout: 30_000 })
  }, 120_000)

  afterAll(async () => {
    await browser?.close()
    await scaffold?.close()
  })

  it('finds a cold conversation by body, shows its snippet, and opens the saved chat without a model call', async () => {
    onTestFailed(() => saveFailureShot(page, 'web-e2e-full-text-search'))
    expect(scaffold.ctx.agents.get(SESSION_ID)).toBeUndefined()
    const persisted = await readPersistedEvents(scaffold, SESSION_ID)
    expect(persisted.find(event => event.type === 'session/title')?.data).toMatchObject({ title: TITLE })

    await page.getByRole('button', { name: 'Search sessions', exact: true }).click()
    const input = page.getByPlaceholder('Search conversations', { exact: true })
    await input.fill(QUERY)
    const results = page.getByRole('tree', { name: 'Search results', exact: true })
    const result = results.getByRole('treeitem')
    await expect.poll(() => result.count(), { timeout: 30_000 }).toBe(1)
    await result.getByText(TITLE, { exact: true }).waitFor()
    await result.getByText(QUERY, { exact: false }).waitFor()
    expect(scaffold.ctx.agents.get(SESSION_ID)).toBeUndefined()

    await result.click()
    await expect.poll(() => input.inputValue(), { timeout: 10_000 }).toBe('')
    const chat = page.locator('[data-conversation-scroll]')
    await chat.getByText(USER_TEXT, { exact: true }).waitFor({ timeout: 15_000 })
    await chat.getByText(ASSISTANT_TEXT, { exact: true }).waitFor({ timeout: 15_000 })
    await expect.poll(() => page.evaluate(() => {
      const saved = localStorage.getItem('cortex.sessions.current')
      return saved === null ? undefined : (JSON.parse(saved) as { sessionId: string }).sessionId
    })).toBe(SESSION_ID)
    expect(await page.getByRole('tab', { name: 'Code', exact: true }).count()).toBe(0)
    expect(await page.getByRole('button', { name: /(?:unpin|pin) editor/i }).count()).toBe(0)
    expect(modelCalls).toBe(0)
    expect(tripwire.pageErrors).toEqual([])
  }, 60_000)
})
