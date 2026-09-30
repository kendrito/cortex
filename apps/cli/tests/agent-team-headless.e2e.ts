import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { execa } from 'execa'
import { describe, expect, it } from 'vitest'
import { resolveExampleLaunch } from '@cortex/loader-smoke'

const cortexBinScript = fileURLToPath(new URL('../src/bin.ts', import.meta.url))
const tsconfigPath = fileURLToPath(new URL('../../../tsconfig.json', import.meta.url))
const fixturePlugin = pathToFileURL(fileURLToPath(
  new URL('./profiles/headless/tests/fixtures/team-llm.mjs', import.meta.url),
)).href

function records(content: string): Record<string, unknown>[] {
  return content.split('\n').filter(Boolean).map(line => JSON.parse(line) as Record<string, unknown>)
}

describe('cortex run with Agent Teams enabled', () => {
  it('runs two teammates, durable peer mail, dependent tasks, waiting, and final aggregation', async () => {
    const cwd = await mkdtemp(join(tmpdir(), 'cortex-agent-team-headless-'))
    try {
      const home = join(cwd, '.cortex')
      const sessions = join(home, 'sessions')
      const profileDir = join(home, 'profiles', 'headless')
      await mkdir(profileDir, { recursive: true })
      await writeFile(join(profileDir, 'package.json'), JSON.stringify({
        name: 'cortex-profile-headless',
        private: true,
        dependencies: {
          '@cortex/experimental-agent-team-profile': 'workspace:^',
        },
        cortex: {
          profile: {
            bundles: [
              '@cortex/base',
              '@cortex/headless',
              '@cortex/experimental-agent-team-profile',
            ],
          },
        },
      }, undefined, 2) + '\n')
      await writeFile(join(profileDir, 'cordis.patch.yml'), [
        '- id: llm-deepseek',
        '  disabled: true',
        '- id: session-persistence-jsonl',
        '  config:',
        `    root: '${sessions}'`,
        '    compression: none',
        '- insert:',
        '    - id: team-fixture-llm',
        `      name: '${fixturePlugin}'`,
        '',
      ].join('\n'))
      const launch = resolveExampleLaunch({
        srcBin: cortexBinScript,
        configArgs: ['--profile', 'headless', '请先运行 workflow 检查，再使用 Agent Teams 把调研和实现拆给两个 teammate，等待完成后汇总。'],
        tsconfigPath,
        env: {
          CORTEX_HOME: home,
          CORTEX_AGENTS_HOME: join(cwd, '.agents'),
          CORTEX_TELEMETRY_DISABLED: '1',
          DEEPSEEK_API_KEY: '',
          NODE_OPTIONS: [
            process.env.NODE_OPTIONS,
            '--disable-warning=ExperimentalWarning',
            '--disable-warning=MODULE_TYPELESS_PACKAGE_JSON',
          ].filter(Boolean).join(' '),
        },
      })
      const result = await execa(launch.command, launch.args, {
        cwd,
        env: launch.env,
        input: '',
        timeout: 90_000,
        killSignal: 'SIGKILL',
        reject: false,
      })
      expect(
        result.exitCode,
        `cortex headless profile exited unexpectedly.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
      ).toBe(0)
      expect(result.stderr).toBe('')
      expect(result.stdout).toContain('TEAM_WORKFLOW_OK')

      const files = (await readdir(sessions, { recursive: true }))
        .filter(file => file.endsWith('.jsonl'))
      expect(files).toHaveLength(4)
      const logs = await Promise.all(files.map(file => readFile(join(sessions, file), 'utf8')))
      const parsed = logs.map(records)
      const workflowChild = parsed.find(log => log.some(record => record.type === 'subagent/descriptor'
        && (record.data as { mode: string }).mode === 'one-shot'))
      expect(workflowChild).toBeDefined()
      expect(workflowChild!.find(record => record.type === 'subagent/descriptor')?.data)
        .toMatchObject({ mode: 'one-shot', provider: 'spawn' })
      expect(workflowChild!.filter(record => record.type === 'user/message'
        && (record.data as { source: { kind: string } }).source.kind === 'user').map(record => record.data))
        .toEqual([expect.objectContaining({ content: [{ type: 'text', text: 'TEAM_WORKFLOW_CHILD' }] })])
      const root = parsed.find((log) => {
        const header = log[0]
        return header?.type === 'session' && typeof header.parentSession !== 'string'
      })
      expect(root).toBeDefined()
      const eventTypes = root!.map(record => record.type)
      expect(eventTypes.filter(type => type === 'team/member')).toHaveLength(4)
      expect(eventTypes).toContain('team/message/queued')
      expect(eventTypes).toContain('team/message/delivered')
      const taskEvents = root!.filter(record => record.type === 'team/task')
      expect(taskEvents.filter((record) => {
        const data = record.data as { task?: { status?: string } } | undefined
        return data?.task?.status === 'completed'
      })).toHaveLength(2)
      const toolNames = root!.filter(record => record.type === 'tool/call')
        .map(record => (record.data as { name?: string } | undefined)?.name)
      expect(toolNames).toContain('wait_agent')
      expect(toolNames).toContain('team_task_list')
      expect(toolNames).toContain('list_agents')
      expect(toolNames).toContain('workflow')
      expect(root!.find(record => record.type === 'tool-workflow/run-end')?.data)
        .toMatchObject({ stopReason: 'completed' })
    } finally {
      await rm(cwd, { recursive: true, force: true })
    }
  }, 105_000)
})
