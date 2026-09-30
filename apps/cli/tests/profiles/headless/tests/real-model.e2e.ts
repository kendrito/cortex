import { readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { runLoaderSmoke } from '@cortex/loader-smoke'
import { hasConfiguredTestModel } from './harness.ts'

const binScript = fileURLToPath(new URL('../../../../../../packages/test-support/loader-smoke/tests/fixtures/headless-driver.ts', import.meta.url))
const configPath = fileURLToPath(new URL('../real-model.patch.yml', import.meta.url))
const tsconfigPath = fileURLToPath(new URL('../../../../../../tsconfig.json', import.meta.url))

describe.skipIf(!hasConfiguredTestModel)('headless-agent with real model', () => {
  it('modifies a temporary workspace and verifies the file outside the agent', async () => {
    const endpoint = new URL(process.env.CORTEX_TEST_BASE_URL!)
    if (!['http:', 'https:'].includes(endpoint.protocol) || !['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname)
      || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
      throw new Error('CORTEX_TEST_BASE_URL must be a loopback HTTP(S) endpoint without credentials, query, or fragment')
    }
    let verified = ''
    const { stdout } = await runLoaderSmoke({
      label: 'headless-agent real model',
      tempDirPrefix: 'headless-agent-real-',
      binScript,
      libBinScript: binScript,
      configPath,
      binArgs: [
        configPath,
        'Read task.txt, replace its complete contents with exactly "value=after" followed by a newline, read it again, and report briefly.',
      ],
      tsconfigPath,
      processTimeoutMs: 120_000,
      prepare: cwd => writeFile(join(cwd, 'task.txt'), 'value=before\n'),
      inspect: async (cwd) => { verified = await readFile(join(cwd, 'task.txt'), 'utf8') },
    })
    expect(verified).toBe('value=after\n')
    expect(stdout.trim().length).toBeGreaterThan(0)
  }, 135_000)
})
