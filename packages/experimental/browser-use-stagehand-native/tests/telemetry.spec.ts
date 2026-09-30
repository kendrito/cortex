/** Guard the pinned SDK patch: initialization cannot enable external trace export. */
import { readFile } from 'node:fs/promises'
import { runInNewContext } from 'node:vm'
import { StagehandClientCreateConfigSchema } from '@browserbasehq/stagehand'
import { expect, it } from 'vitest'

it('defaults to disabled telemetry and rejects an exporter configuration', () => {
  expect(StagehandClientCreateConfigSchema.parse({}).telemetry).toBeNull()
  expect(StagehandClientCreateConfigSchema.parse({ telemetry: null }).telemetry).toBeNull()
  expect(StagehandClientCreateConfigSchema.safeParse({
    telemetry: { traces: { endpoint: 'https://collector.invalid/v1/traces', headers: {} } },
  }).success).toBe(false)
})

it('keeps extension tracing inert through configuration, flush, and shutdown', async () => {
  const sdkEntry = import.meta.resolve('@browserbasehq/stagehand')
  const source = await readFile(new URL('./extension/service-worker.js', sdkEntry), 'utf8')
  const start = source.indexOf('function createStagehandTracing(')
  const end = source.indexOf('\nfunction createOtlpSpanProcessor(', start)
  expect(start).toBeGreaterThan(0)
  expect(end).toBeGreaterThan(start)
  const factory = source.slice(start, end)
  class NoopTracer { readonly enabled = false }
  const runtime = runInNewContext(`(${factory})()`, { NoopTracer }) as {
    tracer: unknown
    configure(config: unknown, client: unknown): Promise<void>
    forceFlush(): Promise<void>
    shutdown(): Promise<void>
  }
  expect(runtime.tracer).toBeInstanceOf(NoopTracer)
  // The isolated context deliberately exposes no collector, network, or span
  // processor factory; attempts to construct one fail this real patch test.
  await runtime.configure(null, { name: 'cortex', version: 'test' })
  await runtime.forceFlush()
  await runtime.shutdown()
})
