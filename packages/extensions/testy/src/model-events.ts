/** Durable audit records for Testy's auxiliary calls through Cortex. @module */
import type { Branded } from '@cortex/brand'
import type { LlmCallConfig, Message, StreamChunk, ToolSchema } from '@cortex/llm'

/** One bridge dispatch, distinct from a Testy run or a Cortex turn. */
export type TestyModelRequestId = Branded<'TestyModelRequestId'>

/** Exact adapter-resolved inputs recorded before model dispatch. */
export interface TestyModelRequestData {
  requestId: TestyModelRequestId
  config: LlmCallConfig
  messages: Message[]
  tools: ToolSchema[]
}

/** Complete or interrupted provider output, including failures returned to Testy. */
export interface TestyModelResponseData {
  requestId: TestyModelRequestId
  status: 'completed' | 'failed' | 'cancelled'
  chunks: StreamChunk[]
  response?: Record<string, unknown>
  error?: { code: string; message: string }
}

declare module '@cortex/session/types' {
  interface SessionEventMap {
    /** Log-only request for a Testy operation; does not enter the conversation surface. */
    'testy/model-request': TestyModelRequestData
    /** Log-only settlement corresponding to exactly one Testy model request. */
    'testy/model-response': TestyModelResponseData
  }
}

declare module '@cortex/llm' {
  interface MessageSourceMap {
    /**
     * Testy-provided instructions, observations, and action evidence.
     * @persistenceAttribution
     */
    testy: { kind: 'testy' }
  }
}
