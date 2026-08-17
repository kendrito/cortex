// @vitest-environment jsdom

import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render } from '@testing-library/react'
import { DropOverlay } from '../src/DropOverlay.tsx'

afterEach(cleanup)

describe('DropOverlay', () => {
  it('portals the invitation with its title and limits desc to the body', () => {
    const view = render(
      <DropOverlay disabled={false} labels={{ title: 'Drag images here to add them', desc: 'Up to 20 images, 5MB each' }} />,
    )
    const overlay = view.getByRole('status')
    expect(overlay.parentElement).toBe(document.body)
    expect(overlay.textContent).toContain('Drag images here to add them')
    expect(overlay.textContent).toContain('Up to 20 images, 5MB each')
  })

  it('omits the desc line when none is resolved', () => {
    const view = render(<DropOverlay disabled={false} labels={{ title: 'Drag images here to add them' }} />)
    expect(view.getByRole('status').textContent).toBe('Drag images here to add them')
  })

  it('drops the desc and switches the illustration while disabled', () => {
    const enabled = render(
      <DropOverlay disabled={false} labels={{ title: 'Drop here', desc: 'Limits' }} />,
    )
    const enabledSvg = enabled.getByRole('status').querySelector('svg')!.innerHTML
    enabled.unmount()
    const disabled = render(
      <DropOverlay disabled labels={{ title: 'Images cannot be added right now', desc: 'Limits' }} />,
    )
    const overlay = disabled.getByRole('status')
    expect(overlay.textContent).toBe('Images cannot be added right now')
    expect(overlay.querySelector('svg')!.innerHTML).not.toBe(enabledSvg)
  })
})
