// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { axisTicks, buildTrace, formatDuration, RunTrace } from '../src/client/RunTrace.tsx'
import type { RecordValue } from '../src/client/wire.ts'
import type { TestyToolResult } from '../src/types.ts'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

const at = (ms: number) => new Date(Date.UTC(2026, 8, 30, 8, 0, 0) + ms).toISOString()
const run: RecordValue = {
  runId: 'run-one', status: 'failed', running: false, startedAt: at(0), finishedAt: at(8200), durationMs: 8200,
  artifactDirectory: 'C:\\Testy\\runs\\20260930-080000-ai-1234abcd',
  steps: [
    { number: 1, title: 'Open customers', action: 'click', status: 'passed', startedAt: at(1000), durationMs: 400, screenshotAvailable: true },
    { number: 2, title: 'Type name', action: 'typeText', status: 'passed', startedAt: at(3000), durationMs: 600, screenshotAvailable: true },
    { number: 3, title: 'Check saved', action: 'assertText', status: 'failed', startedAt: at(6000), durationMs: 1000, screenshotAvailable: true },
    { number: 4, title: 'Close dialog', action: 'click', status: 'skipped', startedAt: at(0), durationMs: 0 },
  ],
}

describe('run trace model', () => {
  it('places steps and the time between them on one axis whose parts add up to the total', () => {
    const model = buildTrace(run)
    expect(model.total).toBe(8200)
    expect(model.aiGuided).toBe(true)
    expect(model.steps.map(step => [step.number, step.state, step.start, step.duration])).toEqual([
      [1, 'passed', 1000, 400], [2, 'passed', 3000, 600], [3, 'failed', 6000, 1000], [4, 'notRun', 0, 0],
    ])
    expect(model.gaps).toEqual([
      { start: 0, duration: 1000 }, { start: 1400, duration: 1600 }, { start: 3600, duration: 2400 }, { start: 7000, duration: 1200 },
    ])
    expect(model.legend).toEqual({ steps: '2.0 s', between: '6.2 s', total: '8.2 s' })
  })

  it('uses nice ticks in seconds, or milliseconds for runs under a second', () => {
    expect(axisTicks(8200).map(tick => tick.label)).toEqual(['0', '1', '2', '3', '4', '5', '6', '7', '8'])
    expect(axisTicks(640).map(tick => tick.label)).toEqual(['0', '100', '200', '300', '400', '500', '600'])
    expect(axisTicks(0)).toEqual([{ ms: 0, label: '0' }])
  })

  it('formats durations like native Studio', () => {
    expect([0, 0.4, 12, 400, 8200, 125_000].map(formatDuration)).toEqual(['0 ms', '<1 ms', '12 ms', '0.4 s', '8.2 s', '125 s'])
  })

  it('treats replayed runs as time between steps and falls back to cumulative time without timestamps', () => {
    const model = buildTrace({ status: 'passed', durationMs: 900, steps: [
      { number: 1, status: 'passed', durationMs: 300 }, { number: 2, status: 'passed', durationMs: 500 },
    ] })
    expect(model.aiGuided).toBe(false)
    expect(model.inMilliseconds).toBe(true)
    expect(model.steps.map(step => step.start)).toEqual([0, 300])
    expect(model.legend).toEqual({ steps: '800 ms', between: '100 ms', total: '900 ms' })
  })
})

describe('run trace view', () => {
  it('selects steps from the axis, the keyboard and the screenshot strip', async () => {
    const onSelect = vi.fn<(step: number) => void>()
    const image: TestyToolResult = { content: [{ type: 'image', mimeType: 'image/png', data: 'aW1hZ2U=' }] }
    const invoke = vi.fn<(name: string, args: RecordValue) => Promise<TestyToolResult>>(async () => image)
    render(<RunTrace run={run} runId="run-one" label="Run trace" selected={3} onSelect={onSelect} invoke={invoke} />)

    expect(screen.getByText('Assistant finding the next control')).toBeTruthy()
    expect(screen.getByText('6.2 s')).toBeTruthy()
    const track = screen.getByRole('group', { name: 'Run trace' })
    // Names stay "Step N" (what the web e2e and screen readers use); the tooltip carries the detail.
    const failed = within(track).getByRole('button', { name: 'Step 3' })
    expect(failed.getAttribute('title')).toBe('Step 3, Check saved, failed, 1.0 s at 6.0 s')
    expect(failed.getAttribute('aria-pressed')).toBe('true')
    expect(failed.tabIndex).toBe(0)
    expect(screen.queryAllByRole('button', { name: /Check saved/ })).toHaveLength(0)

    fireEvent.click(within(track).getByRole('button', { name: 'Step 1' }))
    expect(onSelect).toHaveBeenLastCalledWith(1)
    fireEvent.keyDown(failed, { key: 'ArrowLeft' })
    expect(onSelect).toHaveBeenLastCalledWith(2)
    fireEvent.click(screen.getByRole('button', { name: /Close dialog/ }))
    expect(onSelect).toHaveBeenLastCalledWith(4)

    await waitFor(() => { expect(invoke).toHaveBeenCalledTimes(3) })
    expect(invoke).toHaveBeenCalledWith('get_run_screenshot', { runId: 'run-one', stepNumber: 1, maxWidth: 160 })
    const strip = screen.getByRole('group', { name: 'Step screenshots' })
    await waitFor(() => { expect(strip.querySelectorAll('img')).toHaveLength(3) })
    fireEvent.click(within(strip).getByRole('button', { name: 'Screenshot of step 2' }))
    expect(onSelect).toHaveBeenLastCalledWith(2)
  })
})
