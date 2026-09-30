/** MCP's maintained framing with a Cortex-owned native child and awaited tree teardown. */
import { spawn } from 'node:child_process'
import type { ChildProcessWithoutNullStreams } from 'node:child_process'
import { ReadBuffer, serializeMessage } from '@modelcontextprotocol/client'
import type { JSONRPCMessage, Transport } from '@modelcontextprotocol/client'
import { stopOwnedProcessTree } from './native-process.ts'
import { BackendDiagnostics } from './backend-diagnostics.ts'

/** Process specification and deployment bounds for the owned Testy stdio transport. */
export interface BackendTransportOptions {
  command: string
  args: string[]
  cwd: string
  env: Record<string, string>
  maxMessageBytes: number
  shutdownGraceMs: number
  diagnosticSecrets: readonly string[]
  failed: (error: Error) => void
}

/** Close stdin first, then terminate the exact remaining child tree after its grace period. */
export class TestyStdioTransport implements Transport {
  onclose?: () => void
  onerror?: (error: Error) => void
  onmessage?: (message: JSONRPCMessage) => void
  private child: ChildProcessWithoutNullStreams | undefined
  private readonly readBuffer: ReadBuffer
  private readonly closed = Promise.withResolvers<undefined>()
  private disposal: Promise<void> | undefined
  private readonly diagnostics: BackendDiagnostics
  private failure: Error | undefined

  constructor(private readonly options: BackendTransportOptions) {
    this.readBuffer = new ReadBuffer({ maxBufferSize: options.maxMessageBytes })
    this.diagnostics = new BackendDiagnostics(options.diagnosticSecrets)
  }

  async start(): Promise<void> {
    if (this.child !== undefined || this.disposal !== undefined) throw new Error('The Testy child transport cannot be restarted.')
    const child = spawn(this.options.command, this.options.args, {
      cwd: this.options.cwd, env: this.options.env, windowsHide: true, stdio: 'pipe',
    })
    this.child = child
    const fail = (error: Error): void => {
      if (this.disposal !== undefined || this.failure !== undefined) return
      this.failure = this.diagnostics.error(error)
      this.options.failed(this.failure)
      this.onerror?.(this.failure)
    }
    child.once('close', (code, signal) => {
      this.diagnostics.finish()
      this.closed.resolve(undefined)
      fail(new Error(`The Testy engine exited unexpectedly (code ${String(code)}, signal ${signal ?? 'none'}).`))
      this.onclose?.()
    })
    child.stderr.on('data', (chunk: Buffer) => { this.diagnostics.append(chunk) })
    child.on('error', fail)
    child.stdin.on('error', fail)
    child.stdout.on('error', fail)
    child.stderr.on('error', fail)
    child.stdout.on('data', (chunk: Buffer) => {
      try {
        this.readBuffer.append(chunk)
        for (;;) {
          const message = this.readBuffer.readMessage()
          if (message === null) break
          this.onmessage?.(message)
        }
      } catch (error) {
        fail(error instanceof Error ? error : new Error('Invalid Testy protocol message.'))
        void this.close().catch(fail)
      }
    })
    await new Promise<void>((resolveStarted, reject) => {
      child.once('spawn', () => { child.off('error', reject); resolveStarted() })
      child.once('error', reject)
    })
  }

  send(message: JSONRPCMessage): Promise<void> {
    const child = this.child
    if (child === undefined || this.disposal !== undefined || child.stdin.destroyed) {
      return Promise.reject(new Error('The Testy child is disconnected.'))
    }
    return new Promise<void>((resolveSent, reject) => {
      child.stdin.write(serializeMessage(message), (error) => {
        if (error === null || error === undefined) resolveSent()
        else reject(error)
      })
    })
  }

  close(): Promise<void> {
    this.disposal ??= this.stop()
    return this.disposal
  }

  private async stop(): Promise<void> {
    const child = this.child
    if (child === undefined) return
    child.stdin.end()
    let timer: ReturnType<typeof setTimeout> | undefined
    try {
      await Promise.race([this.closed.promise, new Promise<void>((resolveGrace) => {
        timer = setTimeout(resolveGrace, this.options.shutdownGraceMs)
      })])
      await stopOwnedProcessTree(child)
      await this.closed.promise
    } finally {
      if (timer !== undefined) clearTimeout(timer)
      this.readBuffer.clear()
    }
  }
}
