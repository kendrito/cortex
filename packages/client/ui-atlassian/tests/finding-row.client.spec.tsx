// @vitest-environment jsdom
/** The review-tool cards: one finding row and the review-complete row. */
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { FindingRow, ReviewCompleteRow } from '../src/client/cards/FindingRow.tsx'
import { en } from '../src/client/locales.ts'
import { cardProps, running, settled } from './card-support.client.ts'
import { panelActions } from './support.client.ts'

afterEach(cleanup)

const FINDING = 'atlassian_review_finding'
const COMPLETE = 'atlassian_review_complete'
const RECORDED = JSON.stringify({ recorded: true, findingId: 'f-1', count: 1, message: 'Recorded finding 1' })
const REFUSED = JSON.stringify({ recorded: false, count: 0, message: 'No pull request review is running in this session.' })

const args = {
  file: 'src/auth/redirect.ts', line: 4, side: 'ADDED', severity: 'critical', category: 'correctness',
  title: 'Open redirect', comment: 'Validate the **target**.', evidence: '  return decodeURIComponent(state)', rationale: 'Any URL redirects.',
}

describe('FindingRow', () => {
  it('shows severity, category, file:line, and title on the collapsed row', () => {
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, args, RECORDED))} />)
    expect(screen.getByText(en['severity.critical'])).toBeTruthy()
    expect(screen.getByText(en['category.correctness'])).toBeTruthy()
    expect(screen.getByText('redirect.ts:4')).toBeTruthy()
    expect(screen.getByText(/Open redirect/)).toBeTruthy()
  })

  it('expands to the comment, evidence, rationale, and the panel verb', () => {
    const actions = panelActions()
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, args, RECORDED), { actions })} />)
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getByText('target').tagName).toBe('STRONG')
    expect(document.body.textContent).toContain('return decodeURIComponent(state)')
    expect(screen.getByText('Any URL redirects.')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: en['card.open'] }))
    expect(actions.setTab).toHaveBeenCalledWith('review')
    expect(actions.open).toHaveBeenCalled()
  })

  it('toggles from the keyboard and ignores other keys', () => {
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, args, RECORDED))} />)
    const target = screen.getByRole('button', { expanded: false })
    fireEvent.keyDown(target, { key: 'x' })
    expect(target.getAttribute('aria-expanded')).toBe('false')
    fireEvent.keyDown(target, { key: ' ' })
    expect(target.getAttribute('aria-expanded')).toBe('true')
    fireEvent.keyDown(target, { key: 'Enter' })
    expect(target.getAttribute('aria-expanded')).toBe('false')
  })

  it('renders running, refused, and failed calls', () => {
    render(<FindingRow {...cardProps(FINDING, running(FINDING, '{"severity":"maj'))} />)
    expect(screen.getAllByText(en['card.finding'])).toHaveLength(2)
    cleanup()
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, args, REFUSED))} />)
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getByText(/No pull request review is running/)).toBeTruthy()
    cleanup()
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, args, 'invalid arguments', { isError: true }))} />)
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getByText('invalid arguments')).toBeTruthy()
  })

  it('shows unknown severities verbatim, drops unknown categories, and tolerates missing fields', () => {
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, { severity: 'blocker', category: 'vibes', line: 'x' }, RECORDED))} />)
    expect(screen.getByText('blocker')).toBeTruthy()
    expect(screen.queryByText('vibes')).toBeNull()
    expect(screen.queryByText(/:x/)).toBeNull()
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.queryByText('Any URL redirects.')).toBeNull()
    cleanup()
    for (const severity of ['major', 'minor', 'nit'] as const) {
      render(<FindingRow {...cardProps(FINDING, settled(FINDING, { ...args, severity, category: 'security' }, RECORDED))} />)
      expect(screen.getByText(en[`severity.${severity}`])).toBeTruthy()
      cleanup()
    }
    render(<FindingRow {...cardProps(FINDING, settled(FINDING, { file: 'a.ts', title: 'T' }, RECORDED))} />)
    expect(screen.getByText('a.ts')).toBeTruthy()
  })
})

describe('ReviewCompleteRow', () => {
  it('shows each verdict chip and the summary, expanding to the full text and the panel verb', () => {
    const actions = panelActions()
    for (const verdict of ['approve', 'request-changes', 'comment'] as const) {
      render(<ReviewCompleteRow {...cardProps(COMPLETE, settled(COMPLETE, { verdict, summary: 'Looks  fine.' }, '{"completed":true}'), { actions })} />)
      expect(screen.getByText(en[`review.verdict.${verdict}`])).toBeTruthy()
      cleanup()
    }
    render(<ReviewCompleteRow {...cardProps(COMPLETE, settled(COMPLETE, { verdict: 'approve', summary: 'Looks  fine.' }, '{"completed":true}'), { actions })} />)
    expect(screen.getByText('Looks fine.')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getAllByText(/Looks/)).toHaveLength(2)
    fireEvent.click(screen.getByRole('button', { name: en['card.open'] }))
    expect(actions.setTab).toHaveBeenCalledWith('review')
    expect(actions.open).toHaveBeenCalled()
  })

  it('handles unknown verdicts, missing summaries, running, and failed calls, and keyboard toggling', () => {
    render(<ReviewCompleteRow {...cardProps(COMPLETE, settled(COMPLETE, { verdict: 'meh' }, '{}'))} />)
    expect(screen.queryByText('meh')).toBeNull()
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getByRole('button', { name: en['card.open'] })).toBeTruthy()
    cleanup()
    render(<ReviewCompleteRow {...cardProps(COMPLETE, running(COMPLETE, '{'))} />)
    expect(screen.getByText(en['card.reviewComplete'])).toBeTruthy()
    cleanup()
    render(<ReviewCompleteRow {...cardProps(COMPLETE, settled(COMPLETE, {}, 'boom', { isError: true }))} />)
    const target = screen.getByRole('button', { expanded: false })
    fireEvent.keyDown(target, { key: 'x' })
    fireEvent.keyDown(target, { key: 'Enter' })
    expect(screen.getByText('boom')).toBeTruthy()
    fireEvent.keyDown(target, { key: ' ' })
    expect(target.getAttribute('aria-expanded')).toBe('false')
  })
})
