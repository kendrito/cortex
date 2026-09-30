/** One browser contribution for the integrated Testy host plugin. */
import type { Context } from '@cortex/cordis'
import type {} from '@cortex/client-connection/client'
import type {} from '@cortex/client-locale/client'
import type {} from '@cortex/client-ui-renderer/client'
import type {} from '@cortex/client-ui-sidebar/client'
import type {} from '@cortex/client-ui-workspace/client'
import type { MainPanelId } from '@cortex/client-ui-layout/client'
import type { RemoteResult } from '@cortex/api-remotes/client'
import testyRemote from '@cortex/testy/remote'
import { TestyPage } from './TestyPage.tsx'
import { TestyIcon } from './TestyIcon.tsx'
import { createTestySource } from './source.ts'
import { createTestyViewStore } from './store.ts'
import { en, zh, type TestyKey } from './locales.ts'

declare module '@cortex/client-ui-slots' {
  interface LocaleNamespaceMap {
    /** Integrated Windows testing workbench copy. */
    testy: TestyKey
  }
}

/** Required client services; Testy mounts its own generated remote contribution. */
export const inject = ['slots', 'locale', 'remote']

async function unwrap<T>(pending: Promise<RemoteResult<T>>): Promise<T> {
  const result = await pending
  if (!result.ok) throw result.error
  return result.value
}

/**
 * Register the Testy page and its sidebar entry for this plugin's lifetime.
 * @param ctx - client services and the generated Testy remote mount.
 * @returns resolution after remote methods and both UI contributions are registered.
 */
export async function apply(ctx: Context): Promise<void> {
  const dispose = await ctx.remote.$mount(testyRemote)
  ctx.effect(() => dispose, 'testy: remote')
  ctx.effect(() => ctx.locale.register('testy', { en, zh }), 'testy: dictionaries')
  const t = ctx.locale.bind('testy')
  const view = createTestyViewStore()
  const id = 'testy' as MainPanelId
  // The contribution creates this namespace, so wait for it in a child scope
  // instead of requiring it before the parent can mount the contribution.
  ctx.inject(['remote', 'remote.testy', 'slots', 'uiWorkspace'], (scope) => {
    const source = createTestySource({
      status: () => unwrap(scope.remote.testy.status(scope.uiWorkspace.currentSessionId())),
      tools: () => unwrap(scope.remote.testy.tools()),
      call: (name, args) => unwrap(scope.remote.testy.call(name, args, scope.uiWorkspace.currentSessionId())),
      nativeStudio: () => unwrap(scope.remote.testy.nativeStudio(scope.uiWorkspace.currentSessionId())),
    })
    scope.slots.inject('main', () => scope.slots.register({ name: 'main', key: id, locale: 'testy', store: view, inject: () => source }, TestyPage))
    scope.slots.inject('sidebar.panellist', () => scope.slots.register({ name: 'sidebar.panellist', id, order: 12, locale: 'testy', label: () => t('panel') }, TestyIcon))
  })
}
