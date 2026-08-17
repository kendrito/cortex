// @vitest-environment jsdom
// AttachmentRail behavior in the jsdom lane: item rendering and callbacks,
// arrow paging over stubbed scroll geometry (jsdom lays nothing out), the
// exclusive vertical-wheel pan, and the new-item end reveal.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render } from '@testing-library/react'
import { AttachmentRail } from '../src/AttachmentRail.tsx'
import type { AttachmentRailItem, AttachmentRailLabels } from '../src/AttachmentRail.tsx'

afterEach(cleanup)

// jsdom implements no ResizeObserver; the stub records instances so a test
// can drive the size-change recompute path.
const observers: { callback: ResizeObserverCallback; observed: Element[] }[] = []
beforeEach(() => {
  observers.length = 0
  vi.stubGlobal('ResizeObserver', class {
    observed: Element[] = []
    constructor(callback: ResizeObserverCallback) {
      observers.push({ callback, observed: this.observed })
    }

    observe(el: Element) { this.observed.push(el) }
    disconnect() { this.observed.length = 0 }
  })
})
afterEach(() => { vi.unstubAllGlobals() })

const labels: AttachmentRailLabels = {
  group: 'Pending images',
  open: 'View original',
  scrollLeft: 'Scroll images left',
  scrollRight: 'Scroll images right',
}

function item(id: string): AttachmentRailItem {
  return { id, previewUrl: `blob:${id}`, alt: `${id}.png`, removeLabel: `Remove image ${id}.png` }
}

/** Stub the rail's scroll geometry (jsdom reports 0 for every metric). */
function stubGeometry(rail: HTMLElement, { scrollWidth, clientWidth }: { scrollWidth: number; clientWidth: number }) {
  Object.defineProperty(rail, 'scrollWidth', { value: scrollWidth, configurable: true })
  Object.defineProperty(rail, 'clientWidth', { value: clientWidth, configurable: true })
  let scrollLeft = 0
  Object.defineProperty(rail, 'scrollLeft', {
    configurable: true,
    get: () => scrollLeft,
    set: (value: number) => { scrollLeft = value },
  })
  const scrollBy = vi.fn((options: { left: number }) => {
    scrollLeft = Math.max(0, Math.min(scrollWidth - clientWidth, scrollLeft + options.left))
  })
  rail.scrollBy = scrollBy as unknown as typeof rail.scrollBy
  return { scrollBy, setScrollLeft: (value: number) => { scrollLeft = value } }
}

describe('AttachmentRail', () => {
  it('renders thumbnails in order and routes open and remove clicks', () => {
    const onOpen = vi.fn()
    const onRemove = vi.fn()
    const items = [item('a'), item('b')]
    const view = render(<AttachmentRail items={items} labels={labels} onOpen={onOpen} onRemove={onRemove} />)
    const rail = view.getByRole('group', { name: 'Pending images' })
    expect([...rail.querySelectorAll('img')].map(img => img.getAttribute('alt'))).toEqual(['a.png', 'b.png'])
    fireEvent.click(view.getAllByTitle('View original')[0]!)
    expect(onOpen).toHaveBeenCalledWith(items[0])
    fireEvent.click(view.getByRole('button', { name: 'Remove image b.png' }))
    expect(onRemove).toHaveBeenCalledWith(items[1])
  })

  it('shows edge arrows from scroll geometry and pages a viewport at a time', () => {
    const view = render(
      <AttachmentRail items={[item('a'), item('b'), item('c')]} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
    )
    const rail = view.getByRole('group', { name: 'Pending images' })
    const { scrollBy } = stubGeometry(rail, { scrollWidth: 400, clientWidth: 200 })
    // No arrows until geometry is observed (mount saw jsdom's zero metrics).
    expect(view.queryByLabelText('Scroll images right')).toBeNull()
    fireEvent.scroll(rail)
    // Same-edges scroll takes the memoized-state path.
    fireEvent.scroll(rail)
    expect(view.queryByLabelText('Scroll images left')).toBeNull()
    const right = view.getByLabelText('Scroll images right')
    // clientWidth 200 - 64 < the 200 floor: pages by the floor.
    fireEvent.click(right)
    expect(scrollBy).toHaveBeenCalledWith({ left: 200, behavior: 'smooth' })
    fireEvent.scroll(rail)
    // Scrolled to the far edge: only the left arrow remains.
    expect(view.queryByLabelText('Scroll images right')).toBeNull()
    fireEvent.click(view.getByLabelText('Scroll images left'))
    expect(scrollBy).toHaveBeenCalledWith({ left: -200, behavior: 'smooth' })
    fireEvent.scroll(rail)
    expect(view.queryByLabelText('Scroll images left')).toBeNull()
    expect(view.getByLabelText('Scroll images right')).toBeTruthy()
  })

  it('shows both arrows mid-scroll and recomputes when the rail itself resizes', () => {
    const view = render(
      <AttachmentRail items={[item('a'), item('b'), item('c')]} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
    )
    const rail = view.getByRole('group', { name: 'Pending images' })
    const { setScrollLeft } = stubGeometry(rail, { scrollWidth: 400, clientWidth: 200 })
    setScrollLeft(100)
    // The component observes the rail element, not the window: a sidebar or
    // panel resize reaches it through the ResizeObserver callback.
    expect(observers.at(-1)?.observed).toContain(rail)
    act(() => { observers.at(-1)!.callback([], undefined as never) })
    expect(view.getByLabelText('Scroll images left')).toBeTruthy()
    expect(view.getByLabelText('Scroll images right')).toBeTruthy()
  })

  it('pans horizontally on a vertical wheel, consuming the event, with clamped normalized travel', () => {
    const view = render(
      <AttachmentRail items={[item('a'), item('b')]} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
    )
    const rail = view.getByRole('group', { name: 'Pending images' })
    const { scrollBy } = stubGeometry(rail, { scrollWidth: 400, clientWidth: 200 })
    // Converted ticks are consumed (preventDefault): fireEvent returns false.
    expect(fireEvent.wheel(rail, { deltaY: 30 })).toBe(false)
    expect(scrollBy).toHaveBeenCalledWith({ left: 30, behavior: 'auto' })
    fireEvent.wheel(rail, { deltaY: 500 })
    expect(scrollBy).toHaveBeenCalledWith({ left: 60, behavior: 'auto' })
    fireEvent.wheel(rail, { deltaY: -500 })
    expect(scrollBy).toHaveBeenCalledWith({ left: -60, behavior: 'auto' })
    // Firefox notch wheels report lines; a page-mode wheel reports viewports.
    fireEvent.wheel(rail, { deltaY: 2, deltaMode: WheelEvent.DOM_DELTA_LINE })
    expect(scrollBy).toHaveBeenCalledWith({ left: 32, behavior: 'auto' })
    fireEvent.wheel(rail, { deltaY: -1, deltaMode: WheelEvent.DOM_DELTA_PAGE })
    expect(scrollBy).toHaveBeenCalledWith({ left: -60, behavior: 'auto' })
    // A diagonal pan is consumed too — nothing vertical may escape the rail —
    // and keeps its horizontal intent.
    expect(fireEvent.wheel(rail, { deltaX: 12, deltaY: 30 })).toBe(false)
    expect(scrollBy).toHaveBeenCalledWith({ left: 12, behavior: 'auto' })
    // A purely horizontal pan and a zero-delta wheel keep native behavior.
    expect(fireEvent.wheel(rail, { deltaX: 12, deltaY: 0 })).toBe(true)
    fireEvent.wheel(rail, { deltaY: 0 })
    expect(scrollBy).toHaveBeenCalledTimes(6)
  })

  it('pages instantly under a reduced-motion preference, smoothly otherwise', () => {
    for (const [matches, behavior] of [[true, 'auto'], [false, 'smooth']] as const) {
      vi.stubGlobal('matchMedia', vi.fn(() => ({ matches }) as MediaQueryList))
      const view = render(
        <AttachmentRail items={[item('a'), item('b'), item('c')]} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
      )
      const rail = view.getByRole('group', { name: 'Pending images' })
      const { scrollBy } = stubGeometry(rail, { scrollWidth: 400, clientWidth: 200 })
      fireEvent.scroll(rail)
      fireEvent.click(view.getByLabelText('Scroll images right'))
      expect(scrollBy).toHaveBeenCalledWith({ left: 200, behavior })
      view.unmount()
    }
  })

  it('reveals the rail end when an item is added, not when one is removed', () => {
    const first = [item('a'), item('b')]
    const view = render(
      <AttachmentRail items={first} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
    )
    const rail = view.getByRole('group', { name: 'Pending images' })
    stubGeometry(rail, { scrollWidth: 400, clientWidth: 200 })
    view.rerender(
      <AttachmentRail items={[...first, item('c')]} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
    )
    expect(rail.scrollLeft).toBe(200)
    view.rerender(
      <AttachmentRail items={first} labels={labels} onOpen={vi.fn()} onRemove={vi.fn()} />,
    )
    // Removal keeps the position; only growth jumps to the end.
    expect(rail.scrollLeft).toBe(200)
  })
})
