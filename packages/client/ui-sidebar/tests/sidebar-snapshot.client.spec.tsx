// @vitest-environment jsdom
/**
 * Local DOM snapshots of the sidebar shell through the real assembly path:
 * SlotTestRuntime mounts the package apply on its own fiber, the auto frame
 * supplies the layout's owner share at the render site, and the snapshot
 * captures exactly the 'sidebar' slot's output (CSS-module class names
 * folded to their semantic locals by the runtime's serializer). The child
 * holes (sidebar.workspaces / sidebar.settings) have no registrant here, so
 * the snapshots pin the shell chrome itself.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, waitFor } from '@testing-library/react'
import { SlotTestRuntime, usePinnedBrowserLanguages } from '@cortex/client-test-runtime'
import { LocaleRuntime } from '@cortex/client-locale/client'
import { apply, inject } from '@cortex/client-ui-sidebar/client'

// The service reads its initial locale from the browser; these specs assert
// the shipped copy, so they state the browser they assume.
usePinnedBrowserLanguages('en-US')

afterEach(cleanup)

/**
 * Boot the package over the slot test runtime. The bench stays on the
 * service's default locale (en — the fallback chain's base), pinning what an
 * untouched client shows. The installed face backs the entry's standard `t`
 * seat.
 */
async function bench() {
  const runtime = await SlotTestRuntime.create()
  runtime.provide('layout', { toggleSidebar: vi.fn() })
  const locale = new LocaleRuntime(runtime.ctx)
  runtime.provide('locale', locale)
  runtime.slots.installLocale(locale)
  await runtime.declare({ 'sidebar': { kind: 'single', scope: 'root' } })
  await runtime.mount({ inject: [...inject], apply })
  return { runtime }
}

describe('sidebar shell snapshots', () => {
  it('renders the expanded column (wordmark, capsule, empty holes) in the default locale', async () => {
    const { runtime } = await bench()
    const slot = runtime.renderSlot('sidebar', { collapsed: false, width: 300 })
    // Wordmark + capsule both start a session in the expanded state.
    expect(slot.view.getAllByRole('button', { name: 'New session' })).toHaveLength(2)
    expect(slot.container).toMatchSnapshot()
    await runtime.dispose()
  })

  it('renders the collapsed rail after the crossfade settles, in place', async () => {
    const { runtime } = await bench()
    const slot = runtime.renderSlot('sidebar', { collapsed: false, width: 300 })
    const shell = slot.container.firstElementChild
    slot.update({ collapsed: true, width: 56 })
    // The wide content (wordmark shortcut) unmounts at the 150ms settle;
    // only the rail's capsule remains a New-session button.
    await waitFor(() => {
      expect(slot.view.getAllByRole('button', { name: 'New session' })).toHaveLength(1)
    })
    expect(slot.container).toMatchSnapshot()
    // Same tree position: the owner flip re-rendered the shell in place.
    expect(slot.container.firstElementChild).toBe(shell)
    await runtime.dispose()
  })
})
