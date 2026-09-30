/** JSON-safe values exposed by the integrated Testy plugin. */
import type { JsonValue } from '@cortex/util-values'

export type { JsonValue } from '@cortex/util-values'

/** Current local engine availability and the Cortex model used for new GUI runs. */
export interface TestyPluginStatus {
  supported: boolean
  connected: boolean
  workspace: string
  model: { provider: string; model: string; reasoningEffort?: string }
  activeRuns: number
  revision: number
  lastError?: string
}

/** Raw MCP result retained for both rich UI rendering and chat tool projection. */
export interface TestyToolResult {
  content: JsonValue[]
  structuredContent?: JsonValue
  isError?: boolean
}
