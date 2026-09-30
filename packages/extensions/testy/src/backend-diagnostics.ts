/** Bounded, credential-redacted diagnostics retained only for an owned engine failure. */
import { StringDecoder } from 'node:string_decoder'

// Security bounds apply before retaining native text and after redaction.
const maxLineCharacters = 8192
const maxDiagnosticBytes = 4096

/** Retain complete diagnostic lines without allowing an oversized line to expose a partial credential. */
export class BackendDiagnostics {
  private readonly decoder = new StringDecoder('utf8')
  private pending = ''
  private discarding = false
  private lines: string[] = []

  constructor(private readonly secrets: readonly string[]) {}

  /**
   * Consume one native stderr chunk; incomplete lines stay private until complete.
   * @param chunk - bytes read from the owned child stderr pipe.
   */
  append(chunk: Buffer): void { this.consume(this.decoder.write(chunk)) }

  /** Complete the final unterminated native diagnostic line. */
  finish(): void {
    this.consume(this.decoder.end())
    if (this.pending.length > 0 || this.discarding) this.completeLine()
  }

  /**
   * Return retained sanitized diagnostics without exposing an incomplete line.
   * @returns at most 4096 bytes of complete sanitized lines.
   */
  text(): string { return this.lines.join('\n') }

  /**
   * Sanitize a process or framing error before it reaches logs or the authenticated UI.
   * @param error - native process, stream, or MCP error.
   * @returns bounded redacted error with any retained stderr tail.
   */
  error(error: Error): Error {
    const raw = error.message.replace(/^(Received a response for an unknown message ID|Unknown message type):[\s\S]*$/, '$1 (payload omitted).')
    const sanitized = raw.length > maxLineCharacters ? 'An oversized engine diagnostic was omitted.' : this.sanitize(raw)
    const message = Buffer.byteLength(sanitized) > maxDiagnosticBytes ? 'An oversized engine diagnostic was omitted.' : sanitized
    const detail = this.text()
    return new Error(detail.length === 0 ? message : `${message}\n${detail}`)
  }

  private consume(text: string): void {
    for (const character of text) {
      if (character === '\n') { this.completeLine(); continue }
      if (this.discarding) continue
      if (this.pending.length >= maxLineCharacters) { this.pending = ''; this.discarding = true; continue }
      this.pending += character
    }
  }

  private completeLine(): void {
    const line = this.discarding ? '[Oversized native diagnostic line omitted.]' : this.sanitize(this.pending)
    this.pending = ''
    this.discarding = false
    if (line.length === 0) return
    this.lines.push(line)
    while (Buffer.byteLength(this.lines.join('\n')) > maxDiagnosticBytes && this.lines.length > 1) this.lines.shift()
    if (Buffer.byteLength(this.lines[0] ?? '') > maxDiagnosticBytes) this.lines = ['[Oversized native diagnostic line omitted.]']
  }

  private sanitize(text: string): string {
    let value = text
    for (const secret of this.secrets) if (secret.length > 0) value = value.replaceAll(secret, '[redacted]')
    return value
      .replace(/(bearer\s+)[^\s"'<>]+/gi, '$1[redacted]')
      .replace(/((?:api[_-]?key|authorization|password|secret|token|cortex[_/-]?context)["']?\s*[:=]\s*["']?)[^\s,;"'}]+/gi, '$1[redacted]')
      .replace(/(https?:\/\/)[^\s/@]+:[^\s/@]+@/gi, '$1[redacted]@')
      .replace(/(https?:\/\/[^\s?#]+)\?[^\s"'<>]*/gi, '$1?[redacted]')
      .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '')
      .trim()
  }
}
