import { fileURLToPath } from 'node:url'
import { createServer } from 'node:http'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import {
  runScenario,
  type InputScript,
} from '@cortex/session-snapshot'

const AGENT = {
  binScript: fileURLToPath(new URL('../../../../src/bin.ts', import.meta.url)),
  configPath: fileURLToPath(new URL('../../../../../../snapshots/acp/escalation-approved/cordis.yml', import.meta.url)),
  profile: 'acp',
  tsconfigPath: fileURLToPath(new URL('../../../../../../tsconfig.json', import.meta.url)),
}
const IMAGE_OFFLOAD_CONFIG = fileURLToPath(new URL('./fixtures/image-offload.cordis.yml', import.meta.url))
const SNAPSHOTS_DIR = fileURLToPath(new URL('../../../../../../snapshots/acp/', import.meta.url))
const READ_IMAGE_WORKSPACE = fileURLToPath(new URL('../../../../../../snapshots/session/read-image/workspace/', import.meta.url))

it('keeps bounded ACP request images inline without contacting a Files API', async () => {
  const filesPath = '/v1/files'
  const modelPath = '/v1/messages'
  const requests: Record<string, unknown>[] = []
  const fileRequests: Array<{ method: string; path: string; bytes: number }> = []
  const server = createServer((request: IncomingMessage, response: ServerResponse) => {
    const chunks: Buffer[] = []
    request.on('data', (chunk: Buffer) => { chunks.push(chunk) })
    request.on('end', () => {
      void (async () => {
        const url = new URL(request.url ?? '/', 'http://localhost')
        const body = Buffer.concat(chunks)
        if (url.pathname === filesPath && request.method === 'POST') {
          const headers = new Headers()
          for (const [name, value] of Object.entries(request.headers)) {
            if (value !== undefined) headers.set(name, Array.isArray(value) ? value.join(', ') : value)
          }
          const form = await new Request('http://localhost/files', {
            method: 'POST', headers, body,
          }).formData()
          const file = form.get('file')
          if (!(file instanceof Blob)) throw new Error('snapshot Files upload omitted file')
          fileRequests.push({ method: 'POST', path: url.pathname, bytes: file.size })
          response.writeHead(500).end('Files API must not be used')
          return
        }
        if (url.pathname !== modelPath) {
          response.writeHead(404).end()
          return
        }
        requests.push(JSON.parse(body.toString('utf8')) as Record<string, unknown>)
        response.writeHead(200, { 'content-type': 'text/event-stream' })
        const toolCall = requests.length === 1
        const events = [
          { type: 'message_start', message: { id: 'offload-response', model: 'deepseek-v4-flash-vision-exp', usage: { input_tokens: 3, output_tokens: 0 } } },
          { type: 'content_block_start', index: 0, content_block: toolCall
            ? { type: 'tool_use', id: 'native-read-image', name: 'read_image', input: {} }
            : { type: 'text', text: '' } },
          { type: 'content_block_delta', index: 0, delta: toolCall
            ? { type: 'input_json_delta', partial_json: JSON.stringify({ file_path: 'red.png' }) }
            : { type: 'text_delta', text: 'DONE' } },
          { type: 'content_block_stop', index: 0 },
          { type: 'message_delta', delta: { stop_reason: toolCall ? 'tool_use' : 'end_turn' }, usage: { output_tokens: 1 } },
          { type: 'message_stop' },
        ]
        response.end(events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join(''))
        return
      })().catch((error: unknown) => {
        response.writeHead(500, { 'content-type': 'text/plain' }).end(String(error))
      })
    })
  })
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  if (address === null || typeof address === 'string') throw new Error('image-offload snapshot server has no port')

  const image = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC'
  const input: InputScript = {
    steps: [
      { op: 'initialize' },
      { op: 'newSession' },
      {
        op: 'promptContent',
        content: [
          { type: 'text', text: 'Compare the older image ' },
          { type: 'image', data: image, mimeType: 'image/png' },
          { type: 'text', text: ' with the newer image ' },
          { type: 'image', data: image, mimeType: 'image/png' },
          { type: 'text', text: ', then use read_image on red.png and reply with DONE.' },
        ],
      },
    ],
  }

  try {
    const result = await runScenario(input, {
      agent: AGENT,
      mode: 'record',
      configPath: IMAGE_OFFLOAD_CONFIG,
      fixtureFile: join(SNAPSHOTS_DIR, 'image-offload-request', 'session.jsonl'),
      workspaceDir: READ_IMAGE_WORKSPACE,
      env: {
        CORTEX_SNAPSHOT_API_KEY: 'snapshot-key',
        CORTEX_SNAPSHOT_BASE_URL: `http://127.0.0.1:${address.port}`,
      },
    })
    expect(result.stderr).toBe('')
    expect(requests).toHaveLength(2)
    expect(fileRequests).toEqual([])
    const attachmentDigest = 'b1ff9c8ea3a780bad09b346c423d2d0e46815926879b18e841d928376a946640'
    const attachmentId = `sha256:${attachmentDigest}`
    const accessText = (cwd: string): string => {
      const attachmentPath = join(
        cwd,
        '.cortex',
        'attachments',
        'v1',
        'objects',
        attachmentDigest.slice(0, 2),
        attachmentDigest,
      )
      return ` Normalized copy (read-only; may be resized or re-encoded): ${JSON.stringify(attachmentPath)} (1x1px, image/png).`
        + ' Source dimensions, format, and byte size may differ.'
        + ' Copy to a writable path ending in .png before editing.'
    }
    const normalizedAccess = accessText(result.cwd)
    const fileImage = { type: 'image', source: { type: 'base64', media_type: 'image/png', data: image } }
    const offloadedImage = `[image omitted to fit request image limits; ${attachmentId}.${normalizedAccess}]`
    const imageHandle = `Image ${attachmentId}; request preview 1x1px.${normalizedAccess}`
    const messages = requests[0]?.messages as { content?: unknown }[] | undefined
    const offloaded = messages?.find(message => JSON.stringify(message.content).includes('[image omitted'))
    expect(offloaded?.content).toEqual([
      { type: 'text', text: 'Compare the older image ' },
      { type: 'text', text: offloadedImage },
      { type: 'text', text: ' with the newer image ' },
      { type: 'text', text: imageHandle },
      fileImage,
      { type: 'text', text: ', then use read_image on red.png and reply with DONE.' },
    ])

    const followup = requests[1]?.messages as Array<{ role: string; content: string | Array<Record<string, unknown>> }>
    const blocks = followup.flatMap(message => typeof message.content === 'string'
      ? [{ type: 'text', text: message.content }] : message.content)
    expect(blocks.filter(block => block.type === 'image')).toEqual([])
    expect(JSON.stringify(followup[0]?.content).match(/image omitted to fit request image limits/g)).toHaveLength(2)
    const toolUse = blocks.find(block => block.type === 'tool_use')
    expect(toolUse).toEqual({ type: 'tool_use', id: 'native-read-image', name: 'read_image', input: { file_path: 'red.png' } })
    const toolResult = blocks.find(block => block.type === 'tool_result')!
    expect(toolResult).toBeDefined()
    expect(toolResult.type).toBe('tool_result')
    expect(toolResult.tool_use_id).toBe('native-read-image')
    const content = toolResult.content as Array<Record<string, unknown>>
    expect(content[0]?.type).toBe('text')
    expect(content[0]?.text).toContain('image/png image, 1x1 px, 69 bytes')
    expect(content.slice(1)).toEqual([
      { type: 'text', text: `Image "red.png" (${attachmentId}); request preview 1x1px.${normalizedAccess}` },
      fileImage,
    ])

  } finally {
    await new Promise<void>(resolve => server.close(() => { resolve() }))
  }
}, 45_000)
