/** Native diagnostic retention does not expose bridge credentials or grow without bounds. */
import { expect, it } from 'vitest'
import { BackendDiagnostics } from '../src/backend-diagnostics.ts'

it('redacts credentials split across chunks and completes an unterminated final line', () => {
  const diagnostics = new BackendDiagnostics(['private-bridge-capability'])
  diagnostics.append(Buffer.from('fatal private-bridge-'))
  expect(diagnostics.text()).toBe('')
  diagnostics.append(Buffer.from('capability bearer another-secret token=opaque password="hidden" https://user:pass@localhost/x?auth=private\nfinal disk failure'))
  diagnostics.finish()
  expect(diagnostics.text()).toBe('fatal [redacted] bearer [redacted] token=[redacted] password="[redacted]" https://[redacted]@localhost/x?[redacted]\nfinal disk failure')
})

it('discards oversized incomplete lines and keeps only a bounded recent diagnostic tail', () => {
  const diagnostics = new BackendDiagnostics(['secret-at-line-end'])
  diagnostics.append(Buffer.from('x'.repeat(100000) + 'secret-at-line-end\n'))
  expect(diagnostics.text()).toBe('[Oversized native diagnostic line omitted.]')
  for (let index = 0; index < 1000; index += 1) diagnostics.append(Buffer.from(`operation ${index}: failed\n`))
  expect(Buffer.byteLength(diagnostics.text())).toBeLessThanOrEqual(4096)
  expect(diagnostics.text()).toContain('operation 999: failed')
  expect(diagnostics.text()).not.toContain('operation 0: failed')
})

it('retains UTF-8 diagnostics and redacts a framing error before attaching the stderr tail', () => {
  const diagnostics = new BackendDiagnostics(['bridge-secret'])
  const line = Buffer.from('native failed: café\n')
  diagnostics.append(line.subarray(0, line.length - 2))
  diagnostics.append(line.subarray(line.length - 2))
  expect(diagnostics.error(new Error('Invalid message bridge-secret')).message).toBe('Invalid message [redacted]\nnative failed: café')
  expect(diagnostics.error(new Error('x'.repeat(100000))).message).not.toContain('xxx')
  expect(diagnostics.error(new Error('Received a response for an unknown message ID: {"screenshot":"private-evidence"}')).message)
    .toBe('Received a response for an unknown message ID (payload omitted).\nnative failed: café')
})
