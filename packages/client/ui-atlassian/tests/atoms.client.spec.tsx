// @vitest-environment jsdom
/** Shared atoms: chips, avatars, glyphs, meta rows, section titles, the inline composer, empty state. */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { Avatar, Chip, Empty, InlineComposer, Meta, Person, SectionTitle, ServiceGlyph } from '../src/client/atoms.tsx'

afterEach(cleanup)

describe('Chip', () => {
  it('renders the tone, an optional dot, monospace, and a title', () => {
    const { container } = render(<Chip tone="error" dot mono title="why">Critical</Chip>)
    const chip = screen.getByText('Critical')
    expect(chip.getAttribute('data-tone')).toBe('error')
    expect(chip.getAttribute('title')).toBe('why')
    expect(container.querySelectorAll('span[aria-hidden]')).toHaveLength(1)
    cleanup()
    render(<Chip>Plain</Chip>)
    expect(screen.getByText('Plain').getAttribute('data-tone')).toBe('neutral')
    expect(document.querySelectorAll('span[aria-hidden]')).toHaveLength(0)
  })
})

describe('Avatar', () => {
  it('draws initials, a picture when the person has one, and a placeholder when absent', () => {
    render(<Avatar person={{ name: 'Jordan Alvarez' }} />)
    expect(screen.getByTitle('Jordan Alvarez').textContent).toBe('JA')
    cleanup()
    render(<Avatar person={{ name: 'Mei Chen', avatar: 'http://x/avatar.png' }} size={40} />)
    expect(screen.getByRole('presentation').getAttribute('src')).toBe('http://x/avatar.png')
    cleanup()
    render(<Avatar person={undefined} />)
    expect(screen.getByTitle('').textContent).toBe('·')
    expect(screen.getByTitle('').hasAttribute('data-empty')).toBe(true)
  })
})

describe('ServiceGlyph', () => {
  it('renders one letter per service', () => {
    const { container } = render(
      <>
        <ServiceGlyph service="jira" />
        <ServiceGlyph service="confluence" />
        <ServiceGlyph service="bitbucket" size={12} />
        <ServiceGlyph service="review" />
      </>,
    )
    expect([...container.querySelectorAll('span')].map(span => span.textContent)).toEqual(['J', 'C', 'B', 'R'])
  })
})

describe('Meta', () => {
  it('renders a label/value pair and nothing for empty values', () => {
    render(<Meta label="Sprint">Sprint 42</Meta>)
    expect(screen.getByText('Sprint')).toBeTruthy()
    expect(screen.getByText('Sprint 42')).toBeTruthy()
    for (const empty of [undefined, null, false, ''] as const) {
      const { container } = render(<Meta label="Empty">{empty}</Meta>)
      expect(container.textContent).toBe('')
    }
  })
})

describe('SectionTitle', () => {
  it('shows an optional count and accessory', () => {
    render(<SectionTitle title="Comments" count={3} accessory={<button type="button">More</button>} />)
    expect(screen.getByText('Comments')).toBeTruthy()
    expect(screen.getByText('3')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'More' })).toBeTruthy()
    cleanup()
    render(<SectionTitle title="Bare" />)
    expect(screen.queryByText('3')).toBeNull()
  })
})

describe('Person', () => {
  it('names the person or the fallback', () => {
    render(<Person person={{ name: 'Avery Quinn' }} fallback="Unassigned" />)
    expect(screen.getByText('Avery Quinn')).toBeTruthy()
    cleanup()
    render(<Person person={undefined} fallback="Unassigned" />)
    expect(screen.getByText('Unassigned')).toBeTruthy()
  })
})

describe('InlineComposer', () => {
  it('opens on the trigger, sends prefix + text with Cmd+Enter, and closes after sending', async () => {
    const onSend = vi.fn(() => Promise.resolve())
    render(<InlineComposer label="Comment…" prefix="Add a comment: " onSend={onSend} sendLabel="Send" />)
    fireEvent.click(screen.getByRole('button', { name: 'Comment…' }))
    const box = screen.getByRole('textbox')
    fireEvent.change(box, { target: { value: '  looks good  ' } })
    await act(async () => { fireEvent.keyDown(box, { key: 'Enter', metaKey: true }) })
    expect(onSend).toHaveBeenCalledWith('Add a comment: looks good')
    expect(screen.queryByRole('textbox')).toBeNull()
    expect(screen.getByRole('button', { name: 'Comment…' })).toBeTruthy()
  })

  it('sends through the button, ignores empty text and plain Enter, and cancels on Escape or Cancel', async () => {
    const onSend = vi.fn(() => Promise.resolve())
    render(<InlineComposer label="Edit…" prefix="Edit: " onSend={onSend} sendLabel="Send" placeholder="Say something" />)
    fireEvent.click(screen.getByRole('button', { name: 'Edit…' }))
    const box = screen.getByPlaceholderText('Say something')
    expect(screen.getByRole('button', { name: 'Send' })).toHaveProperty('disabled', true)
    fireEvent.keyDown(box, { key: 'Enter' })
    expect(onSend).not.toHaveBeenCalled()
    fireEvent.change(box, { target: { value: 'hello' } })
    fireEvent.keyDown(box, { key: 'Enter' })
    expect(onSend).not.toHaveBeenCalled()
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Send' })) })
    expect(onSend).toHaveBeenCalledWith('Edit: hello')

    fireEvent.click(screen.getByRole('button', { name: 'Edit…' }))
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Escape' })
    expect(screen.queryByRole('textbox')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Edit…' }))
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('textbox')).toBeNull()
  })

  it('stays busy while a send is in flight and can be disabled', async () => {
    let settle!: () => void
    const onSend = vi.fn(() => new Promise<void>((resolve) => { settle = resolve }))
    render(<InlineComposer label="Go" prefix="" onSend={onSend} sendLabel="Send" />)
    fireEvent.click(screen.getByRole('button', { name: 'Go' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'x' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    expect(screen.getByRole('button', { name: 'Send' })).toHaveProperty('disabled', true)
    // A second submit while busy is ignored.
    fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', ctrlKey: true })
    expect(onSend).toHaveBeenCalledTimes(1)
    await act(async () => { settle() })
    expect(screen.queryByRole('textbox')).toBeNull()
    cleanup()
    render(<InlineComposer label="Go" prefix="" onSend={onSend} sendLabel="Send" disabled />)
    expect(screen.getByRole('button', { name: 'Go' })).toHaveProperty('disabled', true)
  })
})

describe('Empty', () => {
  it('renders the title with optional body and icon', () => {
    render(<Empty title="Nothing here" body="Ask the agent." icon={<span>★</span>} />)
    expect(screen.getByText('Nothing here')).toBeTruthy()
    expect(screen.getByText('Ask the agent.')).toBeTruthy()
    expect(screen.getByText('★')).toBeTruthy()
    cleanup()
    render(<Empty title="Bare" />)
    expect(screen.queryByText('Ask the agent.')).toBeNull()
  })
})
