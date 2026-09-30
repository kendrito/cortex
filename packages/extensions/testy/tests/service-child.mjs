/** External stdio fixture: exercises the real MCP transport and private HTTP bridge. */
import { createInterface } from 'node:readline'
import { writeFile } from 'node:fs/promises'
import { spawn } from 'node:child_process'

const names = ['echo', 'ask', 'run_test', 'run_task', 'get_task', 'cancel_task', 'list_tasks', 'save_project', 'screenshot', 'disconnect', 'crash', 'invalid_message', 'late_response', 'delayed_response', 'emit_late_response', 'stall_tree']
const tools = names.map(name => ({
  name,
  description: `Fixture ${name}`,
  inputSchema: { type: 'object', properties: {}, additionalProperties: true },
  _meta: { 'cortex/usesModel': ['ask', 'run_test', 'run_task'].includes(name), 'cortex/humanOnly': name === 'save_project' },
}))
const tasks = new Map()
let stalled = false
let lateResponseCalls = 0
let lateResponseId
const lines = createInterface({ input: process.stdin })
const send = (id, result) => process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id, result })}\n`)
const result = data => ({ content: [{ type: 'text', text: JSON.stringify(data) }], structuredContent: data })
async function ask(context) {
  const response = await fetch(process.env.TESTY_CORTEX_BRIDGE_URL, {
    method: 'POST', headers: {
      'Content-Type': 'application/json', Authorization: `Bearer ${process.env.TESTY_CORTEX_BRIDGE_TOKEN}`,
      ...context === undefined ? {} : { 'X-Testy-Cortex-Context': context },
    },
    body: JSON.stringify({ model: 'cortex', messages: [{ role: 'user', content: 'fixture application observation' }] }),
  })
  return { status: response.status, data: await response.json() }
}
lines.on('line', async (line) => {
  const request = JSON.parse(line)
  if (request.id === undefined) return
  try {
    if (request.method === 'initialize') return send(request.id, {
      protocolVersion: request.params.protocolVersion, capabilities: { tools: {}, resources: {} }, serverInfo: { name: 'Testy fixture', version: '1' },
    })
    if (request.method === 'tools/list') return send(request.id, { tools })
    if (request.method === 'resources/read') return send(request.id, { contents: [{ uri: request.params.uri, text: 'fixture report', mimeType: 'text/plain' }] })
    if (request.method !== 'tools/call') return send(request.id, {})
    const { name, arguments: args = {}, _meta: meta } = request.params
    const context = meta?.['cortex/context']
    if (name === 'disconnect') { process.exit(0) }
    if (name === 'crash') {
      process.stderr.write(`fixture disk access denied; token=${process.env.TESTY_CORTEX_BRIDGE_TOKEN}\n`, () => process.exit(42))
      return
    }
    if (name === 'invalid_message') return process.stdout.write('{"invalid":"protocol frame"}\n')
    if (name === 'late_response' || name === 'delayed_response') {
      lateResponseCalls += 1
      lateResponseId = request.id
      if (name === 'late_response') send(request.id, result({ completed: true, lateResponseCalls }))
      return
    }
    if (name === 'emit_late_response') {
      send(lateResponseId, result({ completed: true, token: process.env.TESTY_CORTEX_BRIDGE_TOKEN }))
      return send(request.id, result({ emitted: true }))
    }
    if (name === 'stall_tree') {
      const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { windowsHide: true, stdio: 'ignore' })
      await new Promise(resolve => worker.once('spawn', resolve))
      stalled = true
      setInterval(() => {}, 1000)
      return send(request.id, result({ workerId: worker.pid, parentId: process.pid }))
    }
    if (name === 'ask') return send(request.id, result(await ask(context)))
    if (name === 'run_task') {
      await ask(context)
      tasks.set('task-1', { context, running: true })
      return send(request.id, result({ taskId: 'task-1', running: true, status: 'running' }))
    }
    if (name === 'get_task' || name === 'cancel_task') {
      const task = tasks.get(args.taskId)
      const probe = await ask(task?.context)
      if (task && (args.finish || name === 'cancel_task')) task.running = false
      return send(request.id, result({ taskId: args.taskId, running: task?.running ?? false, status: task?.running ? 'running' : 'completed', modelStatus: probe.status }))
    }
    if (name === 'list_tasks') return send(request.id, result({ tasks: [...tasks].map(([taskId, task]) => ({ taskId, running: task.running, status: task.running ? 'running' : 'completed' })) }))
    if (name === 'screenshot') return send(request.id, {
      content: [{ type: 'image', mimeType: 'image/png', data: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAACXBIWXMAAAPoAAAD6AG1e1JrAAAADElEQVQImWNgZGIGAAAOAAeCcsnOAAAAAElFTkSuQmCC' }],
    })
    return send(request.id, result({ name, args, hasModelContext: context !== undefined, workspace: meta?.['cortex/workspace'] ?? null, pid: process.pid, lateResponseCalls }))
  } catch (error) {
    process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, error: { code: -32603, message: String(error) } })}\n`)
  }
})
lines.on('close', async () => { if (stalled) return; await writeFile(process.argv[2], 'closed'); process.exit(0) })
