/**
 * Slot contracts of the Atlassian browser half: the injected faces the apply
 * closure hands to the panel action, the tool cards, and the settings tab, plus
 * the composed component prop types. Live data never rides these faces — the
 * panel reads the `atlassian` projection through `useProjection`; the faces
 * carry only callbacks (and, for settings, bare observables in `hooks`).
 */
import type {
  AckResult, AtlassianSettings, AtlassianStatus, DiffContextRequest, DiffContextResult, DismissFindingRequest,
  ListPullRequestsRequest, ListPullRequestsResult, OpenRequest, OpenResult, PostFindingRequest, PostFindingResult,
  PrRef, ProbeResult,
} from '@cortex/atlassian/client'
import type { SettingsScope } from '@cortex/client-runtime/client'
import type { InjectFace, PropsLocale, PropsRuntime, PropsStore } from '@cortex/client-ui-slots'
import type { ToolCallViewProps } from '@cortex/client-ui-tool/client'
import type {} from '@cortex/client-ui-conversation/client'
import type {} from '@cortex/client-ui-settings/client'
import type { createPanelStore } from './store.ts'
import type { NS } from './locales.ts'

/** Outcome of a callback that only reports success or a message. */
export type ActionOutcome = { ok: true } | { ok: false; message: string }

/** Verbs the panel and the cards reach the host through (all session-addressed). */
export interface PanelFace {
  /** Fetch one entity, record its snapshot, and focus the panel on it. */
  open: (request: OpenRequest) => Promise<OpenResult>
  /** Pin (`key`) or clear (`null`) the session's ticket. */
  pin: (key: string | null) => Promise<AckResult>
  /** Queue one prompt into this session (panel action buttons that go through the agent). */
  sendPrompt: (text: string) => Promise<ActionOutcome>
  /** List pull requests for the picker. */
  listPullRequests: (request: ListPullRequestsRequest) => Promise<ListPullRequestsResult>
  /** Start a review run of one pull request through the `/pr-review` command. */
  startReview: (pr: PrRef, focus: string) => Promise<ActionOutcome>
  /** Post one finding to Bitbucket. */
  postFinding: (request: PostFindingRequest) => Promise<PostFindingResult>
  /** Dismiss one finding. */
  dismissFinding: (request: DismissFindingRequest) => Promise<AckResult>
  /** Cancel the running review. */
  cancelReview: (reviewId: string) => Promise<AckResult>
  /** Diff lines around one finding. */
  diffContext: (request: DiffContextRequest) => Promise<DiffContextResult>
  /** Whole integration status. */
  status: () => Promise<AtlassianStatus>
}

/** Verbs a tool card may use: focus its entity in the panel. */
export interface CardFace {
  /** Fetch + focus one entity (also opens the panel). */
  open: (request: OpenRequest) => Promise<OpenResult>
}

/** Store handle type shared by the panel action and the cards. */
export type PanelStore = ReturnType<typeof createPanelStore>

/** Full props of the session-header Atlassian action (button + drawer). */
export type PanelActionProps =
  PropsRuntime<'conversation.session.header.actions'>
  & PropsStore<PanelStore>
  & InjectFace<PanelFace>
  & PropsLocale<typeof NS>

/** Full props of one keyed Atlassian tool card. */
export type AtlassianCardProps =
  ToolCallViewProps
  & PropsStore<PanelStore>
  & InjectFace<CardFace>
  & PropsLocale<typeof NS>

/** Credential facts the settings tab shows for one token reference. */
export interface TokenState {
  configured: boolean
  writable: boolean
}

/** Injected face of the settings tab. */
export interface SettingsFace {
  hooks: {
    /** The `atlassian` settings namespace scope (bare observable → `useSettings`). */
    settings: SettingsScope<AtlassianSettings>
  }
  /** Whether this browser may write settings and credentials (loopback connection). */
  writable: boolean
  /** Write one settings field. */
  setField: (field: keyof AtlassianSettings, value: string) => Promise<ActionOutcome>
  /** Describe the three token references. */
  describeTokens: (refs: string[]) => Promise<Record<string, TokenState>>
  /** Store one token value under a reference. */
  setToken: (ref: string, value: string) => Promise<ActionOutcome>
  /** Whole integration status. */
  status: () => Promise<AtlassianStatus>
  /** Probe one service. */
  probe: (service: 'jira' | 'confluence' | 'bitbucket') => Promise<ProbeResult>
  /** Retry failed mounts. */
  reconnect: () => Promise<AtlassianStatus>
}

/** Full props of the settings tab. */
export type SettingsTabProps =
  PropsRuntime<'settings.plugins.tab'>
  & InjectFace<SettingsFace>
  & PropsLocale<typeof NS>
