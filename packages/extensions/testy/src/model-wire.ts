/** Strict Chat Completions translation for the private Testy child. @module */
import { Ajv } from 'ajv'
import { z } from 'zod'
import type { Context } from '@cortex/cordis'
import type { ModelSelection } from '@cortex/agent'
import type { AttachmentAdmissionPart } from '@cortex/attachment'
import { createMessage, createUserMessage, createAssistantMessage, ToolCallId } from '@cortex/llm'
import type { BlockAssembler, ContentBlock, Message, ToolSchema } from '@cortex/llm'
import './model-events.ts'

const JsonObject = z.record(z.string(), z.unknown())
const ContentPart = z.discriminatedUnion('type', [
  z.object({ type: z.literal('text'), text: z.string() }).strict(),
  z.object({ type: z.literal('image_url'), image_url: z.object({ url: z.string(), detail: z.enum(['auto', 'low', 'high']).optional() }).strict() }).strict(),
])
const FunctionCall = z.object({ id: z.string().min(1), type: z.literal('function'), function: z.object({ name: z.string().min(1), arguments: z.string() }).strict() }).strict()
const WireMessage = z.discriminatedUnion('role', [
  z.object({ role: z.literal('system'), content: z.string() }).strict(),
  z.object({ role: z.literal('user'), content: z.union([z.string(), z.array(ContentPart)]) }).strict(),
  z.object({ role: z.literal('assistant'), content: z.string().nullable(), tool_calls: z.array(FunctionCall).optional() }).strict(),
  z.object({ role: z.literal('tool'), content: z.string(), tool_call_id: z.string().min(1) }).strict(),
])

/** Only the fields emitted by Testy's Compatible provider are accepted. */
export const TestyCompletionRequest = z.object({
  model: z.literal('cortex'),
  messages: z.array(WireMessage).min(1),
  tools: z.array(z.object({ type: z.literal('function'), function: z.object({
    name: z.string().regex(/^[A-Za-z0-9_-]{1,64}$/), description: z.string().optional(),
    strict: z.boolean().optional(), parameters: JsonObject,
  }).strict() }).strict()).optional(),
  response_format: z.object({ type: z.literal('json_schema'), json_schema: z.object({
    name: z.string().min(1), strict: z.literal(true), schema: JsonObject,
  }).strict() }).strict().optional(),
  parallel_tool_calls: z.literal(false).optional(),
  stream: z.literal(false).optional(),
  temperature: z.number().min(0).max(2).optional(),
  max_tokens: z.number().int().positive().optional(),
  max_completion_tokens: z.number().int().positive().optional(),
}).strict()

/** Parsed child request; routing always belongs to the registered host run. */
export type TestyCompletionRequest = z.infer<typeof TestyCompletionRequest>

/** An input/projection refusal safe to return through the local bridge. */
export class ModelBridgeError extends Error {
  constructor(readonly status: number, readonly code: string, message: string) {
    super(message)
    this.name = 'ModelBridgeError'
  }
}

/**
 * Admit child screenshots and construct identified messages in wire order.
 * @param ctx - attachment-owning host context.
 * @param request - strictly parsed child request.
 * @param model - pinned host selection, never supplied by the child.
 * @param signal - operation lifetime.
 * @returns exact model-facing messages and function schemas.
 */
export async function translateTestyRequest(
  ctx: Context, request: TestyCompletionRequest, model: ModelSelection, signal: AbortSignal,
): Promise<{ messages: Message[]; tools: ToolSchema[] }> {
  const messages: Message[] = []
  const tools = (request.tools ?? []).map(({ function: tool }) => ({
    name: tool.name, description: tool.description ?? '', parameters: tool.parameters,
  }))
  if (new Set(tools.map(tool => tool.name)).size !== tools.length) throw new ModelBridgeError(400, 'INVALID_TOOLS', 'Tool names must be unique.')
  for (const tool of tools) compileSchema(tool.parameters)
  if (tools.length > 0) messages.push(createMessage({ role: 'system', source: { kind: 'system-prompt' }, content: [{
    type: 'text', text: 'Return at most one function call per response. Only call a supplied function with arguments matching its JSON Schema.',
  }] }))
  if (request.response_format !== undefined) {
    if (tools.length > 0) throw new ModelBridgeError(400, 'INVALID_FORMAT', 'Structured planning cannot be combined with action tools.')
    const schema = request.response_format.json_schema.schema
    compileSchema(schema)
    messages.push(createMessage({ role: 'system', source: { kind: 'system-prompt' }, content: [{
      type: 'text', text: `Return only a JSON value matching this JSON Schema, without Markdown fences:\n${JSON.stringify(schema)}`,
    }] }))
  }
  for (const message of request.messages) {
    signal.throwIfAborted()
    switch (message.role) {
      case 'system':
        messages.push(createMessage({ role: 'system', source: { kind: 'system-prompt' }, content: [{ type: 'text', text: message.content }] }))
        break
      case 'user': {
        const parts: AttachmentAdmissionPart[] = typeof message.content === 'string'
          ? [{ type: 'text', text: message.content }]
          : message.content.map((part) => {
            if (part.type === 'text') return part
            const match = /^data:(image\/(?:png|jpeg|webp|gif));base64,([A-Za-z0-9+/]*={0,2})$/.exec(part.image_url.url)
            if (match === null || match[2] === undefined) throw new ModelBridgeError(400, 'INVALID_IMAGE', 'Only inline base64 raster screenshots are accepted.')
            const mediaType = match[1]
            if (mediaType !== 'image/png' && mediaType !== 'image/jpeg' && mediaType !== 'image/webp' && mediaType !== 'image/gif') {
              throw new ModelBridgeError(400, 'INVALID_IMAGE', 'Unsupported screenshot media type.')
            }
            return { type: 'image', mediaType, data: match[2], name: 'Testy screenshot' }
          })
        const content = await ctx.attachments.admitPromptContent(parts)
        signal.throwIfAborted()
        messages.push(createUserMessage({ source: { kind: 'testy' }, content }))
        break
      }
      case 'assistant': {
        const content: ContentBlock[] = message.content === null ? [] : [{ type: 'text', text: message.content }]
        for (const call of message.tool_calls ?? []) content.push({ type: 'tool-call', id: ToolCallId(call.id), name: call.function.name, arguments: call.function.arguments })
        messages.push(createAssistantMessage({ source: model, content }))
        break
      }
      case 'tool':
        messages.push(createMessage({ role: 'tool', source: { kind: 'tool', callId: ToolCallId(message.tool_call_id) }, toolCallId: ToolCallId(message.tool_call_id), content: [{ type: 'text', text: message.content }] }))
        break
    }
  }
  return { messages, tools }
}

/** Compile trusted-child schemas synchronously; no remote references or formats are fetched. */
function compileSchema(schema: Record<string, unknown>) {
  try {
    return new Ajv({ allErrors: true, strict: true }).compile(schema)
  } catch {
    throw new ModelBridgeError(400, 'INVALID_SCHEMA', 'Testy supplied an unsupported JSON Schema.')
  }
}

/**
 * Convert a settled Cortex stream without inventing a successful or truncated reply.
 * @param assembler - complete provider stream assembler.
 * @param request - accepted child request, including allowed tools and output schema.
 * @param id - public request identifier.
 * @returns nonstreaming OpenAI-compatible response.
 */
export function testyCompletionResponse(assembler: BlockAssembler, request: TestyCompletionRequest, id: string): Record<string, unknown> {
  if (assembler.finish.kind === 'error' || assembler.finish.kind === 'aborted') throw new ModelBridgeError(502, 'MODEL_FAILED', 'The selected Cortex model request failed.')
  if (assembler.finish.kind === 'max-tokens') throw new ModelBridgeError(502, 'MODEL_TRUNCATED', 'The selected Cortex model exhausted its output limit.')
  const blocks = assembler.blocks()
  if (blocks.some(block => block.type !== 'text' && block.type !== 'reasoning' && block.type !== 'tool-call')) {
    throw new ModelBridgeError(502, 'MODEL_OUTPUT', 'The selected Cortex model returned unsupported output.')
  }
  const content = blocks.filter(block => block.type === 'text').map(block => block.text).join('')
  const calls = blocks.filter(block => block.type === 'tool-call')
  if (calls.length > 1) throw new ModelBridgeError(502, 'PARALLEL_ACTIONS', 'The selected Cortex model requested multiple actions; no actions were dispatched.')
  for (const call of calls) {
    const schema = request.tools?.find(tool => tool.function.name === call.name)?.function.parameters
    if (schema === undefined) throw new ModelBridgeError(502, 'UNKNOWN_TOOL', 'The selected Cortex model requested an undeclared action.')
    let args: unknown
    try { args = JSON.parse(call.arguments) } catch { throw new ModelBridgeError(502, 'INVALID_ARGUMENTS', 'The selected Cortex model returned invalid action JSON.') }
    if (!compileSchema(schema)(args)) throw new ModelBridgeError(502, 'INVALID_ARGUMENTS', 'The selected Cortex model returned arguments that do not match the action schema.')
  }
  if (request.response_format !== undefined) {
    let value: unknown
    try { value = JSON.parse(content) } catch { throw new ModelBridgeError(502, 'INVALID_OUTPUT', 'The selected Cortex model did not return valid planning JSON.') }
    if (!compileSchema(request.response_format.json_schema.schema)(value)) {
      throw new ModelBridgeError(502, 'INVALID_OUTPUT', 'The selected Cortex model returned planning JSON that does not match its schema.')
    }
  }
  const usage = assembler.usage
  const promptTokens = usage === undefined ? 0 : usage.inputTokens + (usage.cacheReadTokens ?? 0) + (usage.cacheWriteTokens ?? 0)
  return {
    id, object: 'chat.completion', created: Math.floor(Date.now() / 1000), model: 'cortex',
    choices: [{ index: 0, finish_reason: calls.length > 0 ? 'tool_calls' : 'stop', message: {
      role: 'assistant', content: content.length > 0 ? content : null,
      ...calls.length === 0 ? {} : { tool_calls: calls.map(call => ({ id: call.id, type: 'function', function: { name: call.name, arguments: call.arguments } })) },
    } }],
    ...usage === undefined ? {} : { usage: {
      prompt_tokens: promptTokens, completion_tokens: usage.outputTokens,
      total_tokens: usage.totalTokens ?? promptTokens + usage.outputTokens,
    } },
  }
}
