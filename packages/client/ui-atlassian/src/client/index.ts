/**
 * Atlassian browser plugin, browser half: the session-header work panel
 * (drawer over the `atlassian` projection), the keyed tool cards for the
 * Jira / Confluence / Bitbucket MCP tools and the review tools, and the
 * Settings → Plugins → Atlassian tab. Every host verb rides the generated
 * `ctx.remote.atlassian` face; prompts queue through the session binding.
 */
import type { ConnectionHandle, SessionId } from '@cortex/api-remotes/client'
import type { ClientContext } from '@cortex/client-runtime/client'
import type {
  AtlassianSettings, ListPullRequestsResult, OpenRequest, OpenResult, PostFindingResult, PrRef,
} from '@cortex/atlassian/client'
// Type-only merges: ctx.remote (api-remotes), ctx.locale, the conversation/tool/settings SlotMaps,
// the settingsScope service, and the `atlassian` SessionProjectionMap key.
import type {} from '@cortex/api-remotes/client'
import type {} from '@cortex/client-locale/client'
import type {} from '@cortex/client-ui-conversation/client'
import type {} from '@cortex/client-ui-settings/client'
import type {} from '@cortex/client-ui-tool/client'
import type {} from '@cortex/atlassian/client'
import { AtlassianCard } from './cards/AtlassianCard.tsx'
import { FindingRow, ReviewCompleteRow } from './cards/FindingRow.tsx'
import type { ActionOutcome, CardFace, PanelFace, SettingsFace } from './contract.ts'
import { en, NS, type AtlassianKey } from './locales.ts'
import { AtlassianAction } from './panel/AtlassianAction.tsx'
import { AtlassianSettingsTab } from './settings/AtlassianSettingsTab.tsx'
import { createPanelStore } from './store.ts'

export type { AtlassianCardProps, PanelActionProps, SettingsTabProps } from './contract.ts'
export type { AtlassianKey } from './locales.ts'
export { createPanelStore } from './store.ts'

declare module '@cortex/client-ui-slots' {
  interface LocaleNamespaceMap {
    /** The Atlassian panel, cards, and settings copy. */
    atlassian: AtlassianKey
  }
}

/** Raw tool names (prefix stripped) that get the dedicated Atlassian card. */
export const CARD_TOOLS: readonly string[] = [
  // Jira
  'jira_get_issue', 'jira_search', 'jira_get_project_issues', 'jira_get_board_issues', 'jira_get_sprint_issues',
  'jira_create_issue', 'jira_batch_create_issues', 'jira_update_issue', 'jira_transition_issue', 'jira_add_comment',
  'jira_edit_comment', 'jira_assign_issue', 'jira_get_transitions', 'jira_link_to_epic', 'jira_create_issue_link',
  'jira_add_worklog', 'jira_delete_issue', 'jira_get_issue_development_info', 'jira_get_agile_boards',
  'jira_get_sprints_from_board', 'jira_get_all_projects', 'jira_get_worklog',
  // Confluence
  'confluence_get_page', 'confluence_search', 'confluence_create_page', 'confluence_update_page',
  'confluence_update_page_section', 'confluence_add_comment', 'confluence_get_page_children', 'confluence_get_comments',
  'confluence_get_page_diff', 'confluence_add_label', 'confluence_get_page_history',
  // Bitbucket
  'bitbucket_get_pull_request_details', 'bitbucket_get_pull_request_diff', 'bitbucket_get_pull_request_reviews',
  'bitbucket_create_pull_request', 'bitbucket_approve_pull_request', 'bitbucket_merge_pull_request',
  'bitbucket_decline_pull_request', 'bitbucket_add_pull_request_comment', 'bitbucket_add_pull_request_file_line_comment',
  'bitbucket_get_file_content', 'bitbucket_browse_directory', 'bitbucket_list_repositories',
  'bitbucket_list_repository_branches', 'bitbucket_create_branch', 'bitbucket_search_content', 'bitbucket_list_workspaces',
  'bitbucket_get_repository_details', 'bitbucket_get_user_profile',
]

/** Required services: slots, copy, sessions (prompt + projections), the generated Remote faces, connection, settings scope. */
export const inject = ['slots', 'locale', 'sessions', 'remote', 'remote.atlassian', 'remote.commands', 'connection', 'settingsScope']

/** Fold a Remote failure into the panel outcome vocabulary. */
function failed(code: string, message: string): { ok: false; code: string; message: string } {
  return { ok: false, code, message }
}

/**
 * Client plugin body: dictionaries, the header action + drawer, the keyed
 * tool cards, and the settings tab.
 * @param ctx - client root context.
 */
export function apply(ctx: ClientContext): void {
  ctx.effect(() => ctx.locale.register(NS, { en }), 'ui-atlassian: dictionaries')
  const t = ctx.locale.bind(NS)
  const sessions = ctx.sessions
  const connection = ctx.get('connection') as ConnectionHandle
  const panelStore = createPanelStore()

  const open = async (sessionId: SessionId, request: OpenRequest): Promise<OpenResult> => {
    const result = await ctx.remote.atlassian.open(sessionId, request)
    return result.ok ? result.value : failed(result.error.code, result.error.message)
  }

  const panelFace = (sessionId: SessionId): PanelFace => ({
    open: request => open(sessionId, request),
    pin: async (key) => {
      const result = await ctx.remote.atlassian.pin(sessionId, { key })
      return result.ok ? result.value : failed(result.error.code, result.error.message)
    },
    sendPrompt: async (text): Promise<ActionOutcome> => {
      const session = sessions.binding(sessionId)?.session
      if (session === undefined) return { ok: false, message: 'session is not open' }
      const result = await session.prompt([{ type: 'text', text }], 'queue')
      return result.ok ? { ok: true } : { ok: false, message: `${result.error.code}: ${result.error.message}` }
    },
    listPullRequests: async (request): Promise<ListPullRequestsResult> => {
      const result = await ctx.remote.atlassian.listPullRequests(request)
      return result.ok ? result.value : failed(result.error.code, result.error.message)
    },
    startReview: async (pr: PrRef, focus: string): Promise<ActionOutcome> => {
      const key = `${pr.project}/${pr.repo}#${String(pr.id)}`
      const line = focus.trim() === '' ? `/pr-review ${key}` : `/pr-review ${key} ${focus.trim()}`
      const result = await ctx.remote.commands.execute(sessionId, line)
      if (!result.ok) return { ok: false, message: `${result.error.message} (${result.error.code})` }
      if (result.value === undefined) return { ok: false, message: 'unknown command: /pr-review' }
      return result.value.result.kind === 'success' ? { ok: true } : { ok: false, message: result.value.result.text }
    },
    postFinding: async (request): Promise<PostFindingResult> => {
      const result = await ctx.remote.atlassian.postFinding(sessionId, request)
      return result.ok ? result.value : failed(result.error.code, result.error.message)
    },
    dismissFinding: async (request) => {
      const result = await ctx.remote.atlassian.dismissFinding(sessionId, request)
      return result.ok ? result.value : failed(result.error.code, result.error.message)
    },
    cancelReview: async (reviewId) => {
      const result = await ctx.remote.atlassian.cancelReview(sessionId, { reviewId })
      return result.ok ? result.value : failed(result.error.code, result.error.message)
    },
    diffContext: async (request) => {
      const result = await ctx.remote.atlassian.diffContext(request)
      return result.ok ? result.value : failed(result.error.code, result.error.message)
    },
    status: async () => {
      const result = await ctx.remote.atlassian.status()
      if (!result.ok) throw new Error(`atlassian.status failed: ${result.error.code}: ${result.error.message}`)
      return result.value
    },
  })

  ctx.slots.inject('conversation.session.header.actions', () => ctx.slots.register({
    name: 'conversation.session.header.actions',
    id: 'atlassian',
    order: 30,
    locale: NS,
    store: panelStore,
    inject: (sessionId: SessionId): PanelFace => panelFace(sessionId),
  }, AtlassianAction))

  const cardFace = (sessionId: SessionId): CardFace => ({ open: request => open(sessionId, request) })
  ctx.slots.inject('tool.call.toolview', function* () {
    for (const raw of CARD_TOOLS) {
      const server = raw.startsWith('bitbucket_') ? 'bitbucket' : 'atlassian'
      yield ctx.slots.register({
        name: 'tool.call.toolview',
        key: `mcp__${server}__${raw}`,
        locale: NS,
        store: panelStore,
        inject: cardFace,
      }, AtlassianCard)
    }
    yield ctx.slots.register(
      { name: 'tool.call.toolview', key: 'atlassian_review_finding', locale: NS, store: panelStore, inject: cardFace },
      FindingRow,
    )
    yield ctx.slots.register(
      { name: 'tool.call.toolview', key: 'atlassian_review_complete', locale: NS, store: panelStore, inject: cardFace },
      ReviewCompleteRow,
    )
  })

  const settingsScope = ctx.settingsScope.bind<AtlassianSettings>({ namespace: 'atlassian' })
  const settingsFace = (): SettingsFace => ({
    hooks: { settings: settingsScope },
    writable: connection.isLoopback,
    setField: async (field, value): Promise<ActionOutcome> => {
      try {
        await settingsScope.set(field, value)
        return { ok: true }
      } catch (error: unknown) {
        return { ok: false, message: error instanceof Error ? error.message : String(error) }
      }
    },
    describeTokens: async (refs) => {
      const response = await connection.api.credentials.describe({ refs })
      if (!response.result.ok) return {}
      const out: Record<string, { configured: boolean; writable: boolean }> = {}
      for (const [ref, view] of Object.entries(response.result.value.credentials)) {
        out[ref] = { configured: view.configured, writable: view.writable }
      }
      return out
    },
    setToken: async (ref, value): Promise<ActionOutcome> => {
      const response = await connection.api.credentials.set({ ref, value })
      return response.result.ok ? { ok: true } : { ok: false, message: response.result.error.message }
    },
    status: async () => {
      const result = await ctx.remote.atlassian.status()
      if (!result.ok) throw new Error(`atlassian.status failed: ${result.error.code}: ${result.error.message}`)
      return result.value
    },
    probe: async (service) => {
      const result = await ctx.remote.atlassian.probe({ service })
      return result.ok ? result.value : { service, ok: false, error: `${result.error.code}: ${result.error.message}` }
    },
    reconnect: async () => {
      const result = await ctx.remote.atlassian.reconnect()
      if (!result.ok) throw new Error(`atlassian.reconnect failed: ${result.error.code}: ${result.error.message}`)
      return result.value
    },
  })

  ctx.slots.inject('settings.plugins.tab', () => ctx.slots.register({
    name: 'settings.plugins.tab',
    id: 'atlassian',
    order: 30,
    label: () => t('settings.tab'),
    locale: NS,
    inject: settingsFace,
  }, AtlassianSettingsTab))
}
