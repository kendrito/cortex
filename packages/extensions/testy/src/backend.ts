/** Owned stdio connection to the packaged Windows Testy engine. */
import { Client } from '@modelcontextprotocol/client'
import { scrubbedParentEnv } from '@cortex/subprocess'
import { z } from 'zod'
import type { JsonValue, TestyToolResult } from './types.ts'
import { TestyStdioTransport } from './backend-transport.ts'
import { BackendDiagnostics } from './backend-diagnostics.ts'

const resultSchema = z.object({
  content: z.array(z.json()),
  structuredContent: z.json().optional(),
  isError: z.boolean().optional(),
})

/** A backend process specification whose credentials belong only to the local bridge. */
export interface BackendOptions {
  executable: string
  args: string[]
  workspace: string
  bridgeUrl: string
  bridgeToken: string
  timeoutMs: number
  maxMessageBytes: number
  shutdownGraceMs: number
  disconnected: (error?: Error) => void
  diagnostic: (error: Error) => void
}

/** SDK-validated MCP tool catalog returned by the local engine. */
export type BackendTool = Awaited<ReturnType<Client['listTools']>>['tools'][number]

/** Single child process shared by the Testy pane and chat tools. */
export class TestyBackend {
  private readonly client = new Client({ name: 'Cortex Testy', version: '0.2.0-rc.2' }, { capabilities: {} })
  private readonly transport: TestyStdioTransport
  private closing = false
  private disposal: Promise<void> | undefined
  private faulted = false

  constructor(private readonly options: BackendOptions) {
    const diagnostics = new BackendDiagnostics([options.bridgeToken])
    const disconnected = (error: Error): void => {
      if (this.closing || this.faulted) return
      this.faulted = true
      options.disconnected(diagnostics.error(error))
    }
    this.transport = new TestyStdioTransport({
      command: options.executable,
      args: [...options.args, 'mcp', '--workspace', options.workspace],
      cwd: options.workspace,
      env: {
        ...scrubbedParentEnv(),
        TESTY_CORTEX_INTEGRATED: '1',
        TESTY_CORTEX_BRIDGE_URL: options.bridgeUrl,
        TESTY_CORTEX_BRIDGE_TOKEN: options.bridgeToken,
      },
      maxMessageBytes: options.maxMessageBytes, shutdownGraceMs: options.shutdownGraceMs,
      diagnosticSecrets: [options.bridgeToken],
      failed: disconnected,
    })
    this.client.onclose = () => { disconnected(new Error('The Testy engine connection closed unexpectedly.')) }
    // SDK onerror also reports late responses and notification-handler failures without closing the connection.
    this.client.onerror = (error) => {
      if (!this.closing && !this.faulted) options.diagnostic(diagnostics.error(error))
    }
  }

  /**
   * Start the owned process and negotiate the complete tool catalog.
   * @param signal - startup cancellation owned by the plugin lifecycle.
   * @returns the negotiated engine tool catalog.
   */
  async connect(signal: AbortSignal): Promise<BackendTool[]> {
    await this.client.connect(this.transport, { signal, timeout: this.options.timeoutMs })
    return (await this.client.listTools({}, { signal, timeout: this.options.timeoutMs })).tools
  }

  /**
   * Execute one schema-validated engine operation.
   * @param name - exact catalog name.
   * @param args - tool arguments.
   * @param signal - caller cancellation, tied to MCP cancellation.
   * @param contextId - host-issued model context; never an argument the model supplies.
   * @param workspace - trusted initiating chat directory supplied by the host.
   * @returns the raw MCP content and structured result.
   */
  async call(
    name: string, args: Record<string, unknown>, signal: AbortSignal, contextId?: string, workspace?: string,
  ): Promise<TestyToolResult> {
    const result = await this.client.callTool({
      name, arguments: args,
      _meta: {
        ...contextId === undefined ? {} : { 'cortex/context': contextId },
        ...workspace === undefined ? {} : { 'cortex/workspace': workspace },
      },
    }, { signal, timeout: this.options.timeoutMs })
    const parsed = resultSchema.parse(result)
    return {
      content: parsed.content,
      ...parsed.structuredContent === undefined ? {} : { structuredContent: parsed.structuredContent },
      ...parsed.isError === undefined ? {} : { isError: parsed.isError },
    }
  }

  /**
   * Read an engine-owned evidence resource using its URI policy.
   * @param uri - URI advertised by Testy.
   * @param signal - caller cancellation.
   * @returns textual or base64 resource contents with their declared MIME type.
   */
  async resource(uri: string, signal: AbortSignal): Promise<JsonValue> {
    return z.json().parse(await this.client.readResource({ uri }, { signal, timeout: this.options.timeoutMs }))
  }

  /** Disconnect MCP and terminate this connection's child process. */
  dispose(): Promise<void> {
    this.closing = true
    this.disposal ??= this.client.close()
    return this.disposal
  }
}
