/**
 * Card-spec fixtures: frozen tool-call blocks, the composed keyed-toolview
 * props, and a Bitbucket structured diff. Entity/projection builders come
 * from `./support.ts`.
 */
import { vi } from 'vitest'
import type { RunningToolCall, ToolResultNode } from '@cortex/client-runtime/client'
import type { AtlassianProjection } from '@cortex/atlassian/client'
import type { AtlassianCardProps } from '../src/client/contract.ts'
import type { PanelState } from '../src/client/store.ts'
import { NOW, kit, panelActions, panelState, projection, t } from './support.client.ts'

/**
 * Settled tool result node.
 * @param name - wire tool name.
 * @param args - call arguments (JSON-encoded into `argsRaw`).
 * @param text - result text.
 * @param overrides - fields to replace.
 * @returns the node.
 */
export function settled(name: string, args: unknown, text: string, overrides: Partial<ToolResultNode> = {}): ToolResultNode {
  return {
    kind: 'tool-result',
    seq: 12,
    time: NOW,
    callId: 'call-1',
    call: { name, argsRaw: JSON.stringify(args) },
    callTime: NOW - 500,
    content: [{ type: 'text', text }],
    isError: false,
    callView: null,
    resultView: null,
    subCalls: [],
    ...overrides,
  }
}

/**
 * Running (unsettled) call.
 * @param name - wire tool name.
 * @param argsRaw - raw (possibly truncated) argument text.
 * @returns the call.
 */
export function running(name: string, argsRaw: string): RunningToolCall {
  return { callId: 'call-1', name, argsRaw, turn: 1, step: 1, time: NOW, callView: null, subCalls: [] }
}

/** Card props overrides. */
export interface CardOverrides {
  projection?: AtlassianProjection | undefined
  state?: PanelState
  actions?: ReturnType<typeof panelActions>
  open?: AtlassianCardProps['open']
  inspect?: () => void
}

/**
 * Card props for one call.
 * @param toolName - wire tool name.
 * @param block - frozen call block.
 * @param overrides - projection value, store state, and face members.
 * @returns the composed props.
 */
export function cardProps(toolName: string, block: RunningToolCall | ToolResultNode, overrides: CardOverrides = {}): AtlassianCardProps {
  const state = overrides.state ?? panelState()
  const value = 'projection' in overrides ? overrides.projection : projection()
  return {
    ...kit,
    useProjection: (_key: string, selector?: (current: unknown) => unknown) => (selector === undefined ? value : selector(value)),
    useStore: (selector: (current: PanelState) => unknown) => selector(state),
    t,
    callId: block.callId,
    toolName,
    block,
    cwd: '/workspace',
    openFile: vi.fn(),
    inspect: overrides.inspect,
    actions: overrides.actions ?? panelActions(),
    open: overrides.open ?? vi.fn(async () => ({ ok: true as const, entity: { kind: 'issue' as const, key: 'PROJ-123' } })),
  } as unknown as AtlassianCardProps
}

/** Bitbucket structured diff JSON as the MCP diff tool returns it. */
export const DIFF_JSON = {
  fromHash: '0f9e8d', toHash: 'a1b2c3', truncated: false,
  diffs: [
    {
      source: { toString: 'src/auth/redirect.ts' }, destination: { toString: 'src/auth/redirect.ts' }, binary: false, truncated: false,
      hunks: [{ sourceLine: 1, sourceSpan: 3, destinationLine: 1, destinationSpan: 5, segments: [
        { type: 'CONTEXT', lines: [{ source: 1, destination: 1, line: 'export function readRedirect(req) {' }] },
        { type: 'REMOVED', lines: [{ source: 2, destination: 2, line: '  return req.query.state' }] },
        { type: 'ADDED', lines: [
          { source: 2, destination: 2, line: '  const state = req.query.state' },
          { source: 2, destination: 3, line: '  return decodeURIComponent(state)' },
        ] },
        { type: 'CONTEXT', lines: [{ source: 3, destination: 4, line: '}' }] },
      ] }],
    },
    { source: { toString: 'logo.png' }, destination: { toString: 'logo.png' }, binary: true, truncated: false, hunks: [] },
    {
      source: { toString: 'src/only-added.ts' }, destination: { toString: 'src/only-added.ts' }, binary: false, truncated: true,
      hunks: [{ sourceLine: 0, sourceSpan: 0, destinationLine: 1, destinationSpan: 1, segments: [
        { type: 'ADDED', lines: [{ source: 0, destination: 1, line: 'export const x = 1' }] },
      ] }],
    },
  ],
}
