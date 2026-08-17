// @vitest-environment jsdom
// ContextMeter (composer trailing control): occupancy ring gating, the
// click-open breakdown panel, and its close gestures.

import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render } from '@testing-library/react'
import { makeTranslate } from '@cortex/client-test-runtime'
import { en as commonEn } from '@cortex/client-locale/src/locales/index.ts'
import { ContextMeter, type ContextMeterProps } from '../src/client/skeleton/ContextMeter.tsx'
import css from '../src/client/skeleton/ContextMeter.module.css'
import { en } from '../src/client/locales.ts'

afterEach(cleanup)

// Mirrors the real lookup chain (conversation namespace, then common).
const t = makeTranslate(en, commonEn) as ContextMeterProps['t']

const BREAKDOWN = { systemTokens: 120, toolsTokens: 21_500, messageTokens: 477_000 }

const segmentClass = css.segment
if (segmentClass === undefined) throw new Error('segment class missing from ContextMeter.module.css')

/** Stub the projection seat: a key-addressed table of whole values. */
function projections(values: Record<string, unknown>): ContextMeterProps['useProjection'] {
  return (key: string) => values[key]
}

function meter(values: Record<string, unknown>, translate: ContextMeterProps['t'] = t) {
  return render(<ContextMeter useProjection={projections(values)} t={translate} />)
}

describe('ContextMeter', () => {
  it('renders nothing until both pressure and capacity are known', () => {
    expect(meter({}).container.textContent).toBe('')
    expect(meter({ contextPressure: { pressureTokens: 32_000 } }).container.textContent).toBe('')
    expect(meter({ contextPressure: { contextWindow: 128_000 } }).container.textContent).toBe('')
  })

  it('shows the occupancy ring and opens the breakdown panel on click', () => {
    const view = meter({
      contextPressure: { pressureTokens: 32_000, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    })
    const trigger = view.getByRole('button', { name: '25% of context used' })
    expect(view.container.querySelector('[role="dialog"]')).toBeNull()
    fireEvent.click(trigger)
    const panel = view.container.querySelector('[role="dialog"]')!
    expect(panel.textContent).toContain('~32K / 128K')
    expect(panel.textContent).toContain('25%')
    expect(panel.textContent).toContain('of context used')
    expect(panel.textContent).toContain('System prompt~120')
    expect(panel.textContent).toContain('Tools~21.5K')
    expect(panel.textContent).toContain('Messages~477K')
    // The occupancy bar splits into one colored segment per composition row.
    expect(panel.getElementsByClassName(segmentClass)).toHaveLength(3)
    // Clicking the trigger again toggles the panel shut.
    fireEvent.click(trigger)
    expect(view.container.querySelector('[role="dialog"]')).toBeNull()
  })

  it('lets each locale own the headline word order around the reading', () => {
    const values = {
      contextPressure: { pressureTokens: 32_000, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    }
    // The reading leads the label in the shipped dictionary; a locale whose
    // sentence puts it last renders the same header in its own order. Both
    // read as one sentence rather than a concatenated fragment.
    const enView = meter(values)
    fireEvent.click(enView.getByRole('button', { name: '25% of context used' }))
    expect(enView.container.querySelector('[role="dialog"]')!.textContent)
      .toMatch(/^25%of context used/)
    const tTrailing = makeTranslate({ ...en, 'context.aria': 'Context used: {percent}' }, commonEn) as ContextMeterProps['t']
    const trailingView = meter(values, tTrailing)
    fireEvent.click(trailingView.getByRole('button', { name: 'Context used: 25%' }))
    expect(trailingView.container.querySelector('[role="dialog"]')!.textContent)
      .toMatch(/^Context used:25%/)
  })

  it('draws no bar segment at zero occupancy', () => {
    const view = meter({
      contextPressure: { pressureTokens: 0, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    })
    fireEvent.click(view.getByRole('button', { name: '0% of context used' }))
    const panel = view.container.querySelector('[role="dialog"]')!
    // `.segment` carries a min-width, so a zero-width part would still paint a
    // filled sliver over an empty context.
    expect(panel.getElementsByClassName(segmentClass)).toHaveLength(0)
    expect(panel.textContent).toContain('~0 / 128K')
  })

  it('reads the ring from the projected figure so a compaction shows at once', () => {
    // Same provider sample, a surface a compaction just shrank: the ring must
    // follow the projection rather than the sample it is anchored to.
    const view = meter({
      contextPressure: { pressureTokens: 32_000, projectedTokens: 3_000, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    })
    const trigger = view.getByRole('button', { name: '2% of context used' })
    fireEvent.click(trigger)
    expect(view.container.querySelector('[role="dialog"]')!.textContent).toContain('~3K / 128K')
  })

  it('omits the composition rows while the contextBreakdown projection is absent', () => {
    const view = meter({ contextPressure: { pressureTokens: 32_000, contextWindow: 128_000 } })
    fireEvent.click(view.getByRole('button', { name: '25% of context used' }))
    const panel = view.container.querySelector('[role="dialog"]')!
    expect(panel.textContent).toContain('~32K / 128K')
    expect(panel.textContent).not.toContain('System prompt')
    expect(panel.textContent).not.toContain('Messages')
    // Without composition shares, the bar falls back to one plain segment.
    expect(panel.getElementsByClassName(segmentClass)).toHaveLength(1)
  })

  it('closes when capacity disappears and stays closed when it returns', () => {
    let values: Record<string, unknown> = {
      contextPressure: { pressureTokens: 32_000, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    }
    const view = render(<ContextMeter useProjection={(key: string) => values[key]} t={t} />)
    fireEvent.click(view.getByRole('button', { name: '25% of context used' }))
    expect(view.container.querySelector('[role="dialog"]')).not.toBeNull()

    values = { contextPressure: { pressureTokens: 32_000 }, contextBreakdown: BREAKDOWN }
    view.rerender(<ContextMeter useProjection={(key: string) => values[key]} t={t} />)
    expect(view.container.textContent).toBe('')

    values = {
      contextPressure: { pressureTokens: 32_000, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    }
    view.rerender(<ContextMeter useProjection={(key: string) => values[key]} t={t} />)
    expect(view.getByRole('button', { name: '25% of context used' }).getAttribute('aria-expanded')).toBe('false')
    expect(view.container.querySelector('[role="dialog"]')).toBeNull()
  })

  it('closes on outside pointerdown and Escape — but not inside clicks', () => {
    const view = meter({
      contextPressure: { pressureTokens: 32_000, contextWindow: 128_000 },
      contextBreakdown: BREAKDOWN,
    })
    const trigger = view.getByRole('button', { name: '25% of context used' })
    const openPanel = () => {
      fireEvent.click(trigger)
      return view.container.querySelector('[role="dialog"]')!
    }
    // A pointerdown inside the panel keeps it open; outside closes it.
    const again = openPanel()
    fireEvent.pointerDown(again)
    expect(view.container.querySelector('[role="dialog"]')).not.toBeNull()
    fireEvent.pointerDown(document.body)
    expect(view.container.querySelector('[role="dialog"]')).toBeNull()
    // Escape.
    openPanel()
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(view.container.querySelector('[role="dialog"]')).toBeNull()
  })
})
