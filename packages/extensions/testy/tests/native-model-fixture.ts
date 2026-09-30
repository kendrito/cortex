/** Scripted external model for real native Testy and browser integration checks. */
import { LlmAdapter, ToolCallId } from '@cortex/llm'
import type { GenerateOptions, LlmResolvedModelInfo, StreamChunk } from '@cortex/llm'
import { z } from 'zod'

/** Fixed explanation used only after the native engine supplies recorded evidence. */
export const explanation = 'The observed Customer Desk ready-state assertion passed; this explanation does not change the verdict.'

function textResponse(text: string): StreamChunk[] {
  return [{ type: 'text-delta', index: 0, text }, { type: 'finish', reason: { kind: 'stop' } }]
}

function requestText(request: GenerateOptions, role?: 'user' | 'system'): string {
  return request.messages.filter(message => role === undefined || message.role === role)
    .flatMap(message => message.content).flatMap(block => block.type === 'text' ? [block.text] : []).join('\n')
}

/** Fake only the external model; observe the actual native request before each scripted reply. */
export class NativeTestyAdapter extends LlmAdapter {
  requests: GenerateOptions[] = []
  phases: string[] = []
  workflow: 'explain' | 'ai-test' = 'explain'
  clarify = false
  targetPid: number | undefined
  private blocked = false
  private release = Promise.withResolvers<undefined>()

  /** Hold model output until cancellation or an explicit continuation. */
  hold(): void { this.blocked = true; this.release = Promise.withResolvers<undefined>() }
  /** Release held output for an operation whose native lifetime is being tested. */
  continue(): void { this.blocked = false; this.release.resolve(undefined) }
  override resolveModel(provider: string, model: string): Promise<LlmResolvedModelInfo> {
    return Promise.resolve({ provider, id: model, name: model, inputModalities: ['text', 'image'] })
  }
  async *stream(request: GenerateOptions): AsyncIterable<StreamChunk> {
    this.requests.push(request)
    if (this.blocked) {
      await new Promise<void>((resolve) => {
        const stopped = (): void => { resolve() }
        request.signal?.addEventListener('abort', stopped, { once: true })
        void this.release.promise.then(() => { request.signal?.removeEventListener('abort', stopped); resolve() })
        if (request.signal?.aborted) resolve()
      })
      request.signal?.throwIfAborted()
    }
    yield* this.workflow === 'explain' ? textResponse(explanation) : this.aiResponse(request)
  }

  private aiResponse(request: GenerateOptions): StreamChunk[] {
    const system = requestText(request, 'system')
    const user = requestText(request, 'user')
    if (system.includes('Select the Windows desktop application')) {
      this.phases.push('target')
      const inventory = z.object({ candidates: z.array(z.object({ id: z.string(), pid: z.number().nullable(), processName: z.string() })) })
        .parse(JSON.parse(user))
      const matches = inventory.candidates.filter(item => this.targetPid === undefined
        ? item.processName === 'Testy.TestLab' : item.pid === this.targetPid)
      if (matches.length !== 1) throw new Error('Native target inventory must identify exactly one observed Testy.TestLab sample.')
      return textResponse(JSON.stringify(this.clarify
        ? { candidateId: null, question: 'Which application should I test?', reason: 'The request did not identify an application.' }
        : { candidateId: matches[0]!.id, question: '', reason: 'The observed Customer Desk sample matches the requested application.' }))
    }
    if (system.includes("You are Testy's test designer")) {
      this.phases.push('draft')
      if (!user.includes('id:StatusMessage') || !user.includes('Ready for a new customer.')) {
        throw new Error('Native draft request must contain the actual Customer Desk ready-state observation.')
      }
      return textResponse(JSON.stringify({ name: 'AI ready-state check', intent: 'Verify Customer Desk ready state.', steps: [{
        title: 'Customer Desk is ready', action: 'assertText', selector: 'id:StatusMessage', value: 'Ready for a new customer.',
        timeoutMs: 5000, x: 0, y: 0,
      }] }))
    }
    if (!request.tools?.some(tool => tool.name === 'perform_saved_step')) throw new Error('Expected a native bound saved-step tool.')
    if (request.messages.some(message => message.role === 'tool')) {
      this.phases.push('complete')
      if (!requestText(request).includes('passed')) throw new Error('Expected native assertion evidence before completion.')
      return textResponse(explanation)
    }
    this.phases.push('assert')
    const workflow = z.object({ steps: z.array(z.object({ id: z.string() })).length(1) })
      .parse(JSON.parse(user.split('CANONICAL BOUND WORKFLOW:\n')[1]!.split('\nCurrent attached application observation')[0]!))
    return [
      { type: 'block-end', index: 0, block: { type: 'tool-call', id: ToolCallId('native-ready-assertion'), name: 'perform_saved_step', arguments: JSON.stringify({ stepId: workflow.steps[0]!.id }) } },
      { type: 'finish', reason: { kind: 'tool-calls' } },
    ]
  }
}
